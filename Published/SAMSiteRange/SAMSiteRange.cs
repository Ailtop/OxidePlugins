using Facepunch;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using UnityEngine;
using static SamSite;

namespace Oxide.Plugins
{
    [Info("SAM Site Range", "gsuberland/Arainrr", "1.2.6")]
    [Description("Modifies SAM site range.")]
    internal class SAMSiteRange : RustPlugin
    {
        #region Fields

        private static object False;

        #endregion Fields

        #region Oxide Hooks

        private void Init()
        {
            False = false;
            foreach (var kvp in configData.permissions)
            {
                if (!permission.PermissionExists(kvp.Key))
                {
                    permission.RegisterPermission(kvp.Key, this);
                }
            }
        }

        private void Unload()
        {
            False = null;
        }

        private object OnSamSiteTargetScan(SamSite samSite, List<ISamSiteTarget> result)
        {
            float vehicleRange, missileRange;
            if (GetSamSiteScanRange(samSite, out vehicleRange, out missileRange))
            {
                AddTargetSet(samSite, result, Rust.Layers.Mask.Vehicle_World, vehicleRange);
                AddTargetSet(samSite, result, Rust.Layers.Mask.Physics_Projectile, missileRange);
                return False;
            }

            return null;
        }

        private void AddTargetSet(SamSite samSite, List<ISamSiteTarget> allTargets, int layerMask, float scanRadius)
        {
            List<ISamSiteTarget> obj2 = Pool.GetList<ISamSiteTarget>();
            Vis.Entities(samSite.eyePoint.transform.position, scanRadius, obj2, layerMask, QueryTriggerInteraction.Ignore);
            allTargets.AddRange(obj2);
            Pool.FreeList(ref obj2);
        }

        #endregion Oxide Hooks

        #region Methods

        private bool GetSamSiteScanRange(SamSite samSite, out float vehicleRange, out float missileRange)
        {
            if (samSite != null)
            {
                if (samSite.OwnerID == 0)
                {
                    vehicleRange = configData.staticVehicleRange;
                    missileRange = configData.staticMissileRange;
                    return true;
                }

                PermissionSettings permissionSettings;
                if (GetPermissionS(samSite.OwnerID, out permissionSettings))
                {
                    vehicleRange = permissionSettings.vehicleScanRadius;
                    missileRange = permissionSettings.missileScanRadius;
                    return true;
                }
            }
            vehicleRange = missileRange = 0f;
            return false;
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