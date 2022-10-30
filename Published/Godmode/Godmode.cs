using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust;
using Rust;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    [Info("Godmode", "Wulf/lukespragg/Arainrr", "4.2.12", ResourceId = 673)]
    [Description("Allows players with permission to be invulnerable and god-like")]
    internal class Godmode : RustPlugin
    {
        #region Fields

        private const string PermAdmin = "godmode.admin";
        private const string PermInvulnerable = "godmode.invulnerable";
        private const string PermLootPlayers = "godmode.lootplayers";
        private const string PermLootProtection = "godmode.lootprotection";
        private const string PermNoAttacking = "godmode.noattacking";
        private const string PermToggle = "godmode.toggle";
        private const string PermUntiring = "godmode.untiring";
        private const string PermAutoEnable = "godmode.autoenable";

        private readonly object _false = false, _true = true;
        private Dictionary<ulong, float> _informHistory;
        private readonly StoredMetabolism _storedMetabolism = new StoredMetabolism();

        #endregion Fields

        #region Oxide Hook

        private void Init()
        {
            LoadData();
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermInvulnerable, this);
            permission.RegisterPermission(PermLootPlayers, this);
            permission.RegisterPermission(PermLootProtection, this);
            permission.RegisterPermission(PermNoAttacking, this);
            permission.RegisterPermission(PermToggle, this);
            permission.RegisterPermission(PermUntiring, this);
            permission.RegisterPermission(PermAutoEnable, this);

            AddCovalenceCommand(configData.godCommand, nameof(GodCommand));
            AddCovalenceCommand(configData.godsCommand, nameof(GodsCommand));
            if (configData.informOnAttack)
            {
                _informHistory = new Dictionary<ulong, float>();
            }
            if (!configData.disconnectDisable)
            {
                Unsubscribe(nameof(OnPlayerDisconnected));
            }
        }

        private void OnServerInitialized()
        {
            if (!_storedMetabolism.FetchDefaultMetabolism())
            {
                PrintError("Failed to fetch default metabolism data");
            }
            foreach (var god in storedData.godPlayers.ToArray())
            {
                if (!permission.UserHasPermission(god, PermToggle))
                {
                    storedData.godPlayers.Remove(god);
                    continue;
                }
                EnableGodmode(god, true);
            }
            CheckHooks();
        }

        private void OnServerSave()
        {
            timer.Once(Random.Range(0f, 60f), SaveData);
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (IsGod(player))
            {
                PlayerRename(player, true);
                ModifyMetabolism(player, true);
            }
            else if (permission.UserHasPermission(player.UserIDString, PermAutoEnable))
            {
                EnableGodmode(player.UserIDString);
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (IsGod(player))
            {
                DisableGodmode(player.UserIDString);
            }
        }

        private void Unload()
        {
            foreach (var god in storedData.godPlayers.ToArray())
            {
                DisableGodmode(god, true);
            }
            SaveData();
        }

        private object CanBeWounded(BasePlayer player)
        {
            return IsGod(player) ? _false : null;
        }

        private object CanLootPlayer(BasePlayer target, BasePlayer looter)
        {
            if (target == null || looter == null || target == looter)
            {
                return null;
            }
            if (IsGod(target) && permission.UserHasPermission(target.UserIDString, PermLootProtection) && !permission.UserHasPermission(looter.UserIDString, PermLootPlayers))
            {
                Print(looter, Lang("NoLooting", looter.UserIDString));
                return _false;
            }
            return null;
        }

        private object OnEntityTakeDamage(BasePlayer player, HitInfo info)
        {
            if (player == null || !player.userID.IsSteamId())
            {
                return null;
            }
            var attacker = info?.InitiatorPlayer;
            if (IsGod(player) && permission.UserHasPermission(player.UserIDString, PermInvulnerable))
            {
                InformPlayers(player, attacker);
                NullifyDamage(ref info);
                return _true;
            }
            if (IsGod(attacker) && permission.UserHasPermission(attacker.UserIDString, PermNoAttacking))
            {
                InformPlayers(player, attacker);
                NullifyDamage(ref info);
                return _true;
            }
            return null;
        }

        private object OnRunPlayerMetabolism(PlayerMetabolism metabolism, BasePlayer player, float delta)
        {
            if (!IsGod(player))
            {
                return null;
            }
            metabolism.hydration.value = _storedMetabolism.GetMaxHydration();
            if (!permission.UserHasPermission(player.UserIDString, PermUntiring))
            {
                return null;
            }
            var currentCraftLevel = player.currentCraftLevel;
            player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench1, currentCraftLevel == 1f);
            player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench2, currentCraftLevel == 2f);
            player.SetPlayerFlag(BasePlayer.PlayerFlags.Workbench3, currentCraftLevel == 3f);
            player.SetPlayerFlag(BasePlayer.PlayerFlags.SafeZone, player.InSafeZone());
            return _false;
        }

        #endregion Oxide Hook

        #region Methods

        private void CheckHooks()
        {
            if (storedData.godPlayers.Count > 0)
            {
                Subscribe(nameof(CanBeWounded));
                Subscribe(nameof(CanLootPlayer));
                Subscribe(nameof(OnEntityTakeDamage));
                Subscribe(nameof(OnRunPlayerMetabolism));
            }
            else
            {
                Unsubscribe(nameof(CanBeWounded));
                Unsubscribe(nameof(CanLootPlayer));
                Unsubscribe(nameof(OnEntityTakeDamage));
                Unsubscribe(nameof(OnRunPlayerMetabolism));
            }
        }

        private void InformPlayers(BasePlayer victim, BasePlayer attacker)
        {
            if (!configData.informOnAttack || victim == null || attacker == null || victim == attacker)
            {
                return;
            }
            if (!victim.userID.IsSteamId() || !attacker.userID.IsSteamId())
            {
                return;
            }
            float victimTime;
            if (!_informHistory.TryGetValue(victim.userID, out victimTime))
            {
                _informHistory.Add(victim.userID, 0);
            }
            float attackerTime;
            if (!_informHistory.TryGetValue(attacker.userID, out attackerTime))
            {
                _informHistory.Add(attacker.userID, 0);
            }
            var currentTime = Time.realtimeSinceStartup;
            if (IsGod(victim))
            {
                if (currentTime - victimTime > configData.informInterval)
                {
                    _informHistory[victim.userID] = currentTime;
                    Print(attacker, Lang("InformAttacker", attacker.UserIDString, victim.displayName));
                }
                if (currentTime - attackerTime > configData.informInterval)
                {
                    _informHistory[attacker.userID] = currentTime;
                    Print(victim, Lang("InformVictim", victim.UserIDString, attacker.displayName));
                }
            }
            else if (IsGod(attacker))
            {
                if (currentTime - victimTime > configData.informInterval)
                {
                    _informHistory[victim.userID] = currentTime;
                    Print(attacker, Lang("CantAttack", attacker.UserIDString, victim.displayName));
                }
                if (currentTime - attackerTime > configData.informInterval)
                {
                    _informHistory[attacker.userID] = currentTime;
                    Print(victim, Lang("InformVictim", victim.UserIDString, attacker.displayName));
                }
            }
        }

        #region Godmode Toggle

        private bool? ToggleGodmode(BasePlayer target, BasePlayer player)
        {
            var isGod = IsGod(target);
            if (Interface.CallHook("OnGodmodeToggle", target.UserIDString, !isGod) != null)
            {
                return null;
            }
            if (isGod)
            {
                DisableGodmode(target.UserIDString);
                if (player != null)
                {
                    if (target == player)
                    {
                        Print(player, Lang("GodmodeDisabled", player.UserIDString));
                    }
                    else
                    {
                        Print(player, Lang("GodmodeDisabledFor", player.UserIDString, target.displayName));
                        Print(target, Lang("GodmodeDisabledBy", target.UserIDString, player.displayName));
                    }
                }
                else
                {
                    Print(target, Lang("GodmodeDisabledBy", target.UserIDString, "server console"));
                }
                return false;
            }

            EnableGodmode(target.UserIDString);
            if (player != null)
            {
                if (target == player)
                {
                    Print(player, Lang("GodmodeEnabled", player.UserIDString));
                }
                else
                {
                    Print(player, Lang("GodmodeEnabledFor", player.UserIDString, target.displayName));
                    Print(target, Lang("GodmodeEnabledBy", target.UserIDString, player.displayName));
                }
            }
            else
            {
                Print(target, Lang("GodmodeEnabledBy", target.UserIDString, "server console"));
            }
            var targetId = target.UserIDString;
            if (configData.timeLimit > 0)
            {
                timer.Once(configData.timeLimit, () => DisableGodmode(targetId));
            }
            return true;
        }

        private bool EnableGodmode(string playerId, bool isInit = false)
        {
            if (string.IsNullOrEmpty(playerId) || !isInit && IsGod(playerId))
            {
                return false;
            }
            var player = RustCore.FindPlayerByIdString(playerId);
            if (player == null)
            {
                return false;
            }
            PlayerRename(player, true);
            ModifyMetabolism(player, true);
            if (!isInit)
            {
                storedData.godPlayers.Add(player.UserIDString);
                CheckHooks();
            }
            Interface.CallHook("OnGodmodeToggled", playerId, true);
            return true;
        }

        private bool DisableGodmode(string playerId, bool isUnload = false)
        {
            if (string.IsNullOrEmpty(playerId) || !IsGod(playerId))
            {
                return false;
            }
            var player = RustCore.FindPlayerByIdString(playerId);
            if (player == null)
            {
                return false;
            }
            PlayerRename(player, false);
            ModifyMetabolism(player, false);
            if (!isUnload)
            {
                storedData.godPlayers.Remove(player.UserIDString);
                CheckHooks();
            }
            Interface.CallHook("OnGodmodeToggled", playerId, false);
            return true;
        }

        private void PlayerRename(BasePlayer player, bool isGod)
        {
            if (player == null || !configData.showNamePrefix || string.IsNullOrEmpty(configData.namePrefix))
            {
                return;
            }
            var originalName = GetPayerOriginalName(player.userID);
            if (isGod)
            {
                Rename(player, configData.namePrefix + originalName);
            }
            else
            {
                Rename(player, originalName);
            }
        }

        private void Rename(BasePlayer player, string newName)
        {
            if (player == null || string.IsNullOrEmpty(newName.Trim()))
            {
                return;
            }
            player._name = player.displayName = newName;
            if (player.IPlayer != null)
            {
                player.IPlayer.Name = newName;
            }
            if (player.net?.connection != null)
            {
                player.net.connection.username = newName;
            }
            permission.UpdateNickname(player.UserIDString, newName);
            Player.Teleport(player, player.transform.position);
            player.SendNetworkUpdateImmediate();
            //SingletonComponent<ServerMgr>.Instance.persistance.SetPlayerName(player.userID, newName);
        }

        private void ModifyMetabolism(BasePlayer player, bool isGod)
        {
            if (player == null || player.metabolism == null)
            {
                return;
            }
            if (isGod)
            {
                player.health = player.MaxHealth();
                _storedMetabolism.Unlimited(player.metabolism);
            }
            else
            {
                player.health = player.MaxHealth();
                _storedMetabolism.Restore(player.metabolism);
            }
        }

        #endregion Godmode Toggle

        #region Stored Metabolism

        private class StoredMetabolism
        {
            private struct Attribute
            {
                public float Min { get; private set; }
                public float Max { get; private set; }

                public Attribute(MetabolismAttribute attribute)
                {
                    Min = attribute.min;
                    Max = attribute.max;
                }

                public void Reset(MetabolismAttribute attribute)
                {
                    attribute.min = Min;
                    attribute.max = Max;
                }
            }

            public bool FetchDefaultMetabolism()
            {
                var playerPrefab = "assets/prefabs/player/player.prefab";
                var playerMetabolism = GameManager.server.FindPrefab(playerPrefab)?.GetComponent<PlayerMetabolism>();
                if (playerMetabolism != null)
                {
                    Store(playerMetabolism);
                    return true;
                }
                return false;
            }

            private Attribute calories;
            private Attribute hydration;
            private Attribute heartrate;
            private Attribute temperature;
            private Attribute poison;
            private Attribute radiation_level;
            private Attribute radiation_poison;
            private Attribute wetness;
            private Attribute dirtyness;
            private Attribute oxygen;
            private Attribute bleeding;

            // private Attribute comfort;
            // private Attribute pending_health;
            public float GetMaxHydration()
            {
                return hydration.Max;
            }
            public void Store(PlayerMetabolism playerMetabolism)
            {
                calories = new Attribute(playerMetabolism.calories);
                hydration = new Attribute(playerMetabolism.hydration);
                heartrate = new Attribute(playerMetabolism.heartrate);
                temperature = new Attribute(playerMetabolism.temperature);
                poison = new Attribute(playerMetabolism.poison);
                radiation_level = new Attribute(playerMetabolism.radiation_level);
                radiation_poison = new Attribute(playerMetabolism.radiation_poison);
                wetness = new Attribute(playerMetabolism.wetness);
                dirtyness = new Attribute(playerMetabolism.dirtyness);
                oxygen = new Attribute(playerMetabolism.oxygen);
                bleeding = new Attribute(playerMetabolism.bleeding);
                // comfort = new Attribute(playerMetabolism.comfort);
                // pending_health = new Attribute(playerMetabolism.pending_health);
            }

            public void Unlimited(PlayerMetabolism playerMetabolism)
            {
                playerMetabolism.calories.min = calories.Max;
                playerMetabolism.calories.value = calories.Max;
                // playerMetabolism.hydration.min = hydration.Max; // It causes the character to walk slowly
                playerMetabolism.hydration.value = hydration.Max;
                playerMetabolism.heartrate.min = heartrate.Max;
                playerMetabolism.heartrate.value = heartrate.Max;
                playerMetabolism.temperature.min = 37;
                playerMetabolism.temperature.max = 37;
                playerMetabolism.temperature.value = 37;
                playerMetabolism.poison.max = poison.Min;
                playerMetabolism.poison.value = poison.Min;
                playerMetabolism.radiation_level.max = radiation_level.Min;
                playerMetabolism.radiation_level.value = radiation_level.Min;
                playerMetabolism.radiation_poison.max = radiation_poison.Min;
                playerMetabolism.radiation_poison.value = radiation_poison.Min;
                playerMetabolism.wetness.max = wetness.Min;
                playerMetabolism.wetness.value = wetness.Min;
                playerMetabolism.dirtyness.max = dirtyness.Min;
                playerMetabolism.dirtyness.value = dirtyness.Min;
                playerMetabolism.oxygen.min = oxygen.Max;
                playerMetabolism.oxygen.value = oxygen.Max;
                playerMetabolism.bleeding.max = bleeding.Min;
                playerMetabolism.bleeding.value = bleeding.Min;

                playerMetabolism.SendChangesToClient();
            }

            public void Restore(PlayerMetabolism playerMetabolism)
            {
                calories.Reset(playerMetabolism.calories);
                hydration.Reset(playerMetabolism.hydration);
                heartrate.Reset(playerMetabolism.heartrate);
                temperature.Reset(playerMetabolism.temperature);
                poison.Reset(playerMetabolism.poison);
                radiation_level.Reset(playerMetabolism.radiation_level);
                radiation_poison.Reset(playerMetabolism.radiation_poison);
                wetness.Reset(playerMetabolism.wetness);
                dirtyness.Reset(playerMetabolism.dirtyness);
                oxygen.Reset(playerMetabolism.oxygen);
                bleeding.Reset(playerMetabolism.bleeding);
                // comfort.Reset(playerMetabolism.comfort);
                // pending_health.Reset(playerMetabolism.pending_health);

                playerMetabolism.Reset();

                playerMetabolism.calories.value = calories.Max;
                playerMetabolism.hydration.value = hydration.Max;

                playerMetabolism.SendChangesToClient();
            }
        }

        #endregion Stored Metabolism

        #endregion Methods

        #region Helpers

        private static void NullifyDamage(ref HitInfo info)
        {
            info.damageTypes = new DamageTypeList();
            info.HitMaterial = 0;
            info.PointStart = Vector3.zero;
        }

        private static string GetPayerOriginalName(ulong playerId)
        {
            return SingletonComponent<ServerMgr>.Instance.persistance.GetPlayerName(playerId);
        }

        #endregion Helpers

        #region API

        private bool EnableGodmode(IPlayer iPlayer)
        {
            return EnableGodmode(iPlayer.Id);
        }

        private bool EnableGodmode(ulong playerId)
        {
            return EnableGodmode(playerId.ToString());
        }

        private bool DisableGodmode(IPlayer iPlayer)
        {
            return DisableGodmode(iPlayer.Id);
        }

        private bool DisableGodmode(ulong playerId)
        {
            return DisableGodmode(playerId.ToString());
        }

        private bool IsGod(ulong playerId)
        {
            return IsGod(playerId.ToString());
        }

        private bool IsGod(BasePlayer player)
        {
            return player != null && IsGod(player.UserIDString);
        }

        private bool IsGod(string playerId)
        {
            return storedData.godPlayers.Contains(playerId);
        }

        private string[] AllGods(string playerId) => AllGods();
        private string[] AllGods()
        {
            return storedData.godPlayers.ToArray();
        }

        #endregion API

        #region Commands

        private void GodCommand(IPlayer iPlayer, string command, string[] args)
        {
            if (args.Length > 0 && !iPlayer.HasPermission(PermAdmin) || !iPlayer.HasPermission(PermToggle))
            {
                Print(iPlayer, Lang("NotAllowed", iPlayer.Id, command));
                return;
            }
            if (args.Length == 0 && iPlayer.Id == "server_console")
            {
                Print(iPlayer, $"The server console cannot use {command}");
                return;
            }
            var target = args.Length > 0 ? RustCore.FindPlayer(args[0]) : iPlayer.Object as BasePlayer;
            if (args.Length > 0 && target == null)
            {
                Print(iPlayer, Lang("PlayerNotFound", iPlayer.Id, args[0]));
                return;
            }
            var obj = ToggleGodmode(target, iPlayer.Object as BasePlayer);
            if (obj.HasValue && iPlayer.Id == "server_console" && args.Length > 0)
            {
                if (obj.Value)
                {
                    Print(iPlayer, $"'{target?.displayName}' have enabled godmode");
                }
                else
                {
                    Print(iPlayer, $"'{target?.displayName}' have disabled godmode");
                }
            }
        }

        private void GodsCommand(IPlayer iPlayer, string command, string[] args)
        {
            if (!iPlayer.HasPermission(PermAdmin))
            {
                Print(iPlayer, Lang("NotAllowed", iPlayer.Id, command));
                return;
            }
            if (storedData.godPlayers.Count == 0)
            {
                Print(iPlayer, Lang("NoGods", iPlayer.Id));
                return;
            }
            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine();
            foreach (var god in storedData.godPlayers)
            {
                var player = RustCore.FindPlayerByIdString(god);
                stringBuilder.AppendLine(player == null ? god : player.ToString());
            }
            Print(iPlayer, stringBuilder.ToString());
        }

        #endregion Commands

        #region ConfigurationFile

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Inform On Attack (true/false)")]
            public bool informOnAttack = true;

            [JsonProperty(PropertyName = "Inform Interval (Seconds)")]
            public float informInterval = 15;

            [JsonProperty(PropertyName = "Show Name Prefix (true/false)")]
            public bool showNamePrefix = true;

            [JsonProperty(PropertyName = "Name Prefix (Default [God])")]
            public string namePrefix = "[God] ";

            [JsonProperty(PropertyName = "Time Limit (Seconds, 0 to Disable)")]
            public float timeLimit = 0f;

            [JsonProperty(PropertyName = "Disable godmode after disconnect (true/false)")]
            public bool disconnectDisable = false;

            [JsonProperty(PropertyName = "Chat Prefix")]
            public string prefix = "[Godmode]:";

            [JsonProperty(PropertyName = "Chat Prefix color")]
            public string prefixColor = "#00FFFF";

            [JsonProperty(PropertyName = "Chat steamID icon")]
            public ulong steamIDIcon = 0;

            [JsonProperty(PropertyName = "God commands", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public string[] godCommand = { "god", "godmode" };

            [JsonProperty(PropertyName = "Gods commands", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public string[] godsCommand = { "gods", "godlist" };
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

        protected override void SaveConfig()
        {
            Config.WriteObject(configData);
        }

        #endregion ConfigurationFile

        #region DataFile

        private StoredData storedData;

        private class StoredData
        {
            public readonly HashSet<string> godPlayers = new HashSet<string>();
        }

        private void LoadData()
        {
            try
            {
                storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
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

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);
        }

        private void ClearData()
        {
            storedData = new StoredData();
            SaveData();
        }

        #endregion DataFile

        #region LanguageFile

        private void Print(IPlayer iPlayer, string message)
        {
            iPlayer?.Reply(message,
                           iPlayer.Id == "server_console"
                                   ? $"{configData.prefix}"
                                   : $"<color={configData.prefixColor}>{configData.prefix}</color>");
        }

        private void Print(BasePlayer player, string message)
        {
            Player.Message(player, message, string.IsNullOrEmpty(configData.prefix) ? string.Empty : $"<color={configData.prefixColor}>{configData.prefix}</color>", configData.steamIDIcon);
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
                ["GodmodeDisabled"] = "You have <color=#FF4500>Disabled</color> godmode",
                ["GodmodeDisabledBy"] = "Your godmode has been <color=#FF4500>Disabled</color> by {0}",
                ["GodmodeDisabledFor"] = "You have <color=#FF4500>Disabled</color> godmode for {0}",
                ["GodmodeEnabled"] = "You have <color=#00FF00>Enabled</color> godmode",
                ["GodmodeEnabledBy"] = "Your godmode has been <color=#00FF00>Enabled</color> by {0}",
                ["GodmodeEnabledFor"] = "You have <color=#00FF00>Enabled</color> godmode for {0}",
                ["InformAttacker"] = "{0} is in godmode and can't take any damage",
                ["InformVictim"] = "{0} just tried to deal damage to you",
                ["CantAttack"] = "You are in godmode and can't attack {0}",
                ["NoGods"] = "No players currently have godmode enabled",
                ["NoLooting"] = "You are not allowed to loot a player with godmode",
                ["NotAllowed"] = "You are not allowed to use the '{0}' command",
                ["PlayerNotFound"] = "Player '{0}' was not found"
            }, this);
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["GodmodeDisabled"] = "您的上帝模式 <color=#FF4500>已禁用</color>",
                ["GodmodeDisabledBy"] = "{0} <color=#FF4500>禁用了</color> 您的上帝模式",
                ["GodmodeDisabledFor"] = "您 <color=#FF4500>禁用了</color> {0} 的上帝模式",
                ["GodmodeEnabled"] = "您的上帝模式 <color=#00FF00>已启用</color>",
                ["GodmodeEnabledBy"] = "{0} <color=#00FF00>启用了</color> 您的上帝模式",
                ["GodmodeEnabledFor"] = "您 <color=#00FF00>启用了</color> {0} 的上帝模式",
                ["InformAttacker"] = "{0} 处于上帝模式，您不能伤害他",
                ["InformVictim"] = "{0} 想伤害您",
                ["CantAttack"] = "您处于上帝模式，不能伤害 {0}",
                ["NoGods"] = "当前没有玩家启用上帝模式",
                ["NoLooting"] = "您不能掠夺处于上帝模式的玩家",
                ["NotAllowed"] = "您没有权限使用 '{0}' 命令",
                ["PlayerNotFound"] = "玩家 '{0}' 未找到"
            }, this, "zh-CN");
        }

        #endregion LanguageFile
    }
}