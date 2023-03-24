using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust;
using Oxide.Game.Rust.Cui;
using ProtoBuf;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    // TODO support sam site
    [Info("Automatic Authorization", "k1lly0u/Arainrr", "1.3.3", ResourceId = 2063)]
    [Description("Shared cupboards, turrets, locks with teams, clans, friends")]
    public class AutomaticAuthorization : RustPlugin
    {
        #region Fields

        [PluginReference]
        private readonly Plugin Clans, Friends, Bank;

        private const string PERMISSION_USE = "automaticauthorization.use";

        private readonly object _true = true;
        private readonly Dictionary<ulong, EntityCache> _playerEntities = new Dictionary<ulong, EntityCache>();

        private enum ShareType
        {
            None,
            Teams,
            Friends,
            Clans
        }

        [Flags]
        private enum AutoAuthType
        {
            Turret = 1 << 0,
            Cupboard = 1 << 1,
            CodeLock = 1 << 2,
            All = -1,
        }

        private class EntityCache
        {
            public readonly HashSet<AutoTurret> autoTurrets = new HashSet<AutoTurret>();
            public readonly HashSet<BuildingPrivlidge> buildingPrivlidges = new HashSet<BuildingPrivlidge>();
            public readonly HashSet<CodeLock> codeLocks = new HashSet<CodeLock>();
        }

        #endregion Fields

        #region Oxide Hooks

        private void Init()
        {
            LoadData();
            UpdateData();

            permission.RegisterPermission(PERMISSION_USE, this);

            cmd.AddChatCommand(configData.Chat.ChatCommand, this, nameof(CmdAutoAuth));
            cmd.AddChatCommand(configData.Chat.UICommand, this, nameof(CmdAutoAuthUI));

            Unsubscribe(nameof(OnEntitySpawned));
            if (!configData.TeamsShare.Enabled)
            {
                Unsubscribe(nameof(OnTeamLeave));
                Unsubscribe(nameof(OnTeamKick));
                Unsubscribe(nameof(OnTeamDisbanded));
                Unsubscribe(nameof(OnTeamAcceptInvite));
            }
            if (!configData.FriendsShare.Enabled)
            {
                Unsubscribe(nameof(OnFriendAdded));
                Unsubscribe(nameof(OnFriendRemoved));
            }
            if (!configData.ClansShare.Enabled)
            {
                Unsubscribe(nameof(OnClanUpdate));
                Unsubscribe(nameof(OnClanDestroy));
                Unsubscribe(nameof(OnClanMemberGone));
            }
        }

        private void OnServerInitialized()
        {
            Subscribe(nameof(OnEntitySpawned));
            foreach (var serverEntity in BaseNetworkable.serverEntities)
            {
                var autoTurret = serverEntity as AutoTurret;
                if (autoTurret != null)
                {
                    CheckEntitySpawned(autoTurret);
                    continue;
                }
                var buildingPrivlidge = serverEntity as BuildingPrivlidge;
                if (buildingPrivlidge != null)
                {
                    CheckEntitySpawned(buildingPrivlidge);
                }
                var codeLock = serverEntity as CodeLock;
                if (codeLock != null)
                {
                    CheckEntitySpawned(codeLock);
                }
            }
        }

        private void OnServerSave()
        {
            timer.Once(Random.Range(0f, 60f), SaveData);
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyUI(player);
            }
            SaveData();
        }

        private void OnEntitySpawned(AutoTurret autoTurret)
        {
            CheckEntitySpawned(autoTurret, true);
        }

        private void OnEntitySpawned(BuildingPrivlidge buildingPrivlidge)
        {
            CheckEntitySpawned(buildingPrivlidge, true);
        }

        private void OnEntitySpawned(CodeLock codeLock)
        {
            CheckEntitySpawned(codeLock, true);
        }

        private void OnEntityKill(AutoTurret autoTurret)
        {
            CheckEntityKill(autoTurret);
        }

        private void OnEntityKill(BuildingPrivlidge buildingPrivlidge)
        {
            CheckEntityKill(buildingPrivlidge);
        }

        private void OnEntityKill(CodeLock codeLock)
        {
            CheckEntityKill(codeLock);
        }

        private void CanChangeCode(BasePlayer player, CodeLock codeLock, string code, bool isGuest)
        {
            NextFrame(() =>
            {
                if (!isGuest ? codeLock.code != code : codeLock.guestCode != code)
                {
                    return;
                }
                var parentEntity = codeLock.GetParentEntity();
                var ownerId = codeLock.OwnerID.IsSteamId() ? codeLock.OwnerID : parentEntity != null ? parentEntity.OwnerID : 0;
                if (!ownerId.IsSteamId())
                {
                    return;
                }
                UpdateAuthList(ownerId, AutoAuthType.CodeLock);
            });
        }

        private object CanUseLockedEntity(BasePlayer player, BaseLock baseLock)
        {
            if (player == null || baseLock == null || !baseLock.IsLocked())
            {
                return null;
            }
            var parentEntity = baseLock.GetParentEntity();
            var ownerId = baseLock.OwnerID.IsSteamId() ? baseLock.OwnerID : parentEntity != null ? parentEntity.OwnerID : 0;
            if (!ownerId.IsSteamId() || ownerId == player.userID)
            {
                return null;
            }
            if (!permission.UserHasPermission(ownerId.ToString(), PERMISSION_USE))
            {
                return null;
            }

            // Ignore the bank boxes
            if (IsBankBox(parentEntity))
            {
                return null;
            }

            var shareData = GetShareData(ownerId, true);
            if (CanSharingLock(baseLock, parentEntity, shareData, ShareType.Teams, ownerId, player.userID))
            {
                return _true;
            }
            if (CanSharingLock(baseLock, parentEntity, shareData, ShareType.Friends, ownerId, player.userID))
            {
                return _true;
            }
            if (CanSharingLock(baseLock, parentEntity, shareData, ShareType.Clans, ownerId, player.userID))
            {
                return _true;
            }
            return null;
        }

        #endregion Oxide Hooks

        #region Helpers

        private static bool CanShareLockedEntity(BaseEntity parentEntity, StoredData.LockShareEntry lockShareEntry)
        {
            if (!lockShareEntry.enabled)
            {
                return false;
            }
            return parentEntity is Door ? lockShareEntry.door :
                    parentEntity is BoxStorage ? lockShareEntry.box : lockShareEntry.other;
        }

        private static void SendUnlockedEffect(CodeLock codeLock)
        {
            if (codeLock.effectUnlocked.isValid)
            {
                Effect.server.Run(codeLock.effectUnlocked.resourcePath, codeLock.transform.position);
            }
        }

        private static void CheckShareData(StoredData.ShareEntry shareEntry, ConfigData.ShareSettings shareSettings)
        {
            if (!shareSettings.Enabled)
            {
                shareEntry.enabled = false;
            }
            if (!shareSettings.ShareCupboard)
            {
                shareEntry.cupboard = false;
            }
            if (!shareSettings.ShareTurret)
            {
                shareEntry.turret = false;
            }
            if (!shareSettings.KeyLock.Enabled)
            {
                shareEntry.keyLock.enabled = false;
            }
            if (!shareSettings.KeyLock.ShareDoor)
            {
                shareEntry.keyLock.door = false;
            }
            if (!shareSettings.KeyLock.ShareBox)
            {
                shareEntry.keyLock.box = false;
            }
            if (!shareSettings.KeyLock.ShareOtherEntity)
            {
                shareEntry.keyLock.other = false;
            }
            if (!shareSettings.CodeLock.Enabled)
            {
                shareEntry.codeLock.enabled = false;
            }
            if (!shareSettings.CodeLock.ShareDoor)
            {
                shareEntry.codeLock.door = false;
            }
            if (!shareSettings.CodeLock.ShareBox)
            {
                shareEntry.codeLock.box = false;
            }
            if (!shareSettings.CodeLock.ShareOtherEntity)
            {
                shareEntry.codeLock.other = false;
            }
        }

        #endregion Helpers

        #region Methods

        #region Entity Spawn / Kill

        private void CheckEntitySpawned(AutoTurret autoTurret, bool justCreated = false)
        {
            if (autoTurret == null || !autoTurret.OwnerID.IsSteamId())
            {
                return;
            }
            EntityCache entityCache;
            if (!_playerEntities.TryGetValue(autoTurret.OwnerID, out entityCache))
            {
                entityCache = new EntityCache();
                _playerEntities.Add(autoTurret.OwnerID, entityCache);
            }
            entityCache.autoTurrets.Add(autoTurret);

            if (justCreated && permission.UserHasPermission(autoTurret.OwnerID.ToString(), PERMISSION_USE))
            {
                AuthToTurret(new HashSet<AutoTurret> { autoTurret }, autoTurret.OwnerID, true);
            }
        }

        private void CheckEntitySpawned(BuildingPrivlidge buildingPrivlidge, bool justCreated = false)
        {
            if (buildingPrivlidge == null || !buildingPrivlidge.OwnerID.IsSteamId())
            {
                return;
            }
            EntityCache entityCache;
            if (!_playerEntities.TryGetValue(buildingPrivlidge.OwnerID, out entityCache))
            {
                entityCache = new EntityCache();
                _playerEntities.Add(buildingPrivlidge.OwnerID, entityCache);
            }
            entityCache.buildingPrivlidges.Add(buildingPrivlidge);

            if (justCreated && permission.UserHasPermission(buildingPrivlidge.OwnerID.ToString(), PERMISSION_USE))
            {
                AuthToCupboard(new HashSet<BuildingPrivlidge> { buildingPrivlidge }, buildingPrivlidge.OwnerID, true);
            }
        }

        private void CheckEntitySpawned(CodeLock codeLock, bool justCreated = false)
        {
            if (codeLock == null)
            {
                return;
            }
            var parentEntity = codeLock.GetParentEntity();
            if (parentEntity != null && IsBankBox(parentEntity))
            {
                return;
            }
            var ownerId = codeLock.OwnerID.IsSteamId() ? codeLock.OwnerID : parentEntity != null ? parentEntity.OwnerID : 0;
            if (!ownerId.IsSteamId())
            {
                return;
            }
            EntityCache entityCache;
            if (!_playerEntities.TryGetValue(ownerId, out entityCache))
            {
                entityCache = new EntityCache();
                _playerEntities.Add(ownerId, entityCache);
            }
            entityCache.codeLocks.Add(codeLock);

            if (justCreated && permission.UserHasPermission(ownerId.ToString(), PERMISSION_USE))
            {
                AuthToCodeLock(new HashSet<CodeLock> { codeLock }, ownerId, true);
            }
        }

        private void CheckEntityKill(AutoTurret autoTurret)
        {
            if (autoTurret == null || !autoTurret.OwnerID.IsSteamId())
            {
                return;
            }
            EntityCache entityCache;
            if (_playerEntities.TryGetValue(autoTurret.OwnerID, out entityCache))
            {
                entityCache.autoTurrets.Remove(autoTurret);
            }
        }

        private void CheckEntityKill(BuildingPrivlidge buildingPrivlidge)
        {
            if (buildingPrivlidge == null || !buildingPrivlidge.OwnerID.IsSteamId())
            {
                return;
            }
            EntityCache entityCache;
            if (_playerEntities.TryGetValue(buildingPrivlidge.OwnerID, out entityCache))
            {
                entityCache.buildingPrivlidges.Remove(buildingPrivlidge);
            }
        }

        private void CheckEntityKill(CodeLock codeLock)
        {
            if (codeLock == null)
            {
                return;
            }
            var parentEntity = codeLock.GetParentEntity();
            var ownerId = codeLock.OwnerID.IsSteamId() ? codeLock.OwnerID : parentEntity != null ? parentEntity.OwnerID : 0;
            if (!ownerId.IsSteamId())
            {
                return;
            }
            EntityCache entityCache;
            if (_playerEntities.TryGetValue(ownerId, out entityCache))
            {
                entityCache.codeLocks.Remove(codeLock);
            }
        }

        #endregion Entity Spawn / Kill

        private void UpdateAuthList(ulong playerId, AutoAuthType autoAuthType)
        {
            if (!permission.UserHasPermission(playerId.ToString(), PERMISSION_USE))
            {
                return;
            }
            EntityCache entityCache;
            if (!_playerEntities.TryGetValue(playerId, out entityCache))
            {
                return;
            }
            if (autoAuthType.HasFlag(AutoAuthType.Turret))
            {
                AuthToTurret(entityCache.autoTurrets, playerId);
            }
            if (autoAuthType.HasFlag(AutoAuthType.Cupboard))
            {
                AuthToCupboard(entityCache.buildingPrivlidges, playerId);
            }
            if (autoAuthType.HasFlag(AutoAuthType.CodeLock))
            {
                AuthToCodeLock(entityCache.codeLocks, playerId);
            }
        }

        private void AuthToTurret(HashSet<AutoTurret> autoTurrets, ulong playerId, bool justCreated = false)
        {
            if (autoTurrets.Count <= 0)
            {
                return;
            }
            var authList = GetAuthorNameIDs(playerId, AutoAuthType.Turret);
            foreach (var autoTurret in autoTurrets)
            {
                if (autoTurret == null || autoTurret.IsDestroyed)
                {
                    continue;
                }
                var isOnline = autoTurret.IsOnline();
                if (isOnline)
                {
                    autoTurret.SetIsOnline(false);
                }

                autoTurret.authorizedPlayers.Clear();
                foreach (var friend in authList)
                {
                    autoTurret.authorizedPlayers.Add(friend);
                }

                if (isOnline)
                {
                    autoTurret.SetIsOnline(true);
                }
                autoTurret.SendNetworkUpdate();
            }
            if (justCreated && configData.Chat.SendMessage && authList.Count > 1)
            {
                var player = BasePlayer.FindByID(playerId);
                if (player != null)
                {
                    Print(player, Lang("TurretSuccess", player.UserIDString, authList.Count - 1, autoTurrets.Count));
                }
            }
        }

        private void AuthToCupboard(HashSet<BuildingPrivlidge> buildingPrivlidges, ulong playerId, bool justCreated = false)
        {
            if (buildingPrivlidges.Count <= 0)
            {
                return;
            }
            var authList = GetAuthorNameIDs(playerId, AutoAuthType.Cupboard);
            foreach (var buildingPrivlidge in buildingPrivlidges)
            {
                if (buildingPrivlidge == null || buildingPrivlidge.IsDestroyed)
                {
                    continue;
                }
                buildingPrivlidge.authorizedPlayers.Clear();
                foreach (var friend in authList)
                {
                    buildingPrivlidge.authorizedPlayers.Add(friend);
                }
                buildingPrivlidge.SendNetworkUpdate();
            }
            if (justCreated && configData.Chat.SendMessage && authList.Count > 1)
            {
                var player = BasePlayer.FindByID(playerId);
                if (player != null)
                {
                    Print(player, Lang("CupboardSuccess", player.UserIDString, authList.Count - 1, buildingPrivlidges.Count));
                }
            }
        }

        private void AuthToCodeLock(HashSet<CodeLock> codeLocks, ulong playerId, bool justCreated = false)
        {
            if (codeLocks.Count <= 0)
            {
                return;
            }
            foreach (var codeLock in codeLocks)
            {
                if (codeLock == null || codeLock.IsDestroyed)
                {
                    continue;
                }
                codeLock.whitelistPlayers.Clear(); // only owner id
                codeLock.whitelistPlayers.Add(playerId);
                
                codeLock.guestPlayers.Clear(); // excluding owner id
                var authList = GetAuthorIDsForCodeLock(playerId, codeLock);
                foreach (var friend in authList)
                {
                    codeLock.guestPlayers.Add(friend);
                }
                codeLock.SendNetworkUpdate();
            }
        }

        private List<PlayerNameID> GetAuthorNameIDs(ulong playerId, AutoAuthType autoAuthType)
        {
            if (autoAuthType == AutoAuthType.CodeLock)
            {
                PrintError("Cannot be used for code locks");
                return new List<PlayerNameID>();
            }
            var authList = GetAuthIds(playerId, autoAuthType);
            return authList.Select(userid => new PlayerNameID { userid = userid, username = RustCore.FindPlayerById(userid)?.displayName ?? string.Empty }).ToList();
        }

        private HashSet<ulong> GetAuthorIDsForCodeLock(ulong playerId, CodeLock codeLock)
        {
            var sharePlayers = new HashSet<ulong>();
            var parentEntity = codeLock.GetParentEntity();
            if (parentEntity == null)
            {
                return sharePlayers;
            }
            var shareData = GetShareData(playerId, true);

            var teamsShare = shareData.GetShareEntry(ShareType.Teams);
            if (teamsShare.enabled && CanShareLockedEntity(parentEntity, teamsShare.codeLock))
            {
                var teamMembers = GetTeamMembers(playerId);
                if (teamMembers != null)
                {
                    foreach (var member in teamMembers)
                    {
                        sharePlayers.Add(member);
                    }
                }
            }
            var friendsShare = shareData.GetShareEntry(ShareType.Friends);
            if (friendsShare.enabled && CanShareLockedEntity(parentEntity, friendsShare.codeLock))
            {
                var friends = GetFriends(playerId);
                if (friends != null)
                {
                    foreach (var friend in friends)
                    {
                        sharePlayers.Add(friend);
                    }
                }
            }
            var clansShare = shareData.GetShareEntry(ShareType.Clans);
            if (clansShare.enabled && CanShareLockedEntity(parentEntity, clansShare.codeLock))
            {
                var clanMembers = GetClanMembers(playerId);
                if (clanMembers != null)
                {
                    foreach (var member in clanMembers)
                    {
                        sharePlayers.Add(member);
                    }
                }
            }
            return sharePlayers;
        }

        private HashSet<ulong> GetAuthIds(ulong playerId, AutoAuthType autoAuthType)
        {
            var sharePlayers = new HashSet<ulong> { playerId };
            var shareData = GetShareData(playerId, true);

            var teamsShare = shareData.GetShareEntry(ShareType.Teams);
            if (teamsShare.enabled && (autoAuthType == AutoAuthType.Turret ? teamsShare.turret : teamsShare.cupboard))
            {
                var teamMembers = GetTeamMembers(playerId);
                if (teamMembers != null)
                {
                    foreach (var member in teamMembers)
                    {
                        sharePlayers.Add(member);
                    }
                }
            }
            var friendsShare = shareData.GetShareEntry(ShareType.Friends);
            if (friendsShare.enabled && (autoAuthType == AutoAuthType.Turret ? friendsShare.turret : friendsShare.cupboard))
            {
                var friends = GetFriends(playerId);
                if (friends != null)
                {
                    foreach (var friend in friends)
                    {
                        sharePlayers.Add(friend);
                    }
                }
            }
            var clansShare = shareData.GetShareEntry(ShareType.Clans);
            if (clansShare.enabled && (autoAuthType == AutoAuthType.Turret ? clansShare.turret : clansShare.cupboard))
            {
                var clanMembers = GetClanMembers(playerId);
                if (clanMembers != null)
                {
                    foreach (var member in clanMembers)
                    {
                        sharePlayers.Add(member);
                    }
                }
            }
            return sharePlayers;
        }

        private bool CanSharingLock(BaseLock baseLock, BaseEntity parentEntity, StoredData.ShareData shareData, ShareType shareType, ulong ownerId, ulong playerId)
        {
            var shareEntry = shareData.GetShareEntry(shareType);
            if (shareEntry.enabled && AreFriends(shareType, ownerId, playerId))
            {
                if (baseLock is KeyLock)
                {
                    return CanShareLockedEntity(parentEntity, shareEntry.keyLock);
                }
                var codeLock = baseLock as CodeLock;
                if (codeLock != null && CanShareLockedEntity(parentEntity, shareEntry.codeLock))
                {
                    SendUnlockedEffect(codeLock);
                    return true;
                }
            }
            return false;
        }

        private IEnumerable<ShareType> GetAvailableTypes()
        {
            if (IsShareTypeEnabled(ShareType.Teams))
            {
                yield return ShareType.Teams;
            }
            if (IsShareTypeEnabled(ShareType.Friends))
            {
                yield return ShareType.Friends;
            }
            if (IsShareTypeEnabled(ShareType.Clans))
            {
                yield return ShareType.Clans;
            }
        }

        private bool IsShareTypeEnabled(ShareType shareType)
        {
            var shareSettings = configData.GetShareSettings(shareType);
            if (!shareSettings.Enabled)
            {
                return false;
            }
            switch (shareType)
            {
                case ShareType.Teams:
                    return RelationshipManager.TeamsEnabled();
                case ShareType.Friends:
                    return Friends != null;
                case ShareType.Clans:
                    return Clans != null;
            }
            return false;
        }

        private bool AreFriends(ShareType shareType, ulong ownerId, ulong playerId)
        {
            switch (shareType)
            {
                case ShareType.Teams:
                    return SameTeam(ownerId, playerId);
                case ShareType.Friends:
                    return HasFriend(ownerId, playerId);
                case ShareType.Clans:
                    return SameClan(ownerId, playerId);
            }
            return false;
        }

        #region Data

        private StoredData.ShareData _defaultData;

        private StoredData.ShareData DefaultData => _defaultData ?? (_defaultData = CreateDefaultData());

        private StoredData.ShareData GetShareData(ulong playerId, bool readOnly = false)
        {
            StoredData.ShareData shareData;
            if (!storedData.playerShareData.TryGetValue(playerId, out shareData))
            {
                if (readOnly)
                {
                    return DefaultData;
                }

                shareData = CreateDefaultData();
                storedData.playerShareData.Add(playerId, shareData);
            }
            return shareData;
        }

        private StoredData.ShareData CreateDefaultData()
        {
            return new StoredData.ShareData
            {
                teamsShare = CreateShareEntry(ShareType.Teams),
                friendsShare = CreateShareEntry(ShareType.Friends),
                clansShare = CreateShareEntry(ShareType.Clans)
            };
        }

        private void UpdateData()
        {
            foreach (var shareData in storedData.playerShareData.Values)
            {
                CheckShareData(shareData, ShareType.Teams);
                CheckShareData(shareData, ShareType.Friends);
                CheckShareData(shareData, ShareType.Clans);
            }
            SaveData();
        }

        private StoredData.ShareEntry CreateShareEntry(ShareType shareType)
        {
            var defaultSettings = configData.GetDefaultShareSettings(shareType);
            var shareEntry = new StoredData.ShareEntry
            {
                enabled = defaultSettings.Enabled,
                turret = defaultSettings.ShareTurret,
                cupboard = defaultSettings.ShareCupboard,
                keyLock = new StoredData.LockShareEntry
                {
                    enabled = defaultSettings.KeyLock.Enabled,
                    door = defaultSettings.KeyLock.ShareDoor,
                    box = defaultSettings.KeyLock.ShareBox,
                    other = defaultSettings.KeyLock.ShareOtherEntity
                },
                codeLock = new StoredData.LockShareEntry
                {
                    enabled = defaultSettings.CodeLock.Enabled,
                    door = defaultSettings.CodeLock.ShareDoor,
                    box = defaultSettings.CodeLock.ShareBox,
                    other = defaultSettings.CodeLock.ShareOtherEntity
                }
            };
            CheckShareData(shareEntry, configData.GetShareSettings(shareType));
            return shareEntry;
        }

        private void CheckShareData(StoredData.ShareData shareData, ShareType shareType)
        {
            var shareSettings = configData.GetShareSettings(shareType);
            var shareEntry = shareData.GetShareEntry(shareType);
            CheckShareData(shareEntry, shareSettings);
        }

        #endregion Data

        #region Conflict Resolution

        private bool IsBankBox(BaseNetworkable entity)
        {
            return Bank != null && Bank.Call<bool>("IsBankBox", entity);
        }

        #endregion Conflict Resolution

        #endregion Methods

        #region External Plugins

        #region Teams

        #region Hooks

        private void OnTeamAcceptInvite(RelationshipManager.PlayerTeam playerTeam, BasePlayer player)
        {
            NextTick(() =>
            {
                if (playerTeam == null || player == null)
                {
                    return;
                }
                if (playerTeam.members.Contains(player.userID))
                {
                    UpdateTeamAuthList(playerTeam.members);
                }
            });
        }

        private void OnTeamLeave(RelationshipManager.PlayerTeam playerTeam, BasePlayer player)
        {
            NextTick(() =>
            {
                if (playerTeam == null || player == null)
                {
                    return;
                }
                if (!playerTeam.members.Contains(player.userID))
                {
                    var teamMembers = new List<ulong>(playerTeam.members) { player.userID };
                    UpdateTeamAuthList(teamMembers);
                }
            });
        }

        private void OnTeamKick(RelationshipManager.PlayerTeam playerTeam, BasePlayer leader, ulong target)
        {
            NextTick(() =>
            {
                if (playerTeam == null)
                {
                    return;
                }
                if (!playerTeam.members.Contains(target))
                {
                    var teamMembers = new List<ulong>(playerTeam.members) { target };
                    UpdateTeamAuthList(teamMembers);
                }
            });
        }

        private void OnTeamDisbanded(RelationshipManager.PlayerTeam playerTeam)
        {
            if (playerTeam == null)
            {
                return;
            }
            UpdateTeamAuthList(playerTeam.members);
        }

        #endregion Hooks

        private void UpdateTeamAuthList(List<ulong> teamMembers)
        {
            if (teamMembers.Count <= 0)
            {
                return;
            }
            foreach (var member in teamMembers)
            {
                UpdateAuthList(member, AutoAuthType.All);
            }
        }

        private static IEnumerable<ulong> GetTeamMembers(ulong playerId)
        {
            if (!RelationshipManager.TeamsEnabled())
            {
                return null;
            }
            var playerTeam = RelationshipManager.ServerInstance.FindPlayersTeam(playerId);
            return playerTeam?.members;
        }

        private static bool SameTeam(ulong playerId, ulong friendId)
        {
            if (!RelationshipManager.TeamsEnabled())
            {
                return false;
            }
            var playerTeam = RelationshipManager.ServerInstance.FindPlayersTeam(playerId);
            if (playerTeam == null)
            {
                return false;
            }
            var friendTeam = RelationshipManager.ServerInstance.FindPlayersTeam(friendId);
            if (friendTeam == null)
            {
                return false;
            }
            return playerTeam == friendTeam;
        }

        #endregion Teams

        #region Friends

        #region Hooks

        private void OnFriendAdded(string playerId, string friendId)
        {
            UpdateFriendAuthList(playerId, friendId);
        }

        private void OnFriendRemoved(string playerId, string friendId)
        {
            UpdateFriendAuthList(playerId, friendId);
        }

        #endregion Hooks

        private void UpdateFriendAuthList(string playerId, string friendId)
        {
            UpdateAuthList(Convert.ToUInt64(playerId), AutoAuthType.All);
            UpdateAuthList(Convert.ToUInt64(friendId), AutoAuthType.All);
        }

        private IEnumerable<ulong> GetFriends(ulong playerId)
        {
            if (Friends == null)
            {
                return null;
            }
            var friends = Friends.Call("GetFriends", playerId) as ulong[];
            return friends;
        }

        private bool HasFriend(ulong playerId, ulong friendId)
        {
            if (Friends == null)
            {
                return false;
            }
            var hasFriend = Friends.Call("HasFriend", playerId, friendId);
            return hasFriend is bool && (bool)hasFriend;
        }

        #endregion Friends

        #region Clans

        #region Hooks

        private void OnClanDestroy(string clanName)
        {
            UpdateClanAuthList(clanName);
        }

        private void OnClanUpdate(string clanName)
        {
            UpdateClanAuthList(clanName);
        }

        #region Clans Reborn Hooks

        private void OnClanMemberGone(string playerId, List<string> memberUserIDs)
        {
            UpdateAuthList(Convert.ToUInt64(playerId), AutoAuthType.All);
        }

        #endregion Clans Reborn Hooks

        #endregion Hooks

        private void UpdateClanAuthList(string clanName)
        {
            var clanMembers = GetClanMembers(clanName);
            if (clanMembers != null)
            {
                foreach (var member in clanMembers)
                {
                    UpdateAuthList(member, AutoAuthType.All);
                }
            }
        }

        private IEnumerable<ulong> GetClanMembers(ulong playerId)
        {
            if (Clans == null)
            {
                return null;
            }
            //Clans Reborn
            var members = Clans.Call("GetClanMembers", playerId) as List<string>;
            if (members != null)
            {
                return members.Select(x => Convert.ToUInt64(x));
            }
            //Clans
            var clanName = Clans.Call("GetClanOf", playerId) as string;
            return clanName != null ? GetClanMembers(clanName) : null;
        }

        private IEnumerable<ulong> GetClanMembers(string clanName)
        {
            if (Clans == null)
            {
                return null;
            }
            var clan = Clans.Call("GetClan", clanName) as JObject;
            var members = clan?.GetValue("members") as JArray;
            return members?.Select(Convert.ToUInt64);
        }

        private bool SameClan(ulong playerId, ulong friendId)
        {
            if (Clans == null)
            {
                return false;
            }
            //Clans and Clans Reborn
            var isMember = Clans.Call("IsClanMember", playerId.ToString(), friendId.ToString());
            if (isMember != null)
            {
                return (bool)isMember;
            }
            //Rust:IO Clans
            var playerClan = Clans.Call("GetClanOf", playerId);
            if (playerClan == null)
            {
                return false;
            }
            var friendClan = Clans.Call("GetClanOf", friendId);
            if (friendClan == null)
            {
                return false;
            }
            return (string)playerClan == (string)friendClan;
        }

        #endregion Clans

        #endregion External Plugins

        #region UI

        private const string UINAME_MAIN = "AutoAuthUI_Main";
        private const string UINAME_MENU = "AutoAuthUI_Menu";

        private void CreateMainUI(BasePlayer player)
        {
            var container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.6" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-390 -200", OffsetMax = "390 260" },
                CursorEnabled = true
            }, "Hud", UINAME_MAIN);
            var titlePanel = container.Add(new CuiPanel
            {
                Image = { Color = "0.31 0.88 0.71 1" },
                RectTransform = { AnchorMin = "0 0.912", AnchorMax = "0.998 1" }
            }, UINAME_MAIN);
            container.Add(new CuiElement
            {
                Parent = titlePanel,
                Components =
                {
                    new CuiTextComponent { Text = Lang("UI_Title", player.UserIDString), FontSize = 24, Align = TextAnchor.MiddleCenter, Color = "1 0 0 1" },
                    new CuiOutlineComponent { Distance = "0.5 0.5", Color = "1 1 1 1" },
                    new CuiRectTransformComponent { AnchorMin = "0.2 0", AnchorMax = "0.8 1" }
                }
            });
            container.Add(new CuiButton
            {
                Button = { Color = "0.95 0.1 0.1 0.95", Close = UINAME_MAIN },
                Text = { Text = "X", Align = TextAnchor.MiddleCenter, Color = "0 0 0 1", FontSize = 22 },
                RectTransform = { AnchorMin = "0.915 0", AnchorMax = "1 0.99" }
            }, titlePanel);
            container.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.4" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.908" }
            }, UINAME_MAIN, UINAME_MENU);
            CuiHelper.DestroyUi(player, UINAME_MAIN);
            CuiHelper.AddUi(player, container);
            var shareData = GetShareData(player.userID, true);
            UpdateMenuUI(player, shareData);
        }

        private void UpdateMenuUI(BasePlayer player, StoredData.ShareData shareData, ShareType shareType = ShareType.None)
        {
            if (player == null)
            {
                return;
            }
            var availableTypes = GetAvailableTypes();
            var total = availableTypes.Count();
            if (total <= 0)
            {
                return;
            }

            var i = 0;
            var container = new CuiElementContainer();

            #region Teams UI

            if (availableTypes.Contains(ShareType.Teams))
            {
                if (shareType == ShareType.None || shareType == ShareType.Teams)
                {
                    var anchors = GetMenuSubAnchors(i, total);
                    CuiHelper.DestroyUi(player, UINAME_MENU + ShareType.Teams);
                    CreateMenuSubUI(ref container, shareData, player.UserIDString, ShareType.Teams, $"{anchors[0]} 0.03", $"{anchors[1]} 0.97");
                }
                i++;
            }

            #endregion Teams UI

            #region Friends UI

            if (availableTypes.Contains(ShareType.Friends))
            {
                if (shareType == ShareType.None || shareType == ShareType.Friends)
                {
                    var anchors = GetMenuSubAnchors(i, total);
                    CuiHelper.DestroyUi(player, UINAME_MENU + ShareType.Friends);
                    CreateMenuSubUI(ref container, shareData, player.UserIDString, ShareType.Friends, $"{anchors[0]} 0.03", $"{anchors[1]} 0.97");
                }
                i++;
            }

            #endregion Friends UI

            #region Clans UI

            if (availableTypes.Contains(ShareType.Clans))
            {
                if (shareType == ShareType.None || shareType == ShareType.Clans)
                {
                    var anchors = GetMenuSubAnchors(i, total);
                    CuiHelper.DestroyUi(player, UINAME_MENU + ShareType.Clans);
                    CreateMenuSubUI(ref container, shareData, player.UserIDString, ShareType.Clans, $"{anchors[0]} 0.03", $"{anchors[1]} 0.97");
                }
            }

            #endregion Clans UI

            CuiHelper.AddUi(player, container);
        }

        private void CreateMenuSubUI(ref CuiElementContainer container, StoredData.ShareData shareData, string playerId, ShareType shareType, string anchorMin, string anchorMax)
        {
            var panelName = container.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.5" },
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax }
            }, UINAME_MENU, UINAME_MENU + shareType);
            var titlePanel = container.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.5" },
                RectTransform = { AnchorMin = "0 0.9", AnchorMax = "1 1" }
            }, panelName);
            container.Add(new CuiLabel
            {
                Text = { Color = "1 0.5 0 1", FontSize = 18, Align = TextAnchor.MiddleCenter, Text = Lang("UI_SubTitle", playerId, Lang("UI_" + shareType, playerId)) },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, titlePanel);

            var contentPanel = container.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.55" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.895" }
            }, panelName);

            var i = 0;
            const float entrySize = 0.08f;
            const float spacingY = 0.012f;

            var shareEntry = shareData.GetShareEntry(shareType);
            var commandPrefix = $"AutoAuthUI {shareType} ";
            var enabledMsg = Lang("Enabled", playerId);
            var disabledMsg = Lang("Disabled", playerId);

            var anchors = GetEntryAnchors(i++, entrySize, spacingY);
            CreateEntrySubUI(ref container, contentPanel, commandPrefix, Lang("UI_SubShare", playerId, Lang("UI_" + shareType, playerId)),
                             shareEntry.enabled ? enabledMsg : disabledMsg, $"0 {anchors[0]}",
                             $"0.995 {anchors[1]}");

            anchors = GetEntryAnchors(i++, entrySize, spacingY);
            CreateEntrySubUI(ref container, contentPanel, commandPrefix + "Cupboard", Lang("UI_SubCupboard", playerId),
                             shareEntry.cupboard ? enabledMsg : disabledMsg, $"0 {anchors[0]}", $"0.995 {anchors[1]}");

            anchors = GetEntryAnchors(i++, entrySize, spacingY);
            CreateEntrySubUI(ref container, contentPanel, commandPrefix + "Turret", Lang("UI_SubTurret", playerId),
                             shareEntry.turret ? enabledMsg : disabledMsg, $"0 {anchors[0]}", $"0.995 {anchors[1]}");

            anchors = GetEntryAnchors(i++, entrySize, spacingY);
            CreateEntrySubUI(ref container, contentPanel, commandPrefix + "KeyLock", Lang("UI_SubKeyLock", playerId),
                             shareEntry.keyLock.enabled ? enabledMsg : disabledMsg, $"0 {anchors[0]}", $"0.995 {anchors[1]}");

            anchors = GetEntryAnchors(i++, entrySize, spacingY);
            CreateEntrySubUI(ref container, contentPanel, commandPrefix + "KeyLock Door", Lang("UI_SubKeyLockDoor", playerId),
                             shareEntry.keyLock.door ? enabledMsg : disabledMsg, $"0 {anchors[0]}", $"0.995 {anchors[1]}");
            anchors = GetEntryAnchors(i++, entrySize, spacingY);
            CreateEntrySubUI(ref container, contentPanel, commandPrefix + "KeyLock Box", Lang("UI_SubKeyLockBox", playerId),
                             shareEntry.keyLock.box ? enabledMsg : disabledMsg, $"0 {anchors[0]}", $"0.995 {anchors[1]}");
            anchors = GetEntryAnchors(i++, entrySize, spacingY);
            CreateEntrySubUI(ref container, contentPanel, commandPrefix + "KeyLock Other", Lang("UI_SubKeyLockOther", playerId),
                             shareEntry.keyLock.other ? enabledMsg : disabledMsg, $"0 {anchors[0]}", $"0.995 {anchors[1]}");

            anchors = GetEntryAnchors(i++, entrySize, spacingY);
            CreateEntrySubUI(ref container, contentPanel, commandPrefix + "CodeLock", Lang("UI_SubCodeLock", playerId),
                             shareEntry.codeLock.enabled ? enabledMsg : disabledMsg, $"0 {anchors[0]}", $"0.995 {anchors[1]}");
            anchors = GetEntryAnchors(i++, entrySize, spacingY);
            CreateEntrySubUI(ref container, contentPanel, commandPrefix + "CodeLock Door", Lang("UI_SubCodeLockDoor", playerId),
                             shareEntry.codeLock.door ? enabledMsg : disabledMsg, $"0 {anchors[0]}", $"0.995 {anchors[1]}");
            anchors = GetEntryAnchors(i++, entrySize, spacingY);
            CreateEntrySubUI(ref container, contentPanel, commandPrefix + "CodeLock Box", Lang("UI_SubCodeLockBox", playerId),
                             shareEntry.codeLock.box ? enabledMsg : disabledMsg, $"0 {anchors[0]}", $"0.995 {anchors[1]}");
            anchors = GetEntryAnchors(i++, entrySize, spacingY);
            CreateEntrySubUI(ref container, contentPanel, commandPrefix + "CodeLock Other", Lang("UI_SubCodeLockOther", playerId),
                             shareEntry.codeLock.other ? enabledMsg : disabledMsg, $"0 {anchors[0]}", $"0.995 {anchors[1]}");
        }

        private static void CreateEntrySubUI(ref CuiElementContainer container, string parentName, string command, string leftText, string rightText, string anchorMin, string anchorMax)
        {
            var panelName = container.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.6" },
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax }
            }, parentName);
            container.Add(new CuiLabel
            {
                Text = { Color = "0 1 1 1", FontSize = 12, Align = TextAnchor.MiddleLeft, Text = leftText },
                RectTransform = { AnchorMin = "0.04 0", AnchorMax = "0.68 1" }
            }, panelName);
            container.Add(new CuiButton
            {
                Button = { Color = "0 0 0 0.7", Command = command },
                Text = { Text = rightText, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1", FontSize = 12 },
                RectTransform = { AnchorMin = "0.7 0.2", AnchorMax = "0.985 0.8" }
            }, panelName);
        }

        private static float[] GetEntryAnchors(int i, float entrySize, float spacingY)
        {
            return new[] { 1f - (i + 1) * entrySize - i * spacingY, 1f - i * (entrySize + spacingY) };
        }

        private static float[] GetMenuSubAnchors(int i, int total)
        {
            switch (total)
            {
                case 1:
                    return new[] { 0.32f, 0.68f };
                case 2:
                    return i == 0 ? new[] { 0.15f, 0.48f } : new[] { 0.52f, 0.85f };
                case 3:
                    switch (i)
                    {
                        case 0:
                            return new[] { 0.01f, 0.33f };
                        case 1:
                            return new[] { 0.34f, 0.66f };
                        default:
                            return new[] { 0.67f, 0.99f };
                    }

                default:
                    return null;
            }
        }

        private static void DestroyUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UINAME_MAIN);
        }

        #endregion UI

        #region Chat Commands

        private void CmdAutoAuth(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
            {
                Print(player, Lang("NotAllowed", player.UserIDString));
                return;
            }
            var shareData = GetShareData(player.userID);
            if (args == null || args.Length == 0)
            {
                var availableTypes = GetAvailableTypes();
                if (!availableTypes.Any())
                {
                    Print(player, Lang("UnableAutoAuth", player.UserIDString));
                    return;
                }
                var stringBuilder = new StringBuilder();
                stringBuilder.AppendLine();

                HandleStatusCommand(stringBuilder, player, shareData, availableTypes);

                Print(player, stringBuilder.ToString());
                return;
            }
            switch (args[0].ToLower())
            {
                case "ui":
                    CreateMainUI(player);
                    return;

                case "t":
                case "at":
                case "team":
                case "autoteam":
                    HandleShareCommand(player, shareData, ShareType.Teams, args);
                    return;

                case "f":
                case "af":
                case "friend":
                case "autofriend":
                    HandleShareCommand(player, shareData, ShareType.Friends, args);
                    return;

                case "c":
                case "ac":
                case "clan":
                case "autoclan":
                    HandleShareCommand(player, shareData, ShareType.Clans, args);
                    return;

                case "h":
                case "help":
                    var availableTypes = GetAvailableTypes();
                    if (!availableTypes.Any())
                    {
                        Print(player, Lang("UnableAutoAuth", player.UserIDString));
                        return;
                    }
                    var stringBuilder = new StringBuilder();
                    stringBuilder.AppendLine();

                    HandleHelpCommand(stringBuilder, player, availableTypes);

                    stringBuilder.AppendLine(Lang("UISyntax", player.UserIDString, configData.Chat.UICommand, configData.Chat.ChatCommand));
                    Print(player, stringBuilder.ToString());
                    return;

                default:
                    Print(player, Lang("SyntaxError", player.UserIDString, configData.Chat.ChatCommand));
                    return;
            }
        }

        private void HandleStatusCommand(StringBuilder stringBuilder, BasePlayer player, StoredData.ShareData shareData, IEnumerable<ShareType> availableTypes)
        {
            var enabledMsg = Lang("Enabled", player.UserIDString);
            var disabledMsg = Lang("Disabled", player.UserIDString);
            foreach (var shareType in availableTypes)
            {
                var shareEntry = shareData.GetShareEntry(shareType);
                var shareTypeName = Lang(shareType.ToString(), player.UserIDString);

                stringBuilder.AppendLine(Lang("ShareStatus", player.UserIDString, shareTypeName));
                stringBuilder.AppendLine(Lang("Share", player.UserIDString, shareTypeName, shareEntry.enabled ? enabledMsg : disabledMsg));
                stringBuilder.AppendLine(Lang("ShareCupboard", player.UserIDString, shareTypeName, shareEntry.cupboard ? enabledMsg : disabledMsg));
                stringBuilder.AppendLine(Lang("ShareTurret", player.UserIDString, shareTypeName, shareEntry.turret ? enabledMsg : disabledMsg));

                stringBuilder.AppendLine(Lang("ShareKeyLock", player.UserIDString, shareTypeName, shareEntry.keyLock.enabled ? enabledMsg : disabledMsg));
                stringBuilder.AppendLine(Lang("ShareKeyLockDoor", player.UserIDString, shareTypeName, shareEntry.keyLock.door ? enabledMsg : disabledMsg));
                stringBuilder.AppendLine(Lang("ShareKeyLockBox", player.UserIDString, shareTypeName, shareEntry.keyLock.box ? enabledMsg : disabledMsg));
                stringBuilder.AppendLine(Lang("ShareKeyLockOther", player.UserIDString, shareTypeName, shareEntry.keyLock.other ? enabledMsg : disabledMsg));

                stringBuilder.AppendLine(Lang("ShareCodeLock", player.UserIDString, shareTypeName, shareEntry.codeLock.enabled ? enabledMsg : disabledMsg));
                stringBuilder.AppendLine(Lang("ShareCodeLockDoor", player.UserIDString, shareTypeName, shareEntry.codeLock.door ? enabledMsg : disabledMsg));
                stringBuilder.AppendLine(Lang("ShareCodeLockBox", player.UserIDString, shareTypeName, shareEntry.codeLock.box ? enabledMsg : disabledMsg));
                stringBuilder.AppendLine(Lang("ShareCodeLockOther", player.UserIDString, shareTypeName, shareEntry.codeLock.other ? enabledMsg : disabledMsg));
            }
        }

        private void HandleHelpCommand(StringBuilder stringBuilder, BasePlayer player, IEnumerable<ShareType> availableTypes)
        {
            foreach (var shareType in availableTypes)
            {
                HandleHelpCommand(stringBuilder, player, shareType);
            }
        }

        private void HandleHelpCommand(StringBuilder stringBuilder, BasePlayer player, ShareType shareType)
        {
            var syntaxName = Lang(shareType + "CmdSyntax", player.UserIDString);
            var membersName = Lang(shareType + "Members", player.UserIDString);
            stringBuilder.AppendLine(Lang("Syntax", player.UserIDString, configData.Chat.ChatCommand, syntaxName, membersName));
            stringBuilder.AppendLine(Lang("Syntax1", player.UserIDString, configData.Chat.ChatCommand, syntaxName, membersName));
            stringBuilder.AppendLine(Lang("Syntax2", player.UserIDString, configData.Chat.ChatCommand, syntaxName, membersName));
            stringBuilder.AppendLine(Lang("Syntax3", player.UserIDString, configData.Chat.ChatCommand, syntaxName, membersName));
            stringBuilder.AppendLine(Lang("Syntax4", player.UserIDString, configData.Chat.ChatCommand, syntaxName, membersName));
        }

        private bool HandleShareCommand(BasePlayer player, StoredData.ShareData shareData, ShareType shareType, string[] args, bool sendMsg = true)
        {
            if (!IsShareTypeEnabled(shareType))
            {
                if (sendMsg)
                {
                    Print(player, Lang("AllDisabled", player.UserIDString, Lang(shareType.ToString(), player.UserIDString)));
                }
                return false;
            }

            var shareEntry = shareData.GetShareEntry(shareType);
            if (args.Length <= 1)
            {
                shareEntry.enabled = !shareEntry.enabled;
                if (sendMsg)
                {
                    Print(player, Lang("All", player.UserIDString, Lang(shareType.ToString(), player.UserIDString), shareEntry.enabled ? Lang("Enabled", player.UserIDString) : Lang("Disabled", player.UserIDString)));
                }
                UpdateAuthList(player.userID, AutoAuthType.All);
                return true;
            }

            var shareSettings = configData.GetShareSettings(shareType);
            switch (args[1].ToLower())
            {
                case "c":
                case "cupboard":
                    if (!shareSettings.ShareCupboard)
                    {
                        if (sendMsg)
                        {
                            Print(player, Lang("CupboardDisabled", player.UserIDString, Lang(shareType.ToString(), player.UserIDString)));
                        }
                        return false;
                    }
                    shareEntry.cupboard = !shareEntry.cupboard;
                    if (sendMsg)
                    {
                        Print(player, Lang("Cupboard", player.UserIDString, Lang(shareType.ToString(), player.UserIDString), shareEntry.cupboard ? Lang("Enabled", player.UserIDString) : Lang("Disabled", player.UserIDString)));
                    }
                    UpdateAuthList(player.userID, AutoAuthType.Cupboard);
                    return true;

                case "t":
                case "turret":
                    if (!shareSettings.ShareTurret)
                    {
                        if (sendMsg)
                        {
                            Print(player, Lang("TurretDisabled", player.UserIDString, Lang(shareType.ToString(), player.UserIDString)));
                        }
                        return false;
                    }
                    shareEntry.turret = !shareEntry.turret;
                    if (sendMsg)
                    {
                        Print(player, Lang("Turret", player.UserIDString, Lang(shareType.ToString(), player.UserIDString), shareEntry.turret ? Lang("Enabled", player.UserIDString) : Lang("Disabled", player.UserIDString)));
                    }
                    UpdateAuthList(player.userID, AutoAuthType.Turret);
                    return true;

                case "kl":
                case "keylock":
                    if (!shareSettings.KeyLock.Enabled)
                    {
                        if (sendMsg)
                        {
                            Print(player, Lang("KeyLockDisabled", player.UserIDString, Lang(shareType.ToString(), player.UserIDString)));
                        }
                        return false;
                    }
                    if (args.Length <= 2)
                    {
                        shareEntry.keyLock.enabled = !shareEntry.keyLock.enabled;
                        if (sendMsg)
                        {
                            Print(player, Lang("KeyLock", player.UserIDString, Lang(shareType.ToString(), player.UserIDString), shareEntry.keyLock.enabled ? Lang("Enabled", player.UserIDString) : Lang("Disabled", player.UserIDString)));
                        }
                        return true;
                    }
                    switch (args[2].ToLower())
                    {
                        case "d":
                        case "door":
                            if (!shareSettings.KeyLock.ShareDoor)
                            {
                                if (sendMsg)
                                {
                                    Print(player, Lang("KeyLockDoorDisabled", player.UserIDString, Lang(shareType.ToString(), player.UserIDString)));
                                }
                                return false;
                            }
                            shareEntry.keyLock.door = !shareEntry.keyLock.door;
                            if (sendMsg)
                            {
                                Print(player, Lang("KeyLockDoor", player.UserIDString, Lang(shareType.ToString(), player.UserIDString), shareEntry.keyLock.door ? Lang("Enabled", player.UserIDString) : Lang("Disabled", player.UserIDString)));
                            }
                            return true;

                        case "b":
                        case "box":
                            if (!shareSettings.KeyLock.ShareBox)
                            {
                                if (sendMsg)
                                {
                                    Print(player, Lang("KeyLockBoxDisabled", player.UserIDString, Lang(shareType.ToString(), player.UserIDString)));
                                }
                                return false;
                            }
                            shareEntry.keyLock.box = !shareEntry.keyLock.box;
                            if (sendMsg)
                            {
                                Print(player, Lang("KeyLockBox", player.UserIDString, Lang(shareType.ToString(), player.UserIDString), shareEntry.keyLock.box ? Lang("Enabled", player.UserIDString) : Lang("Disabled", player.UserIDString)));
                            }
                            return true;

                        case "o":
                        case "other":
                            if (!shareSettings.KeyLock.ShareOtherEntity)
                            {
                                if (sendMsg)
                                {
                                    Print(player, Lang("KeyLockOtherDisabled", player.UserIDString, Lang(shareType.ToString(), player.UserIDString)));
                                }
                                return false;
                            }
                            shareEntry.keyLock.other = !shareEntry.keyLock.other;
                            if (sendMsg)
                            {
                                Print(player, Lang("KeyLockOther", player.UserIDString, Lang(shareType.ToString(), player.UserIDString), shareEntry.keyLock.other ? Lang("Enabled", player.UserIDString) : Lang("Disabled", player.UserIDString)));
                            }
                            return true;
                    }
                    break;

                case "cl":
                case "codelock":
                    if (!shareSettings.CodeLock.Enabled)
                    {
                        if (sendMsg)
                        {
                            Print(player, Lang("CodeLockDisabled", player.UserIDString, Lang(shareType.ToString(), player.UserIDString)));
                        }
                        return false;
                    }
                    if (args.Length <= 2)
                    {
                        shareEntry.codeLock.enabled = !shareEntry.codeLock.enabled;
                        if (sendMsg)
                        {
                            Print(player, Lang("CodeLock", player.UserIDString, Lang(shareType.ToString(), player.UserIDString), shareEntry.codeLock.enabled ? Lang("Enabled", player.UserIDString) : Lang("Disabled", player.UserIDString)));
                        }
                        UpdateAuthList(player.userID, AutoAuthType.CodeLock);
                        return true;
                    }
                    switch (args[2].ToLower())
                    {
                        case "d":
                        case "door":
                            if (!shareSettings.CodeLock.ShareDoor)
                            {
                                if (sendMsg)
                                {
                                    Print(player, Lang("CodeLockDoorDisabled", player.UserIDString, Lang(shareType.ToString(), player.UserIDString)));
                                }
                                return false;
                            }
                            shareEntry.codeLock.door = !shareEntry.codeLock.door;
                            if (sendMsg)
                            {
                                Print(player, Lang("CodeLockDoor", player.UserIDString, Lang(shareType.ToString(), player.UserIDString), shareEntry.codeLock.door ? Lang("Enabled", player.UserIDString) : Lang("Disabled", player.UserIDString)));
                            }
                            UpdateAuthList(player.userID, AutoAuthType.CodeLock);
                            return true;

                        case "b":
                        case "box":
                            if (!shareSettings.CodeLock.ShareBox)
                            {
                                if (sendMsg)
                                {
                                    Print(player, Lang("CodeLockBoxDisabled", player.UserIDString, Lang(shareType.ToString(), player.UserIDString)));
                                }
                                return false;
                            }
                            shareEntry.codeLock.box = !shareEntry.codeLock.box;
                            if (sendMsg)
                            {
                                Print(player, Lang("CodeLockBox", player.UserIDString, Lang(shareType.ToString(), player.UserIDString), shareEntry.codeLock.box ? Lang("Enabled", player.UserIDString) : Lang("Disabled", player.UserIDString)));
                            }
                            UpdateAuthList(player.userID, AutoAuthType.CodeLock);
                            return true;

                        case "o":
                        case "other":
                            if (!shareSettings.CodeLock.ShareOtherEntity)
                            {
                                if (sendMsg)
                                {
                                    Print(player, Lang("CodeLockOtherDisabled", player.UserIDString, Lang(shareType.ToString(), player.UserIDString)));
                                }
                                return false;
                            }
                            shareEntry.codeLock.other = !shareEntry.codeLock.other;
                            if (sendMsg)
                            {
                                Print(player, Lang("CodeLockOther", player.UserIDString, Lang(shareType.ToString(), player.UserIDString), shareEntry.codeLock.other ? Lang("Enabled", player.UserIDString) : Lang("Disabled", player.UserIDString)));
                            }
                            UpdateAuthList(player.userID, AutoAuthType.CodeLock);
                            return true;
                    }
                    break;

                case "h":
                case "help":
                    if (sendMsg)
                    {
                        var stringBuilder = new StringBuilder();
                        stringBuilder.AppendLine();

                        HandleHelpCommand(stringBuilder, player, shareType);

                        Print(player, stringBuilder.ToString());
                    }
                    return true;
            }
            if (sendMsg)
            {
                Print(player, Lang("SyntaxError", player.UserIDString, configData.Chat.ChatCommand));
            }
            return false;
        }

        private void CmdAutoAuthUI(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
            {
                Print(player, Lang("NotAllowed", player.UserIDString));
                return;
            }
            CreateMainUI(player);
        }

        [ConsoleCommand("AutoAuthUI")]
        private void CCmdAutoAuthUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                return;
            }
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
            {
                return;
            }
            var shareData = GetShareData(player.userID);
            switch (arg.Args[0].ToLower())
            {
                case "teams":
                    HandleShareUICommand(player, shareData, ShareType.Teams, arg.Args);
                    return;

                case "friends":
                    HandleShareUICommand(player, shareData, ShareType.Friends, arg.Args);
                    return;

                case "clans":
                    HandleShareUICommand(player, shareData, ShareType.Clans, arg.Args);
                    return;
            }
        }

        private void HandleShareUICommand(BasePlayer player, StoredData.ShareData shareData, ShareType shareType, string[] args)
        {
            if (HandleShareCommand(player, shareData, shareType, args, false))
            {
                UpdateMenuUI(player, shareData, shareType);
            }
        }

        #endregion Chat Commands

        #region ConfigurationFile

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Clear Share Data On Map Wipe")]
            public bool ClearDataOnWipe { get; set; } = false;

            [JsonProperty(PropertyName = "Chat Settings")]
            public ChatSettings Chat { get; set; } = new ChatSettings();

            [JsonProperty(PropertyName = "Teams Share Settings")]
            public ShareSettings TeamsShare { get; set; } = new ShareSettings();

            [JsonProperty(PropertyName = "Friends Share Settings")]
            public ShareSettings FriendsShare { get; set; } = new ShareSettings();

            [JsonProperty(PropertyName = "Clans Share Settings")]
            public ShareSettings ClansShare { get; set; } = new ShareSettings();

            [JsonProperty(PropertyName = "Default Share Settings")]
            public Dictionary<ShareType, ShareSettings> DefaultShare { get; set; } = new Dictionary<ShareType, ShareSettings>
            {
                [ShareType.Teams] = new ShareSettings(),
                [ShareType.Friends] = new ShareSettings(),
                [ShareType.Clans] = new ShareSettings()
            };

            [JsonProperty(PropertyName = "Version")]
            public VersionNumber Version { get; set; }

            public class ShareSettings
            {
                [JsonProperty(PropertyName = "Enabled")]
                public bool Enabled { get; set; } = true;

                [JsonProperty(PropertyName = "Share Cupboard")]
                public bool ShareCupboard { get; set; } = true;

                [JsonProperty(PropertyName = "Share Turret")]
                public bool ShareTurret { get; set; } = true;

                [JsonProperty(PropertyName = "Key Lock Settings")]
                public LockSettings KeyLock { get; set; } = new LockSettings();

                [JsonProperty(PropertyName = "Code Lock Settings")]
                public LockSettings CodeLock { get; set; } = new LockSettings();
            }

            public class LockSettings
            {
                [JsonProperty(PropertyName = "Enabled")]
                public bool Enabled { get; set; } = true;

                [JsonProperty(PropertyName = "Share Door")]
                public bool ShareDoor { get; set; } = true;

                [JsonProperty(PropertyName = "Share Box")]
                public bool ShareBox { get; set; } = true;

                [JsonProperty(PropertyName = "Share Other Locked Entities")]
                public bool ShareOtherEntity { get; set; } = true;
            }

            public ShareSettings GetShareSettings(ShareType shareType)
            {
                switch (shareType)
                {
                    case ShareType.Teams:
                        return TeamsShare;
                    case ShareType.Friends:
                        return FriendsShare;
                    case ShareType.Clans:
                        return ClansShare;
                }
                return null;
            }

            public ShareSettings GetDefaultShareSettings(ShareType shareType)
            {
                switch (shareType)
                {
                    case ShareType.Teams:
                        return DefaultShare[ShareType.Teams];
                    case ShareType.Friends:
                        return DefaultShare[ShareType.Friends];
                    case ShareType.Clans:
                        return DefaultShare[ShareType.Clans];
                }
                return null;
            }
        }

        public class ChatSettings
        {
            [JsonProperty(PropertyName = "Send Authorization Success Message")]
            public bool SendMessage { get; set; } = true;

            [JsonProperty(PropertyName = "Chat Command")]
            public string ChatCommand { get; set; } = "autoauth";

            [JsonProperty(PropertyName = "Chat UI Command")]
            public string UICommand { get; set; } = "autoauthui";

            [JsonProperty(PropertyName = "Chat Prefix")]
            public string Prefix { get; set; } = "<color=#00FFFF>[AutoAuth]</color>: ";

            [JsonProperty(PropertyName = "Chat SteamID Icon")]
            public ulong SteamIdIcon { get; set; } = 0;
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
                else
                {
                    UpdateConfigValues();
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
            configData.Version = Version;
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(configData);
        }

        private void UpdateConfigValues()
        {
            if (configData.Version < Version)
            {
                if (configData.Version <= default(VersionNumber))
                {
                    string prefix, prefixColor;
                    if (GetConfigValue(out prefix, "Chat Settings", "Chat Prefix") && GetConfigValue(out prefixColor, "Chat Settings", "Chat Prefix Color"))
                    {
                        configData.Chat.Prefix = $"<color={prefixColor}>{prefix}</color>: ";
                    }
                }

                if (configData.Version <= new VersionNumber(1, 3, 5))
                {
                    UpdateOldData();
                }
                configData.Version = Version;
            }
        }

        private bool GetConfigValue<T>(out T value, params string[] path)
        {
            var configValue = Config.Get(path);
            if (configValue == null)
            {
                value = default(T);
                return false;
            }
            value = Config.ConvertValue<T>(configValue);
            return true;
        }

        #endregion ConfigurationFile

        #region DataFile

        private StoredData storedData;

        private class StoredData
        {
            [JsonProperty(PropertyName = "shareData")]
            public Dictionary<ulong, ShareData> playerShareData = new Dictionary<ulong, ShareData>();

            public class ShareData
            {
                [JsonProperty(PropertyName = "t")]
                public ShareEntry teamsShare = new ShareEntry();

                [JsonProperty(PropertyName = "f")]
                public ShareEntry friendsShare = new ShareEntry();

                [JsonProperty(PropertyName = "c")]
                public ShareEntry clansShare = new ShareEntry();

                public ShareEntry GetShareEntry(ShareType shareType)
                {
                    switch (shareType)
                    {
                        case ShareType.Teams:
                            return teamsShare;
                        case ShareType.Friends:
                            return friendsShare;
                        case ShareType.Clans:
                            return clansShare;
                    }
                    return null;
                }
            }

            public class ShareDataContractResolver : DefaultContractResolver
            {
                private readonly List<string> _excludedProperties = new List<string>();

                public ShareDataContractResolver(bool teams, bool friends, bool clans)
                {
                    if (!teams)
                    {
                        _excludedProperties.Add("t");
                    }
                    if (!friends)
                    {
                        _excludedProperties.Add("f");
                    }
                    if (!clans)
                    {
                        _excludedProperties.Add("c");
                    }
                }

                protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
                {
                    return _excludedProperties.Count <= 0 ? base.CreateProperties(type, memberSerialization) :
                            base.CreateProperties(type, memberSerialization).Where(p => !_excludedProperties.Contains(p.PropertyName)).ToList();
                }
            }

            [JsonConverter(typeof(ShareEntryConverter))]
            public class ShareEntry
            {
                public bool enabled;
                public bool cupboard;
                public bool turret;
                public LockShareEntry keyLock = new LockShareEntry();
                public LockShareEntry codeLock = new LockShareEntry();

                public string Write()
                {
                    var num = Convert.ToInt32(enabled) << 0 | Convert.ToInt32(cupboard) << 1 | Convert.ToInt32(turret) << 2 |
                            Convert.ToInt32(keyLock.enabled) << 3 | Convert.ToInt32(keyLock.door) << 4 | Convert.ToInt32(keyLock.box) << 5 | Convert.ToInt32(keyLock.other) << 6 |
                            Convert.ToInt32(codeLock.enabled) << 7 | Convert.ToInt32(codeLock.door) << 8 | Convert.ToInt32(codeLock.box) << 9 | Convert.ToInt32(codeLock.other) << 10;
                    return Convert.ToString(num, 2);
                }

                public static ShareEntry Read(string json)
                {
                    var shareEntry = new ShareEntry();
                    var num = Convert.ToInt32(json, 2);
                    shareEntry.enabled = (num >> 0 & 1) != 0;
                    shareEntry.cupboard = (num >> 1 & 1) != 0;
                    shareEntry.turret = (num >> 2 & 1) != 0;
                    shareEntry.keyLock.enabled = (num >> 3 & 1) != 0;
                    shareEntry.keyLock.door = (num >> 4 & 1) != 0;
                    shareEntry.keyLock.box = (num >> 5 & 1) != 0;
                    shareEntry.keyLock.other = (num >> 6 & 1) != 0;
                    shareEntry.codeLock.enabled = (num >> 7 & 1) != 0;
                    shareEntry.codeLock.door = (num >> 8 & 1) != 0;
                    shareEntry.codeLock.box = (num >> 9 & 1) != 0;
                    shareEntry.codeLock.other = (num >> 10 & 1) != 0;
                    return shareEntry;
                }
            }

            public class LockShareEntry
            {
                public bool enabled;
                public bool door;
                public bool box;
                public bool other;
            }

            private class ShareEntryConverter : JsonConverter
            {
                public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
                {
                    var shareEntry = (ShareEntry)value;
                    writer.WriteValue(shareEntry.Write());
                }

                public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
                {
                    if (reader.TokenType == JsonToken.String)
                    {
                        return ShareEntry.Read(reader.Value.ToString());
                    }

                    return null;
                }

                public override bool CanConvert(Type objectType)
                {
                    return objectType == typeof(ShareEntry);
                }
            }
        }

        private void LoadData()
        {
            try
            {
                //storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
                var dataFile = Interface.Oxide.DataFileSystem.GetFile(Name);
                storedData = dataFile.ReadObject<StoredData>();
                dataFile.Settings.ContractResolver =
                        new StoredData.ShareDataContractResolver(configData.TeamsShare.Enabled, configData.FriendsShare.Enabled, configData.ClansShare.Enabled);
            }
            catch
            {
                storedData = null;
            }
            if (storedData == null)
            {
                ClearData();
            }
        }

        private void ClearData()
        {
            storedData = new StoredData();
            SaveData();
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);
        }

        private void OnNewSave(string filename)
        {
            if (configData.ClearDataOnWipe)
            {
                ClearData();
            }
        }

        #region OldData

        private void UpdateOldData()
        {
            try
            {
                var oldStoredData = Interface.Oxide.DataFileSystem.ReadObject<OldStoredData>(Name);
                var newJObject = new JObject();
                var newData = new Dictionary<ulong, JObject>();
                foreach (var entry in oldStoredData.playerShareData)
                {
                    newData.Add(entry.Key, entry.Value.GetNewData());
                }

                newJObject["shareData"] = JObject.FromObject(newData);
                Interface.Oxide.DataFileSystem.WriteObject(Name, newJObject);
            }
            catch
            {
                // ignored
            }
        }

        private class OldStoredData
        {
            public readonly Dictionary<ulong, OldShareData> playerShareData = new Dictionary<ulong, OldShareData>();

            public class OldShareData
            {
                public OldShareEntry teamShare = new OldShareEntry();
                public OldShareEntry friendsShare = new OldShareEntry();
                public OldShareEntry clanShare = new OldShareEntry();

                public JObject GetNewData()
                {
                    var jObject = new JObject();
                    jObject["t"] = teamShare.Write();
                    jObject["f"] = friendsShare.Write();
                    jObject["c"] = clanShare.Write();
                    return jObject;
                }
            }

            public class OldShareEntry
            {
                public bool enabled;
                public bool cupboard;
                public bool turret;
                public bool keyLock;
                public bool codeLock;

                public string Write()
                {
                    var num = Convert.ToInt32(enabled) << 0 | Convert.ToInt32(cupboard) << 1 | Convert.ToInt32(turret) << 2 |
                            Convert.ToInt32(keyLock) << 3 | Convert.ToInt32(keyLock) << 4 | Convert.ToInt32(keyLock) << 5 | Convert.ToInt32(keyLock) << 6 |
                            Convert.ToInt32(codeLock) << 7 | Convert.ToInt32(codeLock) << 8 | Convert.ToInt32(codeLock) << 9 | Convert.ToInt32(codeLock) << 10;
                    return Convert.ToString(num, 2);
                }
            }
        }

        #endregion OldData

        #endregion DataFile

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
                ["NotAllowed"] = "You do not have permission to use this command",
                ["Enabled"] = "<color=#8ee700>Enabled</color>",
                ["Disabled"] = "<color=#ce422b>Disabled</color>",
                ["UnableAutoAuth"] = "Unable to automatically authorize other players",
                ["SyntaxError"] = "Syntax error, please type '<color=#ce422b>/{0} <help | h></color>' to view help",
                ["TurretSuccess"] = "Successfully added <color=#ce422b>{0}</color> friends/clan members/team members to <color=#ce422b>{1}</color> turrets auth list",
                ["CupboardSuccess"] = "Successfully added <color=#ce422b>{0}</color> friends/clan members/team members  to <color=#ce422b>{1}</color> cupboards auth list",

                ["UISyntax"] = "<color=#ce422b>/{0} or /{1} ui</color>\n- Open Automatic Authorization UI",

                ["Syntax"] = "<color=#ce422b>/{0} {1}</color>\n- Enable/Disable automatic authorization for your {2}",
                ["Syntax1"] = "<color=#ce422b>/{0} {1} <cupboard | c></color>\n- Sharing cupboard with your {2}",
                ["Syntax2"] = "<color=#ce422b>/{0} {1} <turret | t></color>\n- Sharing turret with your {2}",
                ["Syntax3"] = "<color=#ce422b>/{0} {1} <keylock | kl> [door / box / other]</color>\n- Sharing key lock with your {2}",
                ["Syntax4"] = "<color=#ce422b>/{0} {1} <codelock | cl> [door / box / other]</color>\n- Sharing code lock with your {2}",

                ["TeamsCmdSyntax"] = "<team | t>",
                ["FriendsCmdSyntax"] = "<friend | f>",
                ["ClansCmdSyntax"] = "<clan | c>",

                ["Teams"] = "<color=#009EFF>Team</color>",
                ["Friends"] = "<color=#009EFF>Friend</color>",
                ["Clans"] = "<color=#009EFF>Clan</color>",
                ["TeamsMembers"] = "<color=#009EFF>team members</color>",
                ["FriendsMembers"] = "<color=#009EFF>friends</color>",
                ["ClansMembers"] = "<color=#009EFF>clan members</color>",

                ["ShareStatus"] = "<color=#ffa500>Current {0} sharing status: </color>",
                ["Share"] = "Auto sharing with {0}: {1}",
                ["ShareCupboard"] = "Auto sharing cupboard with {0}: {1}",
                ["ShareTurret"] = "Auto sharing turret with {0}: {1}",
                ["ShareKeyLock"] = "Auto sharing key lock with {0}: {1}",
                ["ShareKeyLockDoor"] = "Auto sharing key lock of door with {0}: {1}",
                ["ShareKeyLockBox"] = "Auto sharing key lock of box with {0}: {1}",
                ["ShareKeyLockOther"] = "Auto sharing key lock of other entity with {0}: {1}",
                ["ShareCodeLock"] = "Auto sharing code lock with {0}: {1}",
                ["ShareCodeLockDoor"] = "Auto sharing code lock of door with {0}: {1}",
                ["ShareCodeLockBox"] = "Auto sharing code lock of box with {0}: {1}",
                ["ShareCodeLockOther"] = "Auto sharing code lock of other entity with {0}: {1}",

                ["All"] = "Sharing with {0} is {1}",
                ["Cupboard"] = "Sharing cupboard with {0} is {1}",
                ["Turret"] = "Sharing turret with {0} is {1}",
                ["KeyLock"] = "Sharing key lock with {0} is {1}",
                ["KeyLockDoor"] = "Sharing key lock of door with {0} is {1}",
                ["KeyLockBox"] = "Sharing key lock of box with {0} is {1}",
                ["KeyLockOther"] = "Sharing key lock of other entity with {0} is {1}",
                ["CodeLock"] = "Sharing code lock with {0} is {1}",
                ["CodeLockDoor"] = "Sharing code lock of door with {0} is {1}",
                ["CodeLockBox"] = "Sharing code lock of box with {0} is {1}",
                ["CodeLockOther"] = "Sharing code lock of other entity with {0} is {1}",

                ["AllDisabled"] = "Server has disabled {0} sharing",
                ["CupboardDisabled"] = "Server has disabled sharing cupboard with {0}",
                ["TurretDisabled"] = "Server has disabled sharing turret with {0}",
                ["KeyLockDisabled"] = "Server has disabled sharing key lock with {0}",
                ["KeyLockDoorDisabled"] = "Server has disabled sharing key lock of door with {0}",
                ["KeyLockBoxDisabled"] = "Server has disabled sharing key lock of box with {0}",
                ["KeyLockOtherDisabled"] = "Server has disabled sharing key lock of other entity with {0}",
                ["CodeLockDisabled"] = "Server has disabled sharing code lock with {0}",
                ["CodeLockDoorDisabled"] = "Server has disabled sharing code lock of door with {0}",
                ["CodeLockBoxDisabled"] = "Server has disabled sharing code lock of box with {0}",
                ["CodeLockOtherDisabled"] = "Server has disabled sharing code lock of other entity with {0}",

                ["UI_Teams"] = "Team",
                ["UI_Friends"] = "Friend",
                ["UI_Clans"] = "Clan",
                ["UI_Title"] = "Automatic Authorization UI",

                ["UI_SubTitle"] = "{0} Sharing Settings",
                ["UI_SubShare"] = "{0} Sharing",
                ["UI_SubCupboard"] = "Sharing Cupboard",
                ["UI_SubTurret"] = "Sharing Turret",
                ["UI_SubKeyLock"] = "Sharing Key Lock",
                ["UI_SubKeyLockDoor"] = "Sharing Key Lock of Door",
                ["UI_SubKeyLockBox"] = "Sharing Key Lock of Box",
                ["UI_SubKeyLockOther"] = "Sharing Key Lock of Other Entity",
                ["UI_SubCodeLock"] = "Sharing Code Lock",
                ["UI_SubCodeLockDoor"] = "Sharing Code Lock of Door",
                ["UI_SubCodeLockBox"] = "Sharing Code Lock of Box",
                ["UI_SubCodeLockOther"] = "Sharing Code Lock of Other Entity"
            }, this);

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NotAllowed"] = "您没有权限使用该命令",
                ["Enabled"] = "<color=#8ee700>已启用</color>",
                ["Disabled"] = "<color=#ce422b>已禁用</color>",
                ["UnableAutoAuth"] = "服务器无法使用自动授权",
                ["SyntaxError"] = "语法错误, 输入 '<color=#ce422b>/{0} <help | h></color>' 查看帮助",
                ["TurretSuccess"] = "自动添加了 <color=#ce422b>{0}</color> 个朋友/战队成员/队友到您的 <color=#ce422b>{1}</color> 个炮台授权列表中",
                ["CupboardSuccess"] = "自动添加了 <color=#ce422b>{0}</color> 个朋友/战队成员/队友到您的 <color=#ce422b>{1}</color> 个领地柜授权列表中",

                ["UISyntax"] = "<color=#ce422b>/{0} 或 /{1} ui</color>\n- 打开自动共享UI",

                ["Syntax"] = "<color=#ce422b>/{0} {1}</color>\n- 启用/禁用{2}自动授权",
                ["Syntax1"] = "<color=#ce422b>/{0} {1} <cupboard | c></color>\n- 自动与{2}共享领地柜",
                ["Syntax2"] = "<color=#ce422b>/{0} {1} <turret | t></color>\n- 自动与{2}共享炮台",
                ["Syntax3"] = "<color=#ce422b>/{0} {1} <keylock | kl> [door / box / other]</color>\n- 自动与{2}共享钥匙锁",
                ["Syntax4"] = "<color=#ce422b>/{0} {1} <codelock | cl> [door / box / other]</color>\n- 自动与{2}共享密码锁",

                ["TeamsCmdSyntax"] = "<team | at>",
                ["FriendsCmdSyntax"] = "<friend | f>",
                ["ClansCmdSyntax"] = "<clan | c>",

                ["Teams"] = "<color=#009EFF>团队</color>",
                ["Friends"] = "<color=#009EFF>好友</color>",
                ["Clans"] = "<color=#009EFF>战队</color>",
                ["TeamsMembers"] = "<color=#009EFF>团队成员</color>",
                ["FriendsMembers"] = "<color=#009EFF>好友</color>",
                ["ClansMembers"] = "<color=#009EFF>战队成员</color>",

                ["ShareStatus"] = "<color=#ffa500>当前{0}自动授权状态: </color>",
                ["Share"] = "自动与{0}共享: {1}",
                ["ShareCupboard"] = "自动与{0}共享领地柜: {1}",
                ["ShareTurret"] = "自动与{0}共享自动炮塔: {1}",
                ["ShareKeyLock"] = "自动与{0}共享钥匙锁: {1}",
                ["ShareKeyLockDoor"] = "自动与{0}共享门的钥匙锁: {1}",
                ["ShareKeyLockBox"] = "自动与{0}共享箱子的钥匙锁: {1}",
                ["ShareKeyLockOther"] = "自动与{0}共享其它实体的钥匙锁: {1}",
                ["ShareCodeLock"] = "自动与{0}共享密码锁: {1}",
                ["ShareCodeLockDoor"] = "自动与{0}共享门的密码锁: {1}",
                ["ShareCodeLockBox"] = "自动与{0}共享箱子的密码锁: {1}",
                ["ShareCodeLockOther"] = "自动与{0}共享其它实体的密码锁: {1}",

                ["All"] = "{0}自动授权 {1}",
                ["Cupboard"] = "自动与{0}共享领地柜 {1}",
                ["Turret"] = "自动与{0}共享自动炮塔 {1}",
                ["KeyLock"] = "自动与{0}共享钥匙锁 {1}",
                ["KeyLockDoor"] = "自动与{0}共享门的钥匙锁 {1}",
                ["KeyLockBox"] = "自动与{0}共享箱子的钥匙锁 {1}",
                ["KeyLockOther"] = "自动与{0}共享其它实体的钥匙锁 {1}",
                ["CodeLock"] = "自动与{0}共享密码锁 {1}",
                ["CodeLockDoor"] = "自动与{0}共享门的密码锁 {1}",
                ["CodeLockBox"] = "自动与{0}共享箱子的密码锁 {1}",
                ["CodeLockOther"] = "自动与{0}共享其它实体的密码锁 {1}",

                ["AllDisabled"] = "服务器已禁用{0}自动授权",
                ["CupboardDisabled"] = "服务器已禁用自动与{0}共享领地柜",
                ["TurretDisabled"] = "服务器已禁用自动与{0}共享自动炮塔",
                ["KeyLockDisabled"] = "服务器已禁用自动与{0}共享钥匙锁",
                ["KeyLockDoorDisabled"] = "服务器已禁用自动与{0}共享门的钥匙锁",
                ["KeyLockBoxDisabled"] = "服务器已禁用自动与{0}共享箱子的钥匙锁",
                ["KeyLockOtherDisabled"] = "服务器已禁用自动与{0}共享其它实体的钥匙锁",
                ["CodeLockDisabled"] = "服务器已禁用自动与{0}共享密码锁",
                ["CodeLockDoorDisabled"] = "服务器已禁用自动与{0}共享门的密码锁",
                ["CodeLockBoxDisabled"] = "服务器已禁用自动与{0}共享箱子的密码锁",
                ["CodeLockOtherDisabled"] = "服务器已禁用自动与{0}共享其它实体的密码锁",

                ["UI_Teams"] = "团队",
                ["UI_Friends"] = "好友",
                ["UI_Clans"] = "战队",
                ["UI_Title"] = "自动共享用户界面",

                ["UI_SubTitle"] = "{0}共享设置",
                ["UI_SubShare"] = "{0}共享",
                ["UI_SubCupboard"] = "共享领地柜",
                ["UI_SubTurret"] = "共享自动炮台",
                ["UI_SubKeyLock"] = "共享钥匙锁",
                ["UI_SubKeyLockDoor"] = "共享门的钥匙锁",
                ["UI_SubKeyLockBox"] = "共享箱子的钥匙锁",
                ["UI_SubKeyLockOther"] = "共享其它实体的钥匙锁",
                ["UI_SubCodeLock"] = "共享密码锁",
                ["UI_SubCodeLockDoor"] = "共享门的密码锁",
                ["UI_SubCodeLockBox"] = "共享箱子的密码锁",
                ["UI_SubCodeLockOther"] = "共享其它实体的密码锁"
            }, this, "zh-CN");
        }

        #endregion LanguageFile
    }
}