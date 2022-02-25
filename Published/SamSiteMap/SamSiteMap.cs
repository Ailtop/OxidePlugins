using System.Collections.Generic;
using CompanionServer.Handlers;
using Network;
using Newtonsoft.Json;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SAM Site Map", "Arainrr", "1.4.0")]
    [Description("Mark all SAM sites on the map")]
    internal class SamSiteMap : RustPlugin
    {
        #region Fields

        private const string PERMISSION_USE = "samsitemap.use";
        private const string PREFAB_MARKER = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string PREFAB_TEXT = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";

        private static SamSiteMap _instance;

        #endregion Fields

        #region Oxide Hooks

        private void Init()
        {
            _instance = this;
            Unsubscribe(nameof(OnEntitySpawned));
            permission.RegisterPermission(PERMISSION_USE, this);
        }

        private void OnServerInitialized()
        {
            Subscribe(nameof(OnEntitySpawned));
            foreach (var severEntity in BaseNetworkable.serverEntities)
            {
                var samSite = severEntity as SamSite;
                if (samSite != null)
                {
                    CreateMapMarker(samSite);
                }
            }

            foreach (var player in BasePlayer.activePlayerList)
            {
                OnPlayerConnected(player);
            }
        }

        private void Unload()
        {
            SamSiteMapMarker.DestroyAll();
            _instance = null;
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            if (player.IsReceivingSnapshot)
            {
                timer.Once(1f, () => OnPlayerConnected(player));
                return;
            }
            if (configData.usePermission)
            {
                SamSiteMapMarker.SendSnapshotToPlayer(player);
                return;
            }
            SamSiteMapMarker.SendUpdateToPlayers();
        }

        private void OnEntitySpawned(SamSite samSite)
        {
            CreateMapMarker(samSite);
        }

        #endregion Oxide Hooks

        #region Methods

        private void CreateMapMarker(SamSite samSite)
        {
            var settings = GetSamSiteSettings(samSite);
            if (!settings.enabled)
            {
                return;
            }
            if (samSite.GetComponent<SamSiteMapMarker>() != null)
            {
                return;
            }
            samSite.gameObject.AddComponent<SamSiteMapMarker>().Init(settings, configData.usePermission, configData.checkInterval);
        }

        private SamSiteSettings GetSamSiteSettings(SamSite samSite)
        {
            return samSite.OwnerID == 0 ? configData.staticSamS : configData.playerSamS;
        }

        #endregion Methods

        #region MapMarker

        private class SamSiteMapMarker : MonoBehaviour
        {
            #region Static

            private static List<SamSiteMapMarker> _mapMarkers;

            public static void DestroyAll()
            {
                if (_mapMarkers == null) return;
                foreach (var samMapMarker in _mapMarkers)
                {
                    Destroy(samMapMarker);
                }
                _mapMarkers.Clear();
                _mapMarkers = null;
            }

            public static void SendUpdateToPlayers()
            {
                if (_mapMarkers == null) return;
                foreach (var mapMarker in _mapMarkers)
                {
                    if (!mapMarker.CanSeeMapMarker()) continue;
                    mapMarker.EnableMarkers();
                }
            }

            private static void SendSnapshotToPlayers()
            {
                if (_mapMarkers == null) return;
                foreach (var player in BasePlayer.activePlayerList)
                {
                    SendSnapshotToPlayer(player);
                }
            }

            public static void SendSnapshotToPlayer(BasePlayer player)
            {
                if (_mapMarkers == null) return;
                if (_instance.permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
                {
                    foreach (var mapMarker in _mapMarkers)
                    {
                        if (!mapMarker.CanSeeMapMarker()) continue;
                        mapMarker.UnlimitedMarkers(player.Connection);
                    }
                }
            }

            #endregion Static

            private SamSite _samSite;
            private MapMarkerGenericRadius _mapMarker;
            private MapMarkerGenericRadius _radiusMapMarker;
            private VendingMachineMapMarker _textMapMarker;
            private bool _usePermission;
            private SamSiteSettings _setting;
            private bool _tempCanShow;

            private void Awake()
            {
                if (_mapMarkers == null) _mapMarkers = new List<SamSiteMapMarker>();
                _mapMarkers.Add(this);
                _samSite = GetComponent<SamSite>();
            }

            public void Init(SamSiteSettings settings, bool usePermission, float checkInterval)
            {
                _setting = settings;
                _usePermission = usePermission;
                SpawnMapMarkers(settings.samSiteMarkerSetting, usePermission);
                _tempCanShow = CanSeeMapMarker();
                if (usePermission)
                {
                    if (_tempCanShow)
                    {
                        Show();
                    }
                }
                else
                {
                    if (!_tempCanShow)
                    {
                        Hide();
                    }
                }
                InvokeRepeating(nameof(TimedCheck), checkInterval, checkInterval);
            }

            private void SpawnMapMarkers(SamSiteMarkerSetting settings, bool usePermission)
            {
                //Text map marker
                if (!string.IsNullOrEmpty(settings.text))
                {
                    _textMapMarker = GameManager.server.CreateEntity(PREFAB_TEXT, _samSite.transform.position) as VendingMachineMapMarker;
                    if (_textMapMarker != null)
                    {
                        _textMapMarker.markerShopName = settings.text;
                        _textMapMarker.OwnerID = _samSite.OwnerID;
                        if (usePermission)
                        {
                            _textMapMarker.limitNetworking = true;
                        }
                        _textMapMarker.Spawn();
                        if (usePermission)
                        {
                            MapMarker.serverMapMarkers.Remove(_textMapMarker);
                        }
                    }
                }
                //Attack range map marker
                if (settings.samSiteRadiusMarker.enabled)
                {
                    _radiusMapMarker = GameManager.server.CreateEntity(PREFAB_MARKER, _samSite.transform.position) as MapMarkerGenericRadius;
                    if (_radiusMapMarker != null)
                    {
                        _radiusMapMarker.alpha = settings.samSiteRadiusMarker.alpha;
                        var color1 = settings.samSiteRadiusMarker.colorl;
                        if (!ColorUtility.TryParseHtmlString(color1, out _radiusMapMarker.color1))
                        {
                            _radiusMapMarker.color1 = Color.black;
                            _instance.PrintError($"Invalid range map marker color1: {color1}");
                        }
                        var color2 = settings.samSiteRadiusMarker.color2;
                        if (!ColorUtility.TryParseHtmlString(color2, out _radiusMapMarker.color2))
                        {
                            _radiusMapMarker.color2 = Color.white;
                            _instance.PrintError($"Invalid range map marker color2: {color2}");
                        }
                        _radiusMapMarker.radius = _samSite.vehicleScanRadius / 145f;
                        _radiusMapMarker.OwnerID = _samSite.OwnerID;
                        if (usePermission)
                        {
                            _radiusMapMarker.limitNetworking = true;
                        }
                        _radiusMapMarker.Spawn();
                        if (usePermission)
                        {
                            MapMarker.serverMapMarkers.Remove(_radiusMapMarker);
                        }
                        else
                        {
                            _radiusMapMarker.SendUpdate();
                        }
                    }
                }

                //Sam map marker
                _mapMarker = GameManager.server.CreateEntity(PREFAB_MARKER, _samSite.transform.position) as MapMarkerGenericRadius;
                if (_mapMarker != null)
                {
                    _mapMarker.alpha = settings.alpha;
                    var color1 = settings.colorl;
                    if (!ColorUtility.TryParseHtmlString(color1, out _mapMarker.color1))
                    {
                        _mapMarker.color1 = Color.black;
                        _instance.PrintError($"Invalid map marker color1: {color1}");
                    }
                    var color2 = settings.color2;
                    if (!ColorUtility.TryParseHtmlString(color2, out _mapMarker.color2))
                    {
                        _mapMarker.color2 = Color.white;
                        _instance.PrintError($"Invalid map marker color2: {color2}");
                    }
                    _mapMarker.radius = settings.radius;
                    _mapMarker.OwnerID = _samSite.OwnerID;
                    if (usePermission)
                    {
                        _mapMarker.limitNetworking = true;
                    }
                    _mapMarker.Spawn();
                    if (usePermission)
                    {
                        MapMarker.serverMapMarkers.Remove(_mapMarker);
                    }
                    else
                    {
                        _mapMarker.SendUpdate();
                    }
                }
            }

            private void TimedCheck()
            {
                var canShow = CanSeeMapMarker();
                if (canShow != _tempCanShow)
                {
                    if (canShow)
                    {
                        Show();
                    }
                    else
                    {
                        Hide();
                    }
                }
                _tempCanShow = canShow;
            }

            private bool CanSeeMapMarker()
            {
                if (_setting.hideWhenNoPower)
                {
                    return _samSite.IsPowered();
                }
                if (_setting.hideWhenNoAmmo)
                {
                    return _samSite.HasAmmo();
                }
                return true;
            }

            private void Show()
            {
                // _instance.PrintError($"Show : {_usePermission} | {_samSite.OwnerID}");
                if (_usePermission)
                {
                    SendSnapshotToPlayers();
                }
                else
                {
                    EnableMarkers();
                }
            }

            private void Hide()
            {
                // _instance.PrintError($"Hide : {_usePermission} | {_samSite.OwnerID}");
                if (_usePermission)
                {
                    LimitedMarkers();
                }
                else
                {
                    DisableMarkers();
                }
            }

            #region Methods

            private void EnableMarkers()
            {
                if (_mapMarker != null)
                {
                    if (_mapMarker.limitNetworking)
                    {
                        _mapMarker.limitNetworking = false;
                        _mapMarker.SendNetworkUpdateImmediate();
                    }
                    _mapMarker.SendUpdate();
                }
                if (_radiusMapMarker != null)
                {
                    if (_radiusMapMarker.limitNetworking)
                    {
                        _radiusMapMarker.limitNetworking = false;
                        _radiusMapMarker.SendNetworkUpdateImmediate();
                    }
                    _radiusMapMarker.SendUpdate();
                }
                if (_textMapMarker != null)
                {
                    if (_textMapMarker.limitNetworking)
                    {
                        _textMapMarker.limitNetworking = false;
                        _textMapMarker.SendNetworkUpdateImmediate();
                    }
                    _textMapMarker.SendNetworkUpdate();
                }
            }

            private void DisableMarkers()
            {
                if (_mapMarker != null)
                {
                    _mapMarker.limitNetworking = true;
                }
                if (_radiusMapMarker != null)
                {
                    _radiusMapMarker.limitNetworking = true;
                }
                if (_textMapMarker != null)
                {
                    _textMapMarker.limitNetworking = true;
                }
            }

            private void LimitedMarkers()
            {
                if (_mapMarker != null)
                {
                    _mapMarker.limitNetworking = false;
                    _mapMarker.limitNetworking = true;
                }
                if (_radiusMapMarker != null)
                {
                    _radiusMapMarker.limitNetworking = false;
                    _radiusMapMarker.limitNetworking = true;
                }
                if (_textMapMarker != null)
                {
                    _textMapMarker.limitNetworking = false;
                    _textMapMarker.limitNetworking = true;
                }
            }

            private void UnlimitedMarkers(Connection connection)
            {
                if (_mapMarker != null)
                {
                    _mapMarker.SendAsSnapshot(connection);
                    _mapMarker.SendUpdate();
                }
                if (_radiusMapMarker != null)
                {
                    _radiusMapMarker.SendAsSnapshot(connection);
                    _radiusMapMarker.SendUpdate();
                }
                if (_textMapMarker != null)
                {
                    _textMapMarker.SendAsSnapshot(connection);
                }
            }

            #endregion Methods

            private void OnDestroy()
            {
                if (_mapMarker != null && !_mapMarker.IsDestroyed)
                {
                    _mapMarker.Kill();
                }
                if (_radiusMapMarker != null && !_radiusMapMarker.IsDestroyed)
                {
                    _radiusMapMarker.Kill();
                }
                if (_textMapMarker != null && !_textMapMarker.IsDestroyed)
                {
                    _textMapMarker.Kill();
                }
                _mapMarkers?.Remove(this);
            }
        }

        #endregion MapMarker

        #region ConfigurationFile

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Use permission")]
            public bool usePermission = false;

            [JsonProperty(PropertyName = "Time interval to check the show of markers (seconds)")]
            public float checkInterval = 5f;

            [JsonProperty(PropertyName = "Static SAM settings")]
            public SamSiteSettings staticSamS = new SamSiteSettings
            {
                enabled = true,
                samSiteMarkerSetting = new SamSiteMarkerSetting
                {
                    radius = 0.15f,
                    colorl = "#FF4500",
                    color2 = "#0000FF",
                    alpha = 1f,
                    text = "static sam",
                    samSiteRadiusMarker = new SamSiteRadiusMarker
                    {
                        colorl = "#FFFF00",
                        color2 = "#FFFFF0",
                        alpha = 0.5f,
                    }
                },
            };

            [JsonProperty(PropertyName = "Player's SAM settings")]
            public SamSiteSettings playerSamS = new SamSiteSettings
            {
                enabled = true,
                samSiteMarkerSetting = new SamSiteMarkerSetting
                {
                    radius = 0.08f,
                    colorl = "#00FF00",
                    color2 = "#0000FF",
                    alpha = 1f,
                    text = "player's sam",
                    samSiteRadiusMarker = new SamSiteRadiusMarker
                    {
                        colorl = "#FFFF00",
                        color2 = "#FFFFF0",
                        alpha = 0.5f,
                    }
                },
            };
        }

        private class SamSiteSettings
        {
            [JsonProperty(PropertyName = "Enabled map marker")]
            public bool enabled = true;

            [JsonProperty(PropertyName = "Hide when no power")]
            public bool hideWhenNoPower = true;

            [JsonProperty(PropertyName = "Hide when no ammo")]
            public bool hideWhenNoAmmo = true;

            [JsonProperty(PropertyName = "SAM map marker")]
            public SamSiteMarkerSetting samSiteMarkerSetting = new SamSiteMarkerSetting();
        }

        private class SamSiteMarkerSetting
        {
            [JsonProperty(PropertyName = "Map marker radius")]
            public float radius = 0.08f;

            [JsonProperty(PropertyName = "Map marker color1")]
            public string colorl = "#00FF00";

            [JsonProperty(PropertyName = "Map marker color2")]
            public string color2 = "#0000FF";

            [JsonProperty(PropertyName = "Map marker alpha")]
            public float alpha = 1f;

            [JsonProperty(PropertyName = "Map marker text")]
            public string text = "sam";

            [JsonProperty(PropertyName = "SAM attack range map marker")]
            public SamSiteRadiusMarker samSiteRadiusMarker = new SamSiteRadiusMarker();
        }

        private class SamSiteRadiusMarker
        {
            [JsonProperty(PropertyName = "Enabled Sam attack range map marker")]
            public bool enabled = false;

            [JsonProperty(PropertyName = "Range map marker color1")]
            public string colorl = "#FFFF00";

            [JsonProperty(PropertyName = "Range map marker color2")]
            public string color2 = "#FFFFF0";

            [JsonProperty(PropertyName = "Range map marker alpha")]
            public float alpha = 0.5f;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                configData = Config.ReadObject<ConfigData>();
                if (configData == null)
                    LoadDefaultConfig();
            }
            catch
            {
                PrintError("The configuration file is corrupted");
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