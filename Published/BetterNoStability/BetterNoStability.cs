using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Oxide.Plugins
{
    [Info("Better No Stability", "Arainrr", "1.1.7")]
    [Description("Similar to 'server.stability false', but when an item loses its base, it does not levitate.")]
    public class BetterNoStability : RustPlugin
    {
        #region Fields

        private const string PERMISSION_USE = "betternostability.use";
        private static object False;

        #endregion Fields

        #region Oxide Hooks

        private void Init()
        {
            False = false;
            LoadData();
            Unsubscribe(nameof(OnEntitySpawned));
            Unsubscribe(nameof(OnUserGroupAdded));
            Unsubscribe(nameof(OnEntityGroundMissing));
            Unsubscribe(nameof(OnUserPermissionGranted));
            Unsubscribe(nameof(OnGroupPermissionGranted));
            permission.RegisterPermission(PERMISSION_USE, this);

            cmd.AddChatCommand(configData.chatS.command, this, nameof(CmdToggle));
        }

        private void OnServerInitialized()
        {
            UpdateConfig();
            if (configData.pluginEnabled)
            {
                Subscribe(nameof(OnEntitySpawned));
                if (configData.usePermission)
                {
                    Subscribe(nameof(OnUserGroupAdded));
                    Subscribe(nameof(OnUserPermissionGranted));
                    Subscribe(nameof(OnGroupPermissionGranted));
                }

                if (configData.floatingS.enabled)
                {
                    Subscribe(nameof(OnEntityGroundMissing));
                }

                ConVar.Server.stability = true;
                foreach (var stabilityEntity in BaseNetworkable.serverEntities.OfType<StabilityEntity>())
                {
                    OnEntitySpawned(stabilityEntity);
                }
            }
        }

        private void Unload()
        {
            False = null;
        }

        private void OnEntitySpawned(StabilityEntity stabilityEntity)
        {
            if (stabilityEntity == null || stabilityEntity.OwnerID == 0) return;
            if (storedData.disabledPlayers.Contains(stabilityEntity.OwnerID)) return;
            bool enabled;
            if (configData.stabilityS.TryGetValue(stabilityEntity.ShortPrefabName, out enabled) && !enabled) return;
            if (configData.usePermission && !permission.UserHasPermission(stabilityEntity.OwnerID.ToString(), PERMISSION_USE)) return;
            stabilityEntity.grounded = true;
        }

        private object OnEntityGroundMissing(BaseEntity entity)
        {
            if (entity == null || entity.OwnerID == 0) return null;
            if (storedData.disabledPlayers.Contains(entity.OwnerID)) return null;
            if (configData.usePermission && !permission.UserHasPermission(entity.OwnerID.ToString(), PERMISSION_USE)) return null;
            if (configData.floatingS.floatingEntity.Contains(entity.ShortPrefabName)) return False;
            return null;
        }

        #endregion Oxide Hooks

        #region Methods

        private void UpdateConfig()
        {
            foreach (var prefab in GameManifest.Current.entities)
            {
                var stabilityEntity = GameManager.server.FindPrefab(prefab)?.GetComponent<StabilityEntity>();
                if (stabilityEntity != null && !string.IsNullOrEmpty(stabilityEntity.ShortPrefabName) &&
                    !configData.stabilityS.ContainsKey(stabilityEntity.ShortPrefabName))
                {
                    configData.stabilityS.Add(stabilityEntity.ShortPrefabName, stabilityEntity is BuildingBlock);
                }
            }

            SaveConfig();
        }

        #region PermissionChanged

        private void OnUserPermissionGranted(string playerID, string permName)
        {
            if (permName != PERMISSION_USE) return;
            UserPermissionChanged(new string[] { playerID });
        }

        private void OnGroupPermissionGranted(string groupName, string permName)
        {
            if (permName != PERMISSION_USE) return;
            var users = permission.GetUsersInGroup(groupName);
            var playerIDs = users.Select(x => x.Substring(0, x.IndexOf(' ')))
                .Where(x => RustCore.FindPlayerByIdString(x) != null).ToArray();
            UserPermissionChanged(playerIDs);
        }

        private void OnUserGroupAdded(string playerID, string groupName)
        {
            if (!permission.GroupHasPermission(groupName, PERMISSION_USE)) return;
            UserPermissionChanged(new string[] { playerID });
        }

        private void UserPermissionChanged(string[] playerIDs)
        {
            var stabilityEntities = BaseNetworkable.serverEntities.OfType<StabilityEntity>()
                .GroupBy(x => x.ShortPrefabName).ToDictionary(x => x.Key, y => y.ToList());
            foreach (var entry in stabilityEntities)
            {
                bool enabled;
                if (configData.stabilityS.TryGetValue(entry.Key, out enabled) && !enabled) continue;
                foreach (var stabilityEntity in entry.Value)
                {
                    if (stabilityEntity.OwnerID == 0) continue;
                    if (playerIDs.Any(x => x == stabilityEntity.OwnerID.ToString()))
                    {
                        if (!storedData.disabledPlayers.Contains(stabilityEntity.OwnerID))
                        {
                            stabilityEntity.grounded = true;
                        }
                    }
                }
            }
        }

        #endregion PermissionChanged

        #endregion Methods

        #region Commands

        private void CmdToggle(BasePlayer player)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
            {
                Print(player, Lang("NotAllowed", player.UserIDString));
                return;
            }
            if (storedData.disabledPlayers.Contains(player.userID))
            {
                storedData.disabledPlayers.Remove(player.userID);
                Print(player, Lang("Toggle", player.UserIDString, Lang("Enabled", player.UserIDString)));
            }
            else
            {
                storedData.disabledPlayers.Add(player.userID);
                Print(player, Lang("Toggle", player.UserIDString, Lang("Disabled", player.UserIDString)));
            }
            SaveData();
        }

        #endregion Commands

        #region ConfigurationFile

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Enable Plugin")]
            public bool pluginEnabled = false;

            [JsonProperty(PropertyName = "Use Permission")]
            public bool usePermission = false;

            [JsonProperty(PropertyName = "Chat Settings")]
            public ChatSettings chatS = new ChatSettings();

            [JsonProperty(PropertyName = "Stability Entity Settings")]
            public Dictionary<string, bool> stabilityS = new Dictionary<string, bool>();

            [JsonProperty(PropertyName = "Floating Settings")]
            public FloatingS floatingS = new FloatingS();
        }

        public class ChatSettings
        {
            [JsonProperty(PropertyName = "Chat Command")]
            public string command = "ns";

            [JsonProperty(PropertyName = "Chat Prefix")]
            public string prefix = "<color=#00FFFF>[NoStability]</color>: ";

            [JsonProperty(PropertyName = "Chat SteamID Icon")]
            public ulong steamIDIcon;
        }

        public class FloatingS
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool enabled = false;

            [JsonProperty(PropertyName = "Floating Entity List (entity short prefab name)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> floatingEntity = new List<string>
            {
                "rug.deployed",
                "cupboard.tool.deployed"
            };
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

        #region DataFile

        private StoredData storedData;

        private class StoredData
        {
            public HashSet<ulong> disabledPlayers = new HashSet<ulong>();
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

        private void ClearData()
        {
            storedData = new StoredData();
            SaveData();
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);

        #endregion DataFile

        #region LanguageFile

        private void Print(BasePlayer player, string message) => Player.Message(player, message, configData.chatS.prefix, configData.chatS.steamIDIcon);

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
                ["Toggle"] = "No Stability is {0}",
                ["Enabled"] = "<color=#8ee700>Enabled</color>",
                ["Disabled"] = "<color=#ce422b>Disabled</color>",
            }, this);
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NotAllowed"] = "您没有使用该命令的权限",
                ["Toggle"] = "建筑悬浮 {0}",
                ["Enabled"] = "<color=#8ee700>已启用</color>",
                ["Disabled"] = "<color=#ce422b>已禁用</color>",
            }, this, "zh-CN");
        }

        #endregion LanguageFile
    }
}