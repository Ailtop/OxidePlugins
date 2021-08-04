//#define DEBUG
#define MORE_SKIN

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Random Deployables", "Norn/Arainrr", "1.1.0", ResourceId = 2187)]
    [Description("Randomize deployable skins")]
    public class RandomDeployables : RustPlugin
    {
        #region Fields

        private const string PERMISSION_USE = "randomdeployables.use";
        private readonly Hash<string, List<ulong>> itemSkins = new Hash<string, List<ulong>>();
        private readonly Dictionary<string, string> deployed2Item = new Dictionary<string, string>();
        private readonly Dictionary<string, List<ulong>> approvedSkins = new Dictionary<string, List<ulong>>();

        #endregion Fields

        #region Oxide Hooks

        private void Init() => permission.RegisterPermission(PERMISSION_USE, this);

        private void OnServerInitialized()
        {
            FindApprovedSkins();
            bool changed = false;
            foreach (var itemDefinition in ItemManager.GetItemDefinitions())
            {
                var deployablePrefab = itemDefinition.GetComponent<ItemModDeployable>()?.entityPrefab?.resourcePath;
                if (!string.IsNullOrEmpty(deployablePrefab) && !deployed2Item.ContainsKey(deployablePrefab))
                {
                    deployed2Item.Add(deployablePrefab, itemDefinition.shortname);
                    if (!configData.blockItems.Contains(itemDefinition.shortname))
                    {
                        var skins = new HashSet<ulong>(GetItemSkins(itemDefinition)).ToList();
                        if (skins.Count > 0)
                        {
                            if (!configData.customSkins.ContainsKey(itemDefinition.shortname))
                            {
                                changed = true;
                                configData.customSkins.Add(itemDefinition.shortname, new List<ulong>());
                            }
                            skins.RemoveAll(skin => configData.blockSkins.Contains(skin) || skin == 0);
                            if (configData.defaultSkin)
                            {
                                skins.Add(0);
                            }
                            itemSkins[itemDefinition.shortname] = skins;
                        }
                    }
                }
            }

            if (changed)
            {
                SaveConfig();
            }
#if DEBUG
            Interface.Oxide.DataFileSystem.WriteObject(Name, itemSkins);
#endif
        }

        private void OnEntityBuilt(Planner planner, GameObject obj)
        {
            var entity = obj?.ToBaseEntity();
            if (entity == null) return;
            var player = planner?.GetOwnerPlayer();
            if (player == null) return;
            if (configData.blockRandom && entity.skinID != 0)
            {
                return;
            }
            string shortName;
            if (deployed2Item.TryGetValue(entity.PrefabName, out shortName))
            {
                if (permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
                {
                    List<ulong> skins;
                    if (itemSkins.TryGetValue(shortName, out skins))
                    {
                        entity.skinID = skins.GetRandom();
                        entity.SendNetworkUpdate();
                    }
                }
            }
        }

        #endregion Oxide Hooks

        #region Methods

        private void FindApprovedSkins()
        {
            foreach (var skinInfo in Rust.Workshop.Approved.All.Values)
            {
                List<ulong> skins;
                var shortName = skinInfo.Skinnable.ItemName == "lr300.item" ? "rifle.lr300" : skinInfo.Skinnable.ItemName;
                if (!approvedSkins.TryGetValue(shortName, out skins))
                {
                    skins = new List<ulong>();
                    approvedSkins.Add(shortName, skins);
                }
                skins.Add(skinInfo.WorkshopdId);
            }
        }

        private IEnumerable<ulong> GetItemSkins(ItemDefinition itemDefinition)
        {
            if (itemDefinition.skins?.Length > 0)
            {
                foreach (var skin in itemDefinition.skins)
                {
                    yield return (ulong)skin.id;
                }
            }

#if MORE_SKIN

            if (itemDefinition.skins2?.Length > 0)
            {
                foreach (var skin in itemDefinition.skins2)
                {
                    yield return skin.WorkshopDownload;
                }
            }

#endif

            List<ulong> skins;
            if (approvedSkins.TryGetValue(itemDefinition.shortname, out skins))
            {
                foreach (var skin in skins)
                {
                    yield return skin;
                }
            }

            if (configData.customSkins.TryGetValue(itemDefinition.shortname, out skins))
            {
                foreach (var skin in skins)
                {
                    yield return skin;
                }
            }
        }

        #endregion Methods

        #region Commands

        [ConsoleCommand("rd.skins")]
        private void CmdOutputSkins()
        {
            Dictionary<string, HashSet<ulong>> allSkins = new Dictionary<string, HashSet<ulong>>();
            foreach (var itemDefinition in ItemManager.GetItemDefinitions())
            {
                var skins = new HashSet<ulong>(GetItemSkins(itemDefinition));
                if (skins.Count > 0)
                {
                    skins.RemoveWhere(x => x == 0);
                    allSkins.TryAdd(itemDefinition.shortname, skins);
                }
            }
            Interface.Oxide.DataFileSystem.WriteObject(Name, allSkins);
        }

        #endregion Commands

        #region ConfigurationFile

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Allow default skin")]
            public bool defaultSkin = false;

            [JsonProperty(PropertyName = "If the item has skin, block random skin")]
            public bool blockRandom = true;

            [JsonProperty(PropertyName = "Block item list (item short name)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> blockItems = new List<string> { "item short name" };

            [JsonProperty(PropertyName = "Block skin list (item skin id)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> blockSkins = new List<ulong> { 492800372 };

            [JsonProperty(PropertyName = "Custom skin list", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, List<ulong>> customSkins = new Dictionary<string, List<ulong>> { ["door.hinged.metal"] = new List<ulong> { 2465885372 } };
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