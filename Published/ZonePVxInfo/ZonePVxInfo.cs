using System;
using System.Collections.Generic;
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
        private const string UinameMain = "ZonePVxInfoUI";
        private bool _pvpAll;

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
            if (_pvpAll)
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

        private string GetZoneName(string zoneId)
        {
            return (string)ZoneManager.Call("GetZoneName", zoneId);
        }

        private string[] GetPlayerZoneIDs(BasePlayer player)
        {
            return (string[])ZoneManager.Call("GetPlayerZoneIDs", player);
        }

        private float GetZoneRadius(string zoneId)
        {
            var obj = ZoneManager.Call("GetZoneRadius", zoneId); ;
            if (obj is float)
            {
                return (float)obj;
            }
            return 0f;
        }

        private Vector3 GetZoneSize(string zoneId)
        {
            var obj = ZoneManager.Call("GetZoneSize", zoneId); ;
            if (obj is Vector3)
            {
                return (Vector3)obj;
            }
            return Vector3.zero;
        }

        private void OnEnterZone(string zoneId, BasePlayer player)
        {
            NextTick(() => CheckPlayerZone(player));
        }

        private void OnExitZone(string zoneId, BasePlayer player)
        {
            NextTick(() => CheckPlayerZone(player));
        }

        private void CheckPlayerZone(BasePlayer player, bool checkPVPDelay = true)
        {
            if (_pvpAll || player == null || !player.IsConnected || !player.userID.IsSteamId()) return;
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
            foreach (var zoneId in zoneIDs)
            {
                var zoneName = GetZoneName(zoneId);
                if (string.IsNullOrEmpty(zoneName)) continue;
                float zoneRadius;
                var zoneSize = GetZoneSize(zoneId);
                if (zoneSize != Vector3.zero)
                {
                    zoneRadius = (zoneSize.x + zoneSize.z) / 2;
                }
                else
                {
                    zoneRadius = GetZoneRadius(zoneId);
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
            if (_pvpAll) return;
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
            if (_pvpAll) return;
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
            if (_pvpAll) return;
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

        private void OnPlayerRemovedFromPVPDelay(ulong playerId, string zoneId) // DynamicPVP
        {
            var player = BasePlayer.FindByID(playerId);
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
                    _pvpAll = false;
                    foreach (var player in BasePlayer.activePlayerList)
                    {
                        CheckPlayerZone(player);
                    }
                    return;

                case "1":
                case "on":
                case "true":
                    _pvpAll = true;
                    foreach (var player in BasePlayer.activePlayerList)
                    {
                        CreatePVxUI(player, PVxType.PVP);
                    }
                    return;
            }
        }

        #endregion Commands

        #region UI

        private void CreatePVxUI(BasePlayer player, PVxType type)
        {
            UiSettings settings;
            if (!configData.UISettings.TryGetValue(type, out settings) || string.IsNullOrEmpty(settings.Json))
            {
                return;
            }
            CuiHelper.DestroyUi(player, UinameMain);
            CuiHelper.AddUi(player, settings.Json);
        }

        private static void DestroyUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UinameMain);
        }

        private static string GetCuiJson(UiSettings settings)
        {
            return new CuiElementContainer
            {
                {
                    new CuiPanel
                    {
                        Image =
                        {
                            Color = settings.BackgroundColor,
                            FadeIn = settings.FadeIn,
                        },
                        RectTransform =
                        {
                            AnchorMin = settings.MinAnchor,
                            AnchorMax = settings.MaxAnchor,
                            OffsetMin = settings.MinOffset,
                            OffsetMax = settings.MaxOffset,
                        },
                        FadeOut = settings.FadeOut,
                    },
                    settings.Layer, UinameMain
                },
                {
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = settings.Text,
                            FontSize = settings.TextSize,
                            Align = TextAnchor.MiddleCenter,
                            Color = settings.TextColor,
                            FadeIn = settings.FadeIn,
                        },
                        RectTransform = {AnchorMin = "0.05 0.05", AnchorMax = "0.95 0.95"},
                        FadeOut = settings.FadeOut,
                    },
                    UinameMain, CuiHelper.GetGuid()
                }
            }.ToJson();
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

            [JsonProperty(PropertyName = "Pvx UI Settings")]
            public Dictionary<PVxType, UiSettings> UISettings { get; set; } = new Dictionary<PVxType, UiSettings>
            {
                [PVxType.PVE] = new UiSettings
                {
                    Text = "PVE",
                    TextSize = 14,
                    TextColor = "1 1 1 1",
                    BackgroundColor = "0.3 0.8 0.1 0.8"
                },
                [PVxType.PVP] = new UiSettings
                {
                    Text = "PVP",
                    TextSize = 14,
                    TextColor = "1 1 1 1",
                    BackgroundColor = "0.8 0.2 0.2 0.8"
                },
                [PVxType.PVPDelay] = new UiSettings
                {
                    Text = "PVP Delay",
                    TextSize = 12,
                    TextColor = "1 1 1 1",
                    BackgroundColor = "0.8 0.5 0.1 0.8"
                },
            };
        }

        private class UiSettings
        {
            [JsonProperty(PropertyName = "Min Anchor")]
            public string MinAnchor { get; set; } = "0.5 0";

            [JsonProperty(PropertyName = "Max Anchor")]
            public string MaxAnchor { get; set; } = "0.5 0";

            [JsonProperty(PropertyName = "Min Offset")]
            public string MinOffset { get; set; } = "190 30";

            [JsonProperty(PropertyName = "Max Offset")]
            public string MaxOffset { get; set; } = "250 60";

            [JsonProperty(PropertyName = "Layer")]
            public string Layer { get; set; } = "Hud";

            [JsonProperty(PropertyName = "Text")]
            public string Text { get; set; } = "PVP";

            [JsonProperty(PropertyName = "Text Size")]
            public int TextSize { get; set; } = 12;

            [JsonProperty(PropertyName = "Text Color")]
            public string TextColor { get; set; } = "1 1 1 1";

            [JsonProperty(PropertyName = "Background Color")]
            public string BackgroundColor { get; set; } = "0.8 0.5 0.1 0.8";

            [JsonProperty(PropertyName = "Fade In")]
            public float FadeIn { get; set; } = 0.25f;

            [JsonProperty(PropertyName = "Fade Out")]
            public float FadeOut { get; set; } = 0.25f;

            private string _json;

            [JsonIgnore]
            public string Json
            {
                get
                {
                    if (string.IsNullOrEmpty(_json))
                    {
                        _json = GetCuiJson(this);
                    }
                    return _json;
                }
            }
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