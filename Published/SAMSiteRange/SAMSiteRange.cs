using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("SAM Site Range", "gsuberland/Arainrr", "1.2.5")]
    [Description("Modifies SAM site range.")]
    internal class SAMSiteRange : RustPlugin
    {
        #region Oxide Hooks

        private void Init()
        {
            Unsubscribe(nameof(OnEntitySpawned));
            foreach (var kvp in configData.permissions)
            {
                if (!permission.PermissionExists(kvp.Key))
                {
                    permission.RegisterPermission(kvp.Key, this);
                }
            }
        }

        private void OnServerInitialized()
        {
            Subscribe(nameof(OnEntitySpawned));
            foreach (var serverEntity in BaseNetworkable.serverEntities)
            {
                var samSite = serverEntity as SamSite;
                if (samSite != null)
                {
                    OnEntitySpawned(samSite);
                }
            }
        }

        private void OnEntitySpawned(SamSite samSite) => ApplySettings(samSite);

        #endregion Oxide Hooks

        #region Methods

        private void ApplySettings(SamSite samSite)
        {
            if (samSite == null) return;
            if (samSite.OwnerID == 0)
            {
                samSite.vehicleScanRadius = configData.staticVehicleRange;
                samSite.missileScanRadius = configData.staticMissileRange;
            }
            else
            {
                PermissionSettings permissionSettings;
                if (GetPermissionS(samSite.OwnerID, out permissionSettings))
                {
                    samSite.vehicleScanRadius = permissionSettings.vehicleScanRadius;
                    samSite.missileScanRadius = permissionSettings.missileScanRadius;
                }
                else
                {
                    return;
                }
            }
            samSite.SendNetworkUpdateImmediate();
        }

        private bool GetPermissionS(ulong playerId, out PermissionSettings permissionSettings)
        {
            int priority = 0;
            permissionSettings = null;
            foreach (var entry in configData.permissions)
            {
                if (entry.Value.priority >= priority && permission.UserHasPermission(playerId.ToString(), entry.Key))
                {
                    priority = entry.Value.priority;
                    permissionSettings = entry.Value;
                }
            }

            return permissionSettings != null;
        }

        #endregion Methods

        #region ConfigurationFile

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Static SamSite Vehicle Scan Range")]
            public float staticVehicleRange = 350f;

            [JsonProperty(PropertyName = "Static SamSite Missile Scan Range")]
            public float staticMissileRange = 500f;

            [JsonProperty(PropertyName = "Permissions", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, PermissionSettings> permissions = new Dictionary<string, PermissionSettings>()
            {
                ["samsiterange.use"] = new PermissionSettings
                {
                    priority = 0,
                    vehicleScanRadius = 200f,
                    missileScanRadius = 275f,
                },
                ["samsiterange.vip"] = new PermissionSettings
                {
                    priority = 1,
                    vehicleScanRadius = 250f,
                    missileScanRadius = 325f,
                }
            };
        }

        private class PermissionSettings
        {
            public int priority;
            public float vehicleScanRadius;
            public float missileScanRadius;
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
    }
}