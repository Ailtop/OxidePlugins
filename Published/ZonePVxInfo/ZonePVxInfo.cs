using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Zone PVx Info", "BuzZ[PHOQUE]/Arainrr", "1.1.5")]
    [Description("HUD on PVx name defined Zones")]
    public class ZonePVxInfo : RustPlugin
    {
        #region Fields

        [PluginReference] private readonly Plugin ZoneManager, DynamicPVP, RaidableBases, AbandonedBases;
        private const string UINAME_MAIN = "ZonePVxInfoUI";
        private bool pvpAll;
        private string pvpUIJson, pveUIJson, pvpDelayUIJson;

        private enum PVxType
        {
            PVE,
            PVP,
            PVPDelay,
        }

        #endregion Fields

        #region Oxide Hooks

        private void Init()
        {
            AddCovalenceCommand("pvpall", nameof(CmdServerPVx));
            pvpUIJson = GetUIJson(PVxType.PVP);
            pveUIJson = GetUIJson(PVxType.PVE);
            pvpDelayUIJson = GetUIJson(PVxType.PVPDelay);
            if (configData.defaultType == PVxType.PVPDelay)
            {
                configData.defaultType = PVxType.PVE;
            }
        }

        private void OnServerInitialized()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                OnPlayerConnected(player);
            }
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyUI(player);
            }
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null || !player.userID.IsSteamId()) return;
            if (pvpAll)
            {
                CreatePVxUI(player, PVxType.PVP);
            }
            else
            {
                CheckPlayerZone(player);
            }
        }

        #endregion Oxide Hooks

        #region ZoneManager

        private string GetZoneName(string zoneID)
        {
            return (string)ZoneManager.Call("GetZoneName", zoneID);
        }

        private string[] GetPlayerZoneIDs(BasePlayer player)
        {
            return (string[])ZoneManager.Call("GetPlayerZoneIDs", player);
        }

        private float GetZoneRadius(string zoneID)
        {
            var obj = ZoneManager.Call("GetZoneRadius", zoneID); ;
            if (obj is float)
            {
                return (float)obj;
            }
            return 0f;
        }

        private Vector3 GetZoneSize(string zoneID)
        {
            var obj = ZoneManager.Call("GetZoneSize", zoneID); ;
            if (obj is Vector3)
            {
                return (Vector3)obj;
            }
            return Vector3.zero;
        }

        private void OnEnterZone(string zoneID, BasePlayer player)
        {
            NextTick(() => CheckPlayerZone(player));
        }

        private void OnExitZone(string zoneID, BasePlayer player)
        {
            NextTick(() => CheckPlayerZone(player));
        }

        private void CheckPlayerZone(BasePlayer player, bool checkPVPDelay = true)
        {
            if (pvpAll || player == null || !player.IsConnected || !player.userID.IsSteamId()) return;
            if (checkPVPDelay && IsPlayerInPVPDelay(player.userID))
            {
                CreatePVxUI(player, PVxType.PVPDelay);
                return;
            }
            if (ZoneManager != null)
            {
                var zoneName = GetSmallestZoneName(player);
                if (!string.IsNullOrEmpty(zoneName))
                {
                    if (zoneName.Contains("pvp", CompareOptions.IgnoreCase))
                    {
                        CreatePVxUI(player, PVxType.PVP);
                        return;
                    }

                    if (zoneName.Contains("pve", CompareOptions.IgnoreCase))
                    {
                        CreatePVxUI(player, PVxType.PVE);
                        return;
                    }
                }
            }

            if (configData.showDefault)
            {
                CreatePVxUI(player, configData.defaultType);
            }
            else
            {
                DestroyUI(player);
            }
        }

        private string GetSmallestZoneName(BasePlayer player)
        {
            float radius = float.MaxValue;
            string smallest = null;
            var zoneIDs = GetPlayerZoneIDs(player);
            foreach (var zoneID in zoneIDs)
            {
                var zoneName = GetZoneName(zoneID);
                if (string.IsNullOrEmpty(zoneName)) continue;
                float zoneRadius;
                var zoneSize = GetZoneSize(zoneID);
                if (zoneSize != Vector3.zero)
                {
                    zoneRadius = (zoneSize.x + zoneSize.z) / 2;
                }
                else
                {
                    zoneRadius = GetZoneRadius(zoneID);
                }
                if (zoneRadius <= 0f)
                {
                    continue;
                }
                if (radius >= zoneRadius)
                {
                    radius = zoneRadius;
                    smallest = zoneName;
                }
            }
            return smallest;
        }

        #endregion ZoneManager

        #region RaidableBases

        private void OnPlayerEnteredRaidableBase(BasePlayer player, Vector3 location, bool allowPVP)
        {
            if (pvpAll) return;
            CreatePVxUI(player, allowPVP ? PVxType.PVP : PVxType.PVE);
        }

        private void OnPlayerExitedRaidableBase(BasePlayer player, Vector3 location, bool allowPVP)
        {
            NextTick(() => CheckPlayerZone(player));
        }

        private void OnRaidableBaseEnded(Vector3 raidPos, int mode, float loadingTime)
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                CheckPlayerZone(player);
            }
        }

        #endregion RaidableBases

        #region AbandonedBases

        private void OnPlayerEnteredAbandonedBase(BasePlayer player, Vector3 location, bool allowPVP)
        {
            if (pvpAll) return;
            CreatePVxUI(player, allowPVP ? PVxType.PVP : PVxType.PVE);
        }

        private void OnPlayerExitAbandonedBase(BasePlayer player, Vector3 location, bool allowPVP)
        {
            NextTick(() => CheckPlayerZone(player));
        }

        private void OnAbandonedBaseEnded(Vector3 location)
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                CheckPlayerZone(player);
            }
        }

        #endregion AbandonedBases

        #region CargoTrainTunnel

        private void OnPlayerEnterPVPBubble(TrainEngine trainEngine, BasePlayer player)
        {
            if (pvpAll) return;
            CreatePVxUI(player, PVxType.PVP);
        }

        private void OnPlayerExitPVPBubble(TrainEngine trainEngine, BasePlayer player)
        {
            NextTick(() => CheckPlayerZone(player));
        }

        private void OnTrainEventEnded(TrainEngine trainEngine)
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                CheckPlayerZone(player);
            }
        }

        #endregion CargoTrainTunnel

        #region PVPDelay

        private void OnPlayerRemovedFromPVPDelay(ulong playerID, string zoneID) // DynamicPVP
        {
            var player = BasePlayer.FindByID(playerID);
            if (player == null) return;
            CheckPlayerZone(player, false);
        }

        private void OnPlayerPvpDelayExpired(BasePlayer player) // RaidableBases
        {
            if (player == null) return;
            CheckPlayerZone(player, false);
        }

        private void OnPlayerPvpDelayExpiredII(BasePlayer player) // AbandonedBases
        {
            if (player == null) return;
            CheckPlayerZone(player, false);
        }

        private bool IsPlayerInPVPDelay(ulong playerID)
        {
            if (DynamicPVP != null && Convert.ToBoolean(DynamicPVP.Call("IsPlayerInPVPDelay", playerID)))
            {
                return true;
            }

            if (RaidableBases != null && Convert.ToBoolean(RaidableBases.Call("HasPVPDelay", playerID)))
            {
                return true;
            }

            if (AbandonedBases != null && Convert.ToBoolean(AbandonedBases.Call("HasPVPDelay", playerID)))
            {
                return true;
            }

            return false;
        }

        #endregion PVPDelay

        #region Commands

        private void CmdServerPVx(IPlayer iPlayer, string command, string[] args)
        {
            if (!iPlayer.IsAdmin) return;
            if (args == null || args.Length < 1) return;
            switch (args[0].ToLower())
            {
                case "0":
                case "off":
                case "false":
                    pvpAll = false;
                    foreach (var player in BasePlayer.activePlayerList)
                    {
                        CheckPlayerZone(player);
                    }
                    return;

                case "1":
                case "on":
                case "true":
                    pvpAll = true;
                    foreach (var player in BasePlayer.activePlayerList)
                    {
                        CreatePVxUI(player, PVxType.PVP);
                    }
                    return;
            }
        }

        #endregion Commands

        #region UI

        private string GetUIJson(PVxType type)
        {
            int textSize;
            string zoneText, zoneColor, textColor;
            switch (type)
            {
                case PVxType.PVE:
                    zoneText = "PVE";
                    zoneColor = configData.pveColor;
                    textColor = configData.pveTextColor;
                    textSize = configData.textSize;
                    break;

                case PVxType.PVP:
                    zoneText = "PVP";
                    zoneColor = configData.pvpColor;
                    textColor = configData.pvpTextColor;
                    textSize = configData.textSize;
                    break;

                case PVxType.PVPDelay:
                    zoneText = "PVP Delay";
                    zoneColor = configData.pvpDelayColor;
                    textColor = configData.pvpDelayTextColor;
                    textSize = configData.pvpDelayTextSize;
                    break;

                default: return null;
            }

            return new CuiElementContainer
            {
                {
                    new CuiPanel
                    {
                        Image = {Color = zoneColor},
                        RectTransform =
                        {
                            AnchorMin = configData.minAnchor,
                            AnchorMax = configData.maxAnchor,
                            OffsetMin = configData.minOffset,
                            OffsetMax = configData.maxOffset
                        }
                    },
                    configData.layer, UINAME_MAIN
                },
                {
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = zoneText, FontSize = textSize, Align = TextAnchor.MiddleCenter,
                            Color = textColor
                        },
                        RectTransform = {AnchorMin = "0.05 0.05", AnchorMax = "0.95 0.95"}
                    },
                    UINAME_MAIN, CuiHelper.GetGuid()
                }
            }.ToJson();
        }

        private void CreatePVxUI(BasePlayer player, PVxType type)
        {
            string uiJson;
            switch (type)
            {
                case PVxType.PVE:
                    uiJson = pveUIJson;
                    break;

                case PVxType.PVP:
                    uiJson = pvpUIJson;
                    break;

                case PVxType.PVPDelay:
                    uiJson = pvpDelayUIJson;
                    break;

                default: return;
            }
            CuiHelper.DestroyUi(player, UINAME_MAIN);
            CuiHelper.AddUi(player, uiJson);
        }

        private static void DestroyUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UINAME_MAIN);
        }

        #endregion UI

        #region ConfigurationFile

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Show Default PVx UI")]
            public bool showDefault = true;

            [JsonConverter(typeof(StringEnumConverter))]
            [JsonProperty(PropertyName = "Server Default PVx (pvp or pve)")]
            public PVxType defaultType = PVxType.PVE;

            [JsonProperty(PropertyName = "UI - Layer")]
            public string layer = "Hud";

            [JsonProperty(PropertyName = "UI - PVP Background Color")]
            public string pvpColor = "0.8 0.2 0.2 0.8";

            [JsonProperty(PropertyName = "UI - PVP Delay Background Color")]
            public string pvpDelayColor = "0.8 0.5 0.1 0.8";

            [JsonProperty(PropertyName = "UI - PVE Background Color")]
            public string pveColor = "0.3 0.8 0.1 0.8";

            [JsonProperty(PropertyName = "UI - PVP Text Color")]
            public string pvpTextColor = "1 1 1 1";

            [JsonProperty(PropertyName = "UI - PVP Delay Text Color")]
            public string pvpDelayTextColor = "1 1 1 1";

            [JsonProperty(PropertyName = "UI - PVE Text Color")]
            public string pveTextColor = "1 1 1 1";

            [JsonProperty(PropertyName = "UI - Text Size")]
            public int textSize = 14;

            [JsonProperty(PropertyName = "UI - PVP Delay Text Size")]
            public int pvpDelayTextSize = 12;

            [JsonProperty(PropertyName = "UI - Min Anchor")]
            public string minAnchor = "0.5 0";

            [JsonProperty(PropertyName = "UI - Max Anchor")]
            public string maxAnchor = "0.5 0";

            [JsonProperty(PropertyName = "UI - Min Offset")]
            public string minOffset = "190 30";

            [JsonProperty(PropertyName = "UI - Max Offset")]
            public string maxOffset = "250 60";
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
    }
}