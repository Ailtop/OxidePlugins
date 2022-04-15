﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Building Grades", "Default/Arainrr", "1.0.5")]
    [Description("Allows players to easily upgrade or downgrade an entire building")]
    public class BuildingGrades : RustPlugin
    {
        #region Fields

        [PluginReference] private readonly Plugin Friends, Clans, NoEscape, RustTranslationAPI;

        private const string PermissionUp = "buildinggrades.up.";
        private const string PermissionDown = "buildinggrades.down.";

        private const string PermissionUse = "buildinggrades.use";
        private const string PermissionUpAll = PermissionUp + "all";
        private const string PermissionDownAll = PermissionDown + "all";
        private const string PermissionNoCost = "buildinggrades.nocost";
        private const string PermissionAdmin = "buildinggrades.admin";

        private static BuildingGrades _instance;

        private HashSet<ulong> _blockedPlayers;
        private readonly Dictionary<ulong, float> _cooldowns = new Dictionary<ulong, float>();
        private readonly Dictionary<string, HashSet<uint>> _categories = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<BuildingGrade.Enum> ValidGrades = new List<BuildingGrade.Enum>
        {
            BuildingGrade.Enum.Twigs, BuildingGrade.Enum.Wood, BuildingGrade.Enum.Stone, BuildingGrade.Enum.Metal, BuildingGrade.Enum.TopTier
        };

        #endregion Fields

        #region Oxide Hooks

        private void Init()
        {
            _instance = this;
            foreach (var kvp in configData.Permissions)
            {
                if (!permission.PermissionExists(kvp.Key, this))
                {
                    permission.RegisterPermission(kvp.Key, this);
                }
            }
            permission.RegisterPermission(PermissionUpAll, this);
            permission.RegisterPermission(PermissionDownAll, this);
            permission.RegisterPermission(PermissionNoCost, this);
            permission.RegisterPermission(PermissionAdmin, this);

            if (!permission.PermissionExists(PermissionUse, this))
            {
                permission.RegisterPermission(PermissionUse, this);
            }

            foreach (var validGrade in ValidGrades)
            {
                if (validGrade > BuildingGrade.Enum.Twigs)
                {
                    permission.RegisterPermission(PermissionUp + (int)validGrade, this);
                }

                if (validGrade < BuildingGrade.Enum.TopTier)
                {
                    permission.RegisterPermission(PermissionDown + (int)validGrade, this);
                }
            }
            cmd.AddChatCommand(configData.Chat.UpgradeCommand, this, nameof(CmdUpgrade));
            cmd.AddChatCommand(configData.Chat.DowngradeCommand, this, nameof(CmdDowngrade));
            cmd.AddChatCommand(configData.Chat.UpgradeAllCommand, this, nameof(CmdUpgradeAll));
            cmd.AddChatCommand(configData.Chat.DowngradeAllCommand, this, nameof(CmdDowngradeAll));

            if (configData.Global.UseCombatBlocker || configData.Global.UseRaidBlocker)
            {
                _blockedPlayers = new HashSet<ulong>();
            }
            if (!configData.Global.UseRaidBlocker)
            {
                Unsubscribe(nameof(OnRaidBlock));
            }
            if (!configData.Global.UseCombatBlocker)
            {
                Unsubscribe(nameof(OnCombatBlock));
            }
        }

        private void OnServerInitialized()
        {
            if (_isUpgradeBlockedMethod == null)
            {
                PrintError("IsUpgradeBlocked == null. Please notify the plugin developer");
            }
            foreach (var entry in configData.Categories)
            {
                var values = new HashSet<uint>();
                foreach (var prefab in entry.Value)
                {
                    var item = StringPool.Get(prefab);
                    if (item == 0u) continue;
                    values.Add(item);
                }
                _categories.Add(entry.Key, values);
            }
        }

        private void Unload()
        {
            if (_changeGradeCoroutine != null)
            {
                ServerMgr.Instance.StopCoroutine(_changeGradeCoroutine);
            }
            _instance = null;
        }

        private void OnRaidBlock(BasePlayer player)
        {
            OnCombatBlock(player);
        }

        private void OnCombatBlock(BasePlayer player)
        {
            if (_changeGradeCoroutine != null)
            {
                _blockedPlayers?.Add(player.userID);
            }
        }

        #endregion Oxide Hooks

        #region Chat Commands

        private void CmdUpgrade(BasePlayer player, string command, string[] args)
        {
            HandleCommand(player, args, true, false);
        }

        private void CmdDowngrade(BasePlayer player, string command, string[] args)
        {
            HandleCommand(player, args, false, false);
        }

        private void CmdUpgradeAll(BasePlayer player, string command, string[] args)
        {
            HandleCommand(player, args, true, true);
        }

        private void CmdDowngradeAll(BasePlayer player, string command, string[] args)
        {
            HandleCommand(player, args, false, true);
        }

        #endregion Chat Commands

        #region Methods

        private void HandleCommand(BasePlayer player, string[] args, bool isUpgrade, bool isAll)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermissionUse))
            {
                Print(player, Lang("NotAllowed", player.UserIDString));
                return;
            }
            if (_changeGradeCoroutine != null)
            {
                Print(player, Lang("AlreadyProcess", player.UserIDString));
                return;
            }

            if (IsPlayerBlocked(player))
            {
                return;
            }

            HashSet<uint> filter = null;
            var targetGrade = BuildingGrade.Enum.None;
            if (args.Length > 0)
            {
                if (!_categories.TryGetValue(args[0], out filter))
                {
                    if (!Enum.TryParse(args[0], true, out targetGrade) || !ValidGrades.Contains(targetGrade) ||
                        isUpgrade ? targetGrade <= BuildingGrade.Enum.Twigs : targetGrade >= BuildingGrade.Enum.TopTier)
                    {
                        Print(player, Lang("UnknownGrade", player.UserIDString));
                        return;
                    }

                    if (!HasGradePermission(player, targetGrade, isUpgrade))
                    {
                        Print(player, Lang("NotAllowed", player.UserIDString));
                        return;
                    }

                    if (args.Length > 1 && !_categories.TryGetValue(args[1], out filter))
                    {
                        Print(player, Lang("UnknownCategory", player.UserIDString, string.Join(", ", configData.Categories.Keys)));
                        return;
                    }
                }
            }
            else
            {
                FindPlayerGrantedGrades(player, isUpgrade);
                if (_tempGrantedGrades.Count <= 0)
                {
                    Print(player, Lang("NotAllowed", player.UserIDString));
                    return;
                }
            }

            var permissionSettings = GetPermissionSettings(player);
            if (permissionSettings.Cooldown > 0f && !(configData.Global.CooldownExclude && player.IsAdmin))
            {
                float lastUse;
                if (_cooldowns.TryGetValue(player.userID, out lastUse))
                {
                    var timeLeft = permissionSettings.Cooldown - (Time.realtimeSinceStartup - lastUse);
                    if (timeLeft > 0f)
                    {
                        Print(player, Lang("OnCooldown", player.UserIDString, Mathf.CeilToInt(timeLeft)));
                        return;
                    }
                }
            }

            var targetBuildingBlock = GetBuildingBlockLookingAt(player, permissionSettings);
            if (targetBuildingBlock == null)
            {
                Print(player, Lang("NotLookingAt", player.UserIDString));
                return;
            }

            var isAdmin = permission.UserHasPermission(player.UserIDString, PermissionAdmin);
            if (!isAdmin && player.IsBuildingBlocked(targetBuildingBlock.WorldSpaceBounds()))
            {
                Print(player, Lang("BuildingBlocked", player.UserIDString));
                return;
            }

            _changeGradeCoroutine = ServerMgr.Instance.StartCoroutine(StartChangeBuildingGrade(targetBuildingBlock, player, targetGrade, filter, permissionSettings, isUpgrade, isAll, isAdmin));
        }

        private PermissionSettings GetPermissionSettings(BasePlayer player)
        {
            int priority = 0;
            PermissionSettings permissionSettings = null;
            foreach (var entry in configData.Permissions)
            {
                if (entry.Value.Priority >= priority && permission.UserHasPermission(player.UserIDString, entry.Key))
                {
                    priority = entry.Value.Priority;
                    permissionSettings = entry.Value;
                }
            }
            return permissionSettings ?? new PermissionSettings();
        }

        private bool HasGradePermission(BasePlayer player, BuildingGrade.Enum grade, bool isUpgrade)
        {
            return isUpgrade
                ? permission.UserHasPermission(player.UserIDString, PermissionUpAll) ||
                  permission.UserHasPermission(player.UserIDString, PermissionUp + (int)grade)
                : permission.UserHasPermission(player.UserIDString, PermissionDownAll) ||
                  permission.UserHasPermission(player.UserIDString, PermissionDown + (int)grade);
        }

        #region AreFriends

        private bool AreFriends(ulong playerId, ulong friendId)
        {
            if (!playerId.IsSteamId()) return false;
            if (playerId == friendId) return true;
            if (configData.Global.UseTeams && SameTeam(playerId, friendId)) return true;
            if (configData.Global.UseFriends && HasFriend(playerId, friendId)) return true;
            if (configData.Global.UseClans && SameClan(playerId, friendId)) return true;
            return false;
        }

        private static bool SameTeam(ulong playerId, ulong friendId)
        {
            if (!RelationshipManager.TeamsEnabled()) return false;
            var playerTeam = RelationshipManager.ServerInstance.FindPlayersTeam(playerId);
            if (playerTeam == null) return false;
            var friendTeam = RelationshipManager.ServerInstance.FindPlayersTeam(friendId);
            if (friendTeam == null) return false;
            return playerTeam == friendTeam;
        }

        private bool HasFriend(ulong playerId, ulong friendId)
        {
            if (Friends == null) return false;
            return (bool)Friends.Call("HasFriend", playerId, friendId);
        }

        private bool SameClan(ulong playerId, ulong friendId)
        {
            if (Clans == null) return false;
            //Clans
            var isMember = Clans.Call("IsClanMember", playerId.ToString(), friendId.ToString());
            if (isMember != null) return (bool)isMember;
            //Rust:IO Clans
            var playerClan = Clans.Call("GetClanOf", playerId);
            if (playerClan == null) return false;
            var friendClan = Clans.Call("GetClanOf", friendId);
            if (friendClan == null) return false;
            return (string)playerClan == (string)friendClan;
        }

        #endregion AreFriends

        #region PlayerIsBlocked

        private bool IsPlayerBlocked(BasePlayer player)
        {
            if (NoEscape == null) return false;
            if (configData.Global.UseRaidBlocker && IsRaidBlocked(player.UserIDString))
            {
                Print(player, Lang("RaidBlocked", player.UserIDString));
                return true;
            }
            if (configData.Global.UseCombatBlocker && IsCombatBlocked(player.UserIDString))
            {
                Print(player, Lang("CombatBlocked", player.UserIDString));
                return true;
            }
            return false;
        }

        private bool IsRaidBlocked(string playerId) => (bool)NoEscape.Call("IsRaidBlocked", playerId);

        private bool IsCombatBlocked(string playerId) => (bool)NoEscape.Call("IsCombatBlocked", playerId);

        #endregion PlayerIsBlocked

        #region RustTranslationAPI

        private string GetItemTranslationByShortName(string language, string itemShortName) => (string)RustTranslationAPI.Call("GetItemTranslationByShortName", language, itemShortName);

        private string GetItemDisplayName(string language, ItemDefinition itemDefinition)
        {
            if (RustTranslationAPI != null)
            {
                var displayName = GetItemTranslationByShortName(language, itemDefinition.shortname);
                if (!string.IsNullOrEmpty(displayName))
                {
                    return displayName;
                }
            }
            return itemDefinition.displayName.english;
        }

        #endregion RustTranslationAPI

        #endregion Methods

        #region Building Grade Control

        private readonly MethodInfo _isUpgradeBlockedMethod = typeof(BuildingBlock).GetMethod("IsUpgradeBlocked", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private Coroutine _changeGradeCoroutine;
        private readonly HashSet<BuildingBlock> _allBuildingBlocks = new HashSet<BuildingBlock>();
        private readonly Dictionary<ulong, bool> _tempFriends = new Dictionary<ulong, bool>();
        private readonly List<BuildingGrade.Enum> _tempGrantedGrades = new List<BuildingGrade.Enum>();

        private readonly List<Item> _collect = new List<Item>();
        private readonly Hash<int, float> _takeOutItems = new Hash<int, float>();
        private readonly Hash<ItemDefinition, int> _missingDictionary = new Hash<ItemDefinition, int>();

        public IEnumerator StartChangeBuildingGrade(BuildingBlock sourceEntity, BasePlayer player, BuildingGrade.Enum targetGrade, HashSet<uint> filter, PermissionSettings permissionSettings, bool isUpgrade, bool isAll, bool isAdmin)
        {
            _blockedPlayers?.Clear();
            yield return GetAllBuildingBlocks(sourceEntity, filter, isAll);
            //if (pay) {
            //    FindUpgradeCosts(targetGrade);
            //    if (!CanAffordUpgrade(player)) {
            //        Clear();
            //        yield break;
            //    }
            //    PayForUpgrade(costs, player);
            //}

            Print(player, Lang(isUpgrade ? "StartUpgrade" : "StartDowngrade", player.UserIDString));
            var playerId = player.userID;
            yield return isUpgrade ? UpgradeBuildingBlocks(player, targetGrade, permissionSettings, isAdmin) : DowngradeBuildingBlocks(player, targetGrade, permissionSettings, isAdmin);

            _tempFriends.Clear();
            _allBuildingBlocks.Clear();
            _tempGrantedGrades.Clear();
            _blockedPlayers?.Clear();
            _cooldowns[playerId] = Time.realtimeSinceStartup;
            _changeGradeCoroutine = null;
        }

        private void FindPlayerGrantedGrades(BasePlayer player, bool isUpgrade)
        {
            var all = permission.UserHasPermission(player.UserIDString, isUpgrade ? PermissionUpAll : PermissionDownAll);
            if (all)
            {
                _tempGrantedGrades.AddRange(ValidGrades);
                return;
            }
            foreach (var validGrade in ValidGrades)
            {
                if (isUpgrade)
                {
                    if (validGrade > BuildingGrade.Enum.Twigs && permission.UserHasPermission(player.UserIDString, PermissionUp + (int)validGrade))
                    {
                        _tempGrantedGrades.Add(validGrade);
                    }
                }
                else
                {
                    if (validGrade < BuildingGrade.Enum.TopTier && permission.UserHasPermission(player.UserIDString, PermissionDown + (int)validGrade))
                    {
                        _tempGrantedGrades.Add(validGrade);
                    }
                }
            }
        }

        private bool ShouldInterrupt(ulong playerId)
        {
            return _blockedPlayers != null && _blockedPlayers.Contains(playerId);
        }

        #region Upgrade

        private IEnumerator UpgradeBuildingBlocks(BasePlayer player, BuildingGrade.Enum targetGrade, PermissionSettings permissionSettings, bool isAdmin)
        {
            var pay = permissionSettings.Pay && !permission.UserHasPermission(player.UserIDString, PermissionNoCost);
            int current = 0, success = 0;
            var autoGrade = targetGrade == BuildingGrade.Enum.None;
            foreach (var buildingBlock in _allBuildingBlocks)
            {
                if (buildingBlock == null || buildingBlock.IsDestroyed)
                {
                    continue;
                }
                if (ShouldInterrupt(player.userID))
                {
                    break;
                }
                BuildingGrade.Enum grade = targetGrade;
                if (CheckBuildingGrade(buildingBlock, true, ref grade))
                {
                    if (!autoGrade || _tempGrantedGrades.Contains(grade))
                    {
                        if (TryUpgradeToGrade(buildingBlock, player, grade, pay, isAdmin))
                        {
                            success++;
                        }
                    }
                }

                if (++current % configData.Global.PerFrame == 0)
                {
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            foreach (var item in _collect)
            {
                _takeOutItems[item.info.itemid] += item.amount;
                item.Remove();
            }
            foreach (var entry in _takeOutItems)
            {
                player.Command("note.inv " + entry.Key + " " + entry.Value * -1f);
            }

            if (player != null && player.IsConnected)
            {
                if (_missingDictionary.Count > 0)
                {
                    StringBuilder stringBuilder = Pool.Get<StringBuilder>();
                    var language = lang.GetLanguage(player.UserIDString);
                    foreach (var entry in _missingDictionary)
                    {
                        stringBuilder.AppendLine(Lang("MissingItemsFormat", player.UserIDString, GetItemDisplayName(language, entry.Key), entry.Value));
                    }
                    var missingResources = stringBuilder.ToString();
                    stringBuilder.Clear();
                    Pool.Free(ref stringBuilder);
                    Print(player, success > 0 ? Lang("UpgradeNotEnoughItemsSuccess", player.UserIDString, success, missingResources) : Lang("UpgradeNotEnoughItems", player.UserIDString, missingResources));
                }
                else
                {
                    Print(player, success > 0 ? Lang("FinishedUpgrade", player.UserIDString, success) : Lang("NotUpgraded", player.UserIDString));
                }
            }

            _collect.Clear();
            _takeOutItems.Clear();
            _missingDictionary.Clear();
        }

        private bool TryUpgradeToGrade(BuildingBlock buildingBlock, BasePlayer player, BuildingGrade.Enum targetGrade, bool pay, bool isAdmin)
        {
            if (!CanUpgrade(buildingBlock, player, targetGrade, pay, isAdmin))
            {
                return false;
            }
            SetBuildingBlockGrade(buildingBlock, targetGrade);
            return true;
        }

        private bool CanUpgrade(BuildingBlock buildingBlock, BasePlayer player, BuildingGrade.Enum targetGrade, bool pay, bool isAdmin)
        {
            if (player == null || !player.CanInteract())
            {
                return false;
            }
            var constructionGrade = buildingBlock.GetGrade(targetGrade);
            if (constructionGrade == null)
            {
                return false;
            }
            if (!isAdmin)
            {
                if (Interface.Oxide.CallHook("OnStructureUpgrade", buildingBlock, player, targetGrade) != null)
                {
                    return false;
                }
                if (buildingBlock.SecondsSinceAttacked < 30f)
                {
                    return false;
                }
                if (!buildingBlock.CanChangeToGrade(targetGrade, player))
                {
                    return false;
                }

                if (!HasAccess(player, buildingBlock))
                {
                    return false;
                }
            }
            if (pay)
            {
                //if (!buildingBlock.CanAffordUpgrade(targetGrade, player)) {
                //    return false;
                //}
                //buildingBlock.PayForUpgrade(constructionGrade, player);
                if (!CanAffordUpgrade(buildingBlock, constructionGrade, player, targetGrade))
                {
                    return false;
                }
                PayForUpgrade(buildingBlock, constructionGrade, player);
            }
            return true;
        }

        public bool CanAffordUpgrade(BuildingBlock buildingBlock, ConstructionGrade constructionGrade, BasePlayer player, BuildingGrade.Enum grade)
        {
            object obj = Interface.CallHook("CanAffordUpgrade", player, buildingBlock, grade);
            if (obj is bool)
            {
                return (bool)obj;
            }

            bool flag = true;
            foreach (var item in constructionGrade.costToBuild)
            {
                var missingAmount = item.amount - player.inventory.GetAmount(item.itemid);
                if (missingAmount > 0f)
                {
                    flag = false;
                    _missingDictionary[item.itemDef] += (int)missingAmount;
                }
            }
            return flag;
        }

        public void PayForUpgrade(BuildingBlock buildingBlock, ConstructionGrade constructionGrade, BasePlayer player)
        {
            if (Interface.CallHook("OnPayForUpgrade", player, buildingBlock, constructionGrade) != null)
            {
                return;
            }
            foreach (var item in constructionGrade.costToBuild)
            {
                player.inventory.Take(_collect, item.itemid, (int)item.amount);
                //player.Command("note.inv " + item.itemid + " " + item.amount * -1f);
            }
        }

        #endregion Upgrade

        #region Downgrade

        private IEnumerator DowngradeBuildingBlocks(BasePlayer player, BuildingGrade.Enum targetGrade, PermissionSettings permissionSettings, bool isAdmin)
        {
            int current = 0, success = 0;
            var autoGrade = targetGrade == BuildingGrade.Enum.None;
            foreach (var buildingBlock in _allBuildingBlocks)
            {
                if (buildingBlock == null || buildingBlock.IsDestroyed)
                {
                    continue;
                }
                if (ShouldInterrupt(player.userID))
                {
                    break;
                }
                BuildingGrade.Enum grade = targetGrade;
                if (CheckBuildingGrade(buildingBlock, false, ref grade))
                {
                    if (!autoGrade || _tempGrantedGrades.Contains(grade))
                    {
                        if (TryDowngradeToGrade(buildingBlock, player, grade/*, permissionSettings.Refund*/, isAdmin: isAdmin))
                        {
                            success++;
                        }
                    }
                }
                if (current++ % configData.Global.PerFrame == 0)
                {
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            if (player != null && player.IsConnected)
            {
                Print(player, success > 0 ? Lang("FinishedDowngrade", player.UserIDString, success) : Lang("NotDowngraded", player.UserIDString));
            }
        }

        private bool TryDowngradeToGrade(BuildingBlock buildingBlock, BasePlayer player, BuildingGrade.Enum targetGrade, bool refund = false, bool isAdmin = false)
        {
            if (!CanDowngrade(buildingBlock, player, targetGrade, refund, isAdmin))
            {
                return false;
            }

            //if (refund)
            //{
            //    foreach (var itemAmount in buildingBlock.currentGrade.costToBuild)
            //    {
            //        var item = ItemManager.CreateByItemID(itemAmount.itemid, (int)itemAmount.amount);
            //        player.GiveItem(item);
            //    }
            //}
            SetBuildingBlockGrade(buildingBlock, targetGrade);
            return true;
        }

        private bool CanDowngrade(BuildingBlock buildingBlock, BasePlayer player, BuildingGrade.Enum targetGrade, bool refund, bool isAdmin)
        {
            if (player == null || !player.CanInteract())
            {
                return false;
            }

            var constructionGrade = buildingBlock.GetGrade(targetGrade);
            if (constructionGrade == null)
            {
                return false;
            }

            if (!isAdmin)
            {
                if (Interface.CallHook("OnStructureUpgrade", buildingBlock, player, targetGrade) != null)
                {
                    return false;
                }
                if (buildingBlock.SecondsSinceAttacked < 30f)
                {
                    return false;
                }
                var obj = Interface.CallHook("CanChangeGrade", player, buildingBlock, targetGrade);
                if (obj is bool)
                {
                    return (bool)obj;
                }
                if (player.IsBuildingBlocked(buildingBlock.transform.position, buildingBlock.transform.rotation, buildingBlock.bounds))
                {
                    return false;
                }
                var isUpgradeBlocked = _isUpgradeBlockedMethod?.Invoke(buildingBlock, null);
                if (isUpgradeBlocked is bool && (bool)isUpgradeBlocked)
                {
                    return false;
                }
                if (!HasAccess(player, buildingBlock))
                {
                    return false;
                }
            }

            return true;
        }

        #endregion Downgrade

        #region Methods

        private bool HasAccess(BasePlayer player, BuildingBlock buildingBlock)
        {
            bool flag;
            if (_tempFriends.TryGetValue(buildingBlock.OwnerID, out flag))
            {
                return flag;
            }
            var areFriends = AreFriends(buildingBlock.OwnerID, player.userID);
            _tempFriends.Add(buildingBlock.OwnerID, areFriends);
            return areFriends;
        }

        private IEnumerator GetAllBuildingBlocks(BuildingBlock sourceEntity, HashSet<uint> filter, bool isAll)
        {
            Func<BuildingBlock, bool> func = x => filter == null || filter.Contains(x.prefabID);
            if (isAll)
            {
                yield return GetNearbyEntities(sourceEntity, _allBuildingBlocks, Rust.Layers.Mask.Construction, func);
            }
            else
            {
                var building = sourceEntity.GetBuilding();
                if (building != null)
                {
                    foreach (var buildingBlock in building.buildingBlocks)
                    {
                        if (func(buildingBlock))
                        {
                            _allBuildingBlocks.Add(buildingBlock);
                        }
                    }
                }
            }
        }

        //#region TryPay

        //private readonly Hash<int, int> costs = new Hash<int, int>();
        //private readonly Hash<string , int> missingDictionary = new Hash<string, int>();
        //private readonly HashSet<BuildingBlock> toRemove = new HashSet<BuildingBlock>();

        //private bool TryPay(BasePlayer player, BuildingGrade.Enum targetGrade, out string missingResources) {
        //    FindUpgradeCosts(player, targetGrade);
        //    if (!CanPay(player, out missingResources)) {
        //        return false;
        //    }

        //    var collect = Pool.GetList<Item>();
        //    foreach (var entry in costs) {
        //        player.inventory.Take(collect, entry.Key, entry.Value);
        //        player.Command(string.Concat("note.inv ", entry.Key, " ", entry.Value * -1f));
        //    }
        //    foreach (var item in collect) {
        //        item.Remove();
        //    }
        //    Pool.FreeList(ref collect);
        //    missingResources = null;
        //    return true;
        //}

        //private bool CanPay(BasePlayer player, out string missingResources) {
        //    foreach (var entry in costs) {
        //        if (entry.Value <= 0) continue;
        //        var missingAmount = entry.Value - player.inventory.GetAmount(entry.Key);
        //        if (missingAmount <= 0) continue;
        //       var displayName=  ItemManager.FindItemDefinition(entry.Key)?.displayName.english;
        //       if (string.IsNullOrEmpty(displayName)) displayName = entry.Key.ToString();
        //         missingDictionary[displayName] += missingAmount;
        //    }
        //    if (missingDictionary.Count > 0) {
        //        StringBuilder stringBuilder = Pool.Get<StringBuilder>();
        //        foreach (var entry in missingDictionary) {
        //            stringBuilder.AppendLine(Lang("MissingResourceFormat", player.UserIDString, entry.Key, entry.Value));
        //        }
        //        missingResources = stringBuilder.ToString();
        //        stringBuilder.Clear();
        //        missingDictionary.Clear();
        //        Pool.Free(ref stringBuilder);
        //        return false;
        //    }
        //    missingResources = null;
        //    return true;
        //}

        //private void FindUpgradeCosts(BasePlayer player, BuildingGrade.Enum targetGrade) {
        //    var autoGrade = targetGrade == BuildingGrade.Enum.None;
        //    foreach (var buildingBlock in allBuildingBlocks) {
        //        if (buildingBlock == null || buildingBlock.IsDestroyed) {
        //            toRemove.Add(buildingBlock);
        //            continue;
        //        }
        //        BuildingGrade.Enum grade = targetGrade;
        //        if (CheckBuildingGrade(buildingBlock, false, ref grade)) {
        //            if (!autoGrade || tempGrantedGrades.Contains(grade)) {
        //                if (!CanUpgrade(buildingBlock, player, grade)) {
        //                    toRemove.Add(buildingBlock);
        //                    continue;
        //                }
        //            }
        //        }
        //        var costToBuild = buildingBlock.blockDefinition.grades[(int)grade].costToBuild;
        //        foreach (var itemAmount in costToBuild) {
        //            costs[itemAmount.itemid] += (int)itemAmount.amount;
        //        }
        //    }

        //    foreach (var buildingBlock in toRemove) {
        //        allBuildingBlocks.Remove(buildingBlock);
        //    }
        //    toRemove.Clear();
        //}

        //#endregion TryPay

        #endregion Methods

        #endregion Building Grade Control

        #region Helpers

        private static BuildingBlock GetBuildingBlockLookingAt(BasePlayer player, PermissionSettings permissionSettings)
        {
            RaycastHit raycastHit;
            var flag = Physics.Raycast(player.eyes.HeadRay(), out raycastHit, permissionSettings.Distance, Rust.Layers.Mask.Construction);
            return flag ? raycastHit.GetEntity() as BuildingBlock : null;
        }

        private static bool CheckBuildingGrade(BuildingBlock buildingBlock, bool isUpgrade, ref BuildingGrade.Enum targetGrade)
        {
            if (buildingBlock.blockDefinition == null)
            {
                return false;
            }
            var grades = buildingBlock.blockDefinition.grades;
            if (grades == null)
            {
                return false;
            }

            var grade = buildingBlock.grade;
            if (IsValidGrade(grades, (int)targetGrade))
            {
                return isUpgrade ? grade < targetGrade : grade > targetGrade;
            }

            targetGrade = grade + (isUpgrade ? 1 : -1);
            return IsValidGrade(grades, (int)targetGrade);
        }

        private static bool IsValidGrade(ConstructionGrade[] grades, int targetGrade)
        {
            return targetGrade >= 0 && targetGrade < grades.Length && grades[targetGrade] != null;
        }

        private static void SetBuildingBlockGrade(BuildingBlock buildingBlock, BuildingGrade.Enum targetGrade)
        {
            buildingBlock.SetGrade(targetGrade);
            buildingBlock.SetHealthToMax();
            buildingBlock.StartBeingRotatable();
            buildingBlock.SendNetworkUpdate();
            buildingBlock.UpdateSkin();
            buildingBlock.ResetUpkeepTime();
            buildingBlock.UpdateSurroundingEntities();
            BuildingManager.server.GetBuilding(buildingBlock.buildingID)?.Dirty();
            if (targetGrade > BuildingGrade.Enum.Twigs)
            {
                Effect.server.Run("assets/bundled/prefabs/fx/build/promote_" + targetGrade.ToString().ToLower() + ".prefab", buildingBlock, 0u, Vector3.zero, Vector3.zero);
            }
        }

        private static IEnumerator GetNearbyEntities<T>(T sourceEntity, HashSet<T> entities, int layers, Func<T, bool> filter = null) where T : BaseEntity
        {
            int current = 0;
            var checkFrom = Pool.Get<Queue<Vector3>>();
            var nearbyEntities = Pool.GetList<T>();
            checkFrom.Enqueue(sourceEntity.transform.position);
            while (checkFrom.Count > 0)
            {
                nearbyEntities.Clear();
                var position = checkFrom.Dequeue();
                Vis.Entities(position, 3f, nearbyEntities, layers);
                for (var i = 0; i < nearbyEntities.Count; i++)
                {
                    var entity = nearbyEntities[i];
                    if (filter != null && !filter(entity)) continue;
                    if (!entities.Add(entity)) continue;
                    checkFrom.Enqueue(entity.transform.position);
                }
                if (++current % _instance.configData.Global.PerFrame == 0) yield return CoroutineEx.waitForEndOfFrame;
            }
            Pool.Free(ref checkFrom);
            Pool.FreeList(ref nearbyEntities);
        }

        #endregion Helpers

        #region ConfigurationFile

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Settings")]
            public GlobalSettings Global { get; set; } = new GlobalSettings();

            [JsonProperty(PropertyName = "Chat Settings")]
            public ChatSettings Chat { get; set; } = new ChatSettings();

            [JsonProperty(PropertyName = "Permission Settings")]
            public Dictionary<string, PermissionSettings> Permissions { get; set; } = new Dictionary<string, PermissionSettings>
            {
                [PermissionUse] = new PermissionSettings
                {
                    Priority = 0,
                    Pay = true,
                    //Refund = false,
                    Cooldown = 60,
                    Distance = 10,
                }
            };

            [JsonProperty(PropertyName = "Building Block Categories", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, HashSet<string>> Categories { get; set; } = new Dictionary<string, HashSet<string>>
            {
                ["foundation"] = new HashSet<string> {
                    "assets/prefabs/building core/foundation/foundation.prefab",
                    "assets/prefabs/building core/foundation.steps/foundation.steps.prefab",
                    "assets/prefabs/building core/foundation.triangle/foundation.triangle.prefab"
                },
                ["wall"] = new HashSet<string> {
                    "assets/prefabs/building core/wall/wall.prefab",
                    "assets/prefabs/building core/wall.doorway/wall.doorway.prefab",
                    "assets/prefabs/building core/wall.frame/wall.frame.prefab",
                    "assets/prefabs/building core/wall.half/wall.half.prefab",
                    "assets/prefabs/building core/wall.low/wall.low.prefab",
                    "assets/prefabs/building core/wall.window/wall.window.prefab",
                },
                ["floor"] = new HashSet<string> {
                    "assets/prefabs/building core/floor/floor.prefab",
                    "assets/prefabs/building core/floor.frame/floor.frame.prefab",
                    "assets/prefabs/building core/floor.triangle/floor.triangle.prefab",
                    "assets/prefabs/building core/floor.triangle.frame/floor.triangle.frame.prefab",
                },
                ["stair"] = new HashSet<string> {
                    "assets/prefabs/building core/stairs.l/block.stair.lshape.prefab",
                    "assets/prefabs/building core/stairs.spiral/block.stair.spiral.prefab",
                    "assets/prefabs/building core/stairs.spiral.triangle/block.stair.spiral.triangle.prefab",
                    "assets/prefabs/building core/stairs.u/block.stair.ushape.prefab",
                },
                ["roof"] = new HashSet<string> {
                    "assets/prefabs/building core/roof/roof.prefab",
                    "assets/prefabs/building core/roof.triangle/roof.triangle.prefab",
                },
                ["ramp"] = new HashSet<string> {
                    "assets/prefabs/building core/ramp/ramp.prefab",
                },
            };
        }

        private class GlobalSettings
        {
            [JsonProperty(PropertyName = "Use Teams")]
            public bool UseTeams { get; set; }

            [JsonProperty(PropertyName = "Use Clans")]
            public bool UseClans { get; set; } = true;

            [JsonProperty(PropertyName = "Use Friends")]
            public bool UseFriends { get; set; } = true;

            [JsonProperty(PropertyName = "Use Raid Blocker (Need NoEscape Plugin)")]
            public bool UseRaidBlocker { get; set; }

            [JsonProperty(PropertyName = "Use Combat Blocker (Need NoEscape Plugin)")]
            public bool UseCombatBlocker { get; set; }

            [JsonProperty(PropertyName = "Cooldown Exclude Admins")]
            public bool CooldownExclude { get; set; } = true;

            [JsonProperty(PropertyName = "Upgrade/Downgrade Per Frame")]
            public int PerFrame { get; set; } = 10;
        }

        private class ChatSettings
        {
            [JsonProperty(PropertyName = "Upgrade Chat Command")]
            public string UpgradeCommand { get; set; } = "up";

            [JsonProperty(PropertyName = "Downgrade Chat Command")]
            public string DowngradeCommand { get; set; } = "down";

            [JsonProperty(PropertyName = "Upgrade All Chat Command")]
            public string UpgradeAllCommand { get; set; } = "upall";

            [JsonProperty(PropertyName = "Downgrade All Chat Command")]
            public string DowngradeAllCommand { get; set; } = "downall";

            [JsonProperty(PropertyName = "Chat Prefix")]
            public string Prefix { get; set; } = "<color=#00FFFF>[BuildingGrades]</color>: ";

            [JsonProperty(PropertyName = "Chat SteamID Icon")]
            public ulong SteamIdIcon { get; set; } = 0;
        }

        public class PermissionSettings
        {
            [JsonProperty(PropertyName = "Priority")]
            public int Priority { get; set; }

            [JsonProperty(PropertyName = "Distance")]
            public float Distance { get; set; } = 4f;

            [JsonProperty(PropertyName = "Cooldown")]
            public float Cooldown { get; set; } = 60f;

            [JsonProperty(PropertyName = "Pay")]
            public bool Pay { get; set; } = true;

            // [JsonProperty(PropertyName = "Refund")]
            // public bool Refund { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                configData = Config.ReadObject<ConfigData>();
                if (configData == null)
                {
                    LoadDefaultConfig();
                }
            }
            catch (Exception ex)
            {
                PrintError($"The configuration file is corrupted. \n{ex}");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Creating a new configuration file");
            configData = new ConfigData();
        }

        protected override void SaveConfig() => Config.WriteObject(configData);

        #endregion ConfigurationFile

        #region LanguageFile

        private void Print(BasePlayer player, string message)
        {
            Player.Message(player, message, configData.Chat.Prefix, configData.Chat.SteamIdIcon);
        }

        private string Lang(string key, string id = null, params object[] args)
        {
            try
            {
                return string.Format(lang.GetMessage(key, this, id), args);
            }
            catch (Exception)
            {
                PrintError($"Error in the language formatting of '{key}'. (userid: {id}. lang: {lang.GetLanguage(id)}. args: {string.Join(" ,", args)})");
                throw;
            }
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NotAllowed"] = "<color=#FF1919>You do not have permission to use this command.</color>",
                ["UnknownGrade"] = "<color=#FF1919>Unknown grade.</color>",
                ["UnknownCategory"] = "<color=#FF1919>Unknown category.</color> Available categories: <color=#009EFF>{0}</color>",
                ["NotLookingAt"] = "<color=#FF1919>You are not looking at a building block.</color>",
                ["RaidBlocked"] = "<color=#FF1919>You may not do that while raid blocked.</color>.",
                ["CombatBlocked"] = "<color=#FF1919>You may not do that while combat blocked.</color>.",
                ["OnCooldown"] = "You must wait <color=#FF1919>{0}</color> seconds before you can use this command.",
                ["BuildingBlocked"] = "You can't use this command if you don't have the building privileges.",

                ["MissingItemsFormat"] = "* <color=#FF1919>{0}</color> x{1}",
                ["StartUpgrade"] = "Start running upgrade, please wait.",
                ["StartDowngrade"] = "Start running downgrade, please wait.",
                ["AlreadyProcess"] = "There is already a process already running, please wait.",

                ["UpgradeNotEnoughItems"] = "None of the buildings were upgraded. You are missing: \n{0}",
                ["UpgradeNotEnoughItemsSuccess"] = "<color=#FF1919>{0}</color> building blocks were upgraded. Some buildings cannot be upgraded, you are missing: \n{1}",

                ["NotUpgraded"] = "None of the buildings were upgraded.",
                ["NotDowngraded"] = "None of the buildings were downgraded.",
                ["FinishedUpgrade"] = "<color=#FF1919>{0}</color> building blocks were upgraded.",
                ["FinishedDowngrade"] = "<color=#FF1919>{0}</color> building blocks were downgraded.",
            }, this);
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NotAllowed"] = "<color=#FF1919>您没有使用该命令的权限</color>",
                ["UnknownGrade"] = "<color=#FF1919>未知的建筑等级</color>",
                ["UnknownCategory"] = "<color=#FF1919>未知的建筑类型</color> 可用的建筑类型: <color=#009EFF>{0}</color>",
                ["NotLookingAt"] = "<color=#FF1919>您需要看着一个建筑</color>",
                ["RaidBlocked"] = "<color=#FF1919>您被突袭阻止了，不能使用该命令</color>",
                ["CombatBlocked"] = "<color=#FF1919>您被战斗阻止了，不能使用该命令</color>",
                ["OnCooldown"] = "您必须等待 <color=#FF1919>{0}</color> 秒后才能使用该命令",
                ["BuildingBlocked"] = "您不能在没有领地权的地方使用该命令",

                ["MissingItemsFormat"] = "* <color=#FF1919>{0}</color> x{1}",
                ["StartUpgrade"] = "开始运行升级您的建筑，请稍等片刻",
                ["StartDowngrade"] = "开始运行降级您的建筑，请稍等片刻",
                ["AlreadyProcess"] = "已经有一个线程正在运行，请稍等片刻",

                ["UpgradeNotEnoughItems"] = "没有可以升级的建筑。您还需要: \n{0}",
                ["UpgradeNotEnoughItemsSuccess"] = "<color=#FF1919>{0}</color> 个建筑成功升级了。有些建筑无法升级，您还需要: \n{1}",

                ["NotUpgraded"] = "没有可以升级的建筑",
                ["NotDowngraded"] = "没有可以降级的建筑",
                ["FinishedUpgrade"] = "<color=#FF1919>{0}</color> 个建筑成功升级了",
                ["FinishedDowngrade"] = "<color=#FF1919>{0}</color> 个建筑成功降级了",
            }, this, "zh-CN");
        }

        #endregion LanguageFile
    }
}