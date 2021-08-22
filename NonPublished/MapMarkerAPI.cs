using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Map Marker API", "Arainrr", "1.0.0")]
    [Description("")]
    internal class MapMarkerAPI : RustPlugin
    {
        //TODO 添加选项，是否可以在App中看到它们。设置appType=0即可？？？

        #region Fields

        private const string PrefabGenericRadius = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string PrefabVending = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";
        private const string PrefabExplosion = "assets/prefabs/tools/map/explosionmarker.prefab";
        private const string PrefabCargoShip = "assets/prefabs/tools/map/cargomarker.prefab";
        private const string PrefabCh47 = "assets/prefabs/tools/map/ch47marker.prefab";
        private const string PrefabHackableCrate = "assets/prefabs/tools/map/cratemarker.prefab";
        private const string PrefabDeliveryDrone = "assets/prefabs/misc/marketplace/deliverydronemarker.prefab";

        private static MapMarkerAPI _instance;

        [Flags]
        private enum MapMarkerType
        {
            None = 0,
            Generic = 1,
            Vending = 1 << 1,
            CargoShip = 1 << 2,
            Ch47 = 1 << 3,
            HackableCrate = 1 << 4,
            Explosion = 1 << 5,
            DeliveryDrone = 1 << 6,
        }

        #endregion Fields

        #region Oxide Hooks

        private void Init()
        {
            _instance = this;
        }

        private void OnServerInitialized()
        {
        }

        private void Unload()
        {
            _instance = null;
        }

        private void OnPlayerConnected(BasePlayer player)
        {
        }

        private void OnEntitySpawned(SamSite samSite)
        {
        }

        private void OnEntityKill(SamSite samSite)
        {
        }

        private void OnEntityKill(MapMarker mapMarker)
        {
        }

        #endregion Oxide Hooks

        private static string GetMapMarkerPrefab(MapMarkerType mapMarkerType)
        {
            switch (mapMarkerType)
            {
                case MapMarkerType.Generic:
                    return PrefabGenericRadius;

                case MapMarkerType.Vending:
                    return PrefabVending;

                case MapMarkerType.CargoShip:
                    return PrefabCargoShip;

                case MapMarkerType.Ch47:
                    return PrefabCh47;

                case MapMarkerType.HackableCrate:
                    return PrefabHackableCrate;

                case MapMarkerType.Explosion:
                    return PrefabExplosion;

                case MapMarkerType.DeliveryDrone:
                    return PrefabDeliveryDrone;

                default:
                    _instance.PrintError($"Unknown map marker type: {mapMarkerType}");
                    return null;
            }
        }

        private static T PreSpawnMapMarkerEntity<T>(MapMarkerType mapMarkerType, Vector3 position, Quaternion rotation)
            where T : MapMarker
        {
            string prefab = GetMapMarkerPrefab(mapMarkerType);
            var entity = GameManager.server.CreateEntity(prefab, position, rotation) as T;
            if (entity == null)
            {
                _instance.PrintError($"The prefab({prefab}) for {nameof(T)} is empty");
                return null;
            }

            return entity;
        }

        // 圆圈标志
        // 圆圈标志带文字的
        // 售货机标志
        private class UpdateQueue
        {
            public static UpdateMapMarkerScanQueue updateMapMarkerScanQueue = new UpdateMapMarkerScanQueue();

            public static void RunQueue()
            {
                // 只处理0.5秒，处理多了就不处理了
                updateMapMarkerScanQueue.RunQueue(0.5);
            }

            public class UpdateMapMarkerScanQueue : ObjectWorkQueue<BaseMapMarker>
            {
                protected override void RunJob(BaseMapMarker entity)
                {
                    if (ShouldAdd(entity))
                    {
                        entity.TargetScan();
                    }
                }

                protected override bool ShouldAdd(BaseMapMarker entity)
                {
                    if (base.ShouldAdd(entity))
                    {
                        return BaseEntityEx.IsValid(entity);
                    }

                    return false;
                }
            }
        }

        #region API

        private void CreateBaseMapMarker(MapMarkerType mapMarkerType, Vector3 position, Quaternion rotation)
        {
            BaseMapMarker mapMarker = new BaseMapMarker();
            BaseMapMarkerConfig mapMarkerConfig = new BaseMapMarkerConfig();
            mapMarkerConfig.disabledInApp = true;
            mapMarkerConfig.parent = null;
            mapMarkerConfig.duration = 0f;
            mapMarkerConfig.OwnerID = 0uL;
            mapMarkerConfig.AuthorizedPlayers = new HashSet<ulong>();
            mapMarker.mapMarkerConfig = mapMarkerConfig;

            mapMarker.CreateMapMarker(mapMarkerType, position, rotation);
            MapMarkerRegistry.Instance.RegisterMapMarker(mapMarker);
        }

        private void CreateRadiusMapMarker(MapMarkerType mapMarkerType, Vector3 position, Quaternion rotation)
        {
            RadiusMapMarker mapMarker = new RadiusMapMarker();
            RadiusMapMarkerConfig mapMarkerConfig = new RadiusMapMarkerConfig();
            mapMarkerConfig.disabledInApp = true;
            mapMarkerConfig.parent = null;
            mapMarkerConfig.duration = 0f;
            mapMarkerConfig.OwnerID = 0uL;
            mapMarkerConfig.AuthorizedPlayers = new HashSet<ulong>();

            mapMarkerConfig.radius = 0.08f;
            mapMarkerConfig.mainColor = "#FF4500";
            mapMarkerConfig.outlineColor = "#0000FF";
            mapMarkerConfig.alpha = 1f;
            mapMarker.mapMarkerConfig = mapMarkerConfig;

            mapMarker.CreateMapMarker(mapMarkerType, position, rotation);
            MapMarkerRegistry.Instance.RegisterMapMarker(mapMarker);
        }

        private void CreateVendingMapMarker(MapMarkerType mapMarkerType, Vector3 position, Quaternion rotation)
        {
            VendingMapMarker mapMarker = new VendingMapMarker();
            VendingMapMarkerConfig mapMarkerConfig = new VendingMapMarkerConfig();
            mapMarkerConfig.disabledInApp = true;
            mapMarkerConfig.parent = null;
            mapMarkerConfig.duration = 0f;
            mapMarkerConfig.OwnerID = 0uL;
            mapMarkerConfig.AuthorizedPlayers = new HashSet<ulong>();

            mapMarkerConfig.text = "map marker";
            mapMarkerConfig.isBusy = false;
            mapMarker.mapMarkerConfig = mapMarkerConfig;

            mapMarker.CreateMapMarker(mapMarkerType, position, rotation);
            MapMarkerRegistry.Instance.RegisterMapMarker(mapMarker);
        }

        private void CreateRadiusVendingMapMarker(MapMarkerType mapMarkerType, Vector3 position, Quaternion rotation)
        {
            RadiusVendingMapMarker mapMarker = new RadiusVendingMapMarker();
            RadiusVendingMapMarkerConfig mapMarkerConfig = new RadiusVendingMapMarkerConfig();
            mapMarkerConfig.disabledInApp = true;
            mapMarkerConfig.parent = null;
            mapMarkerConfig.duration = 0f;
            mapMarkerConfig.OwnerID = 0uL;
            mapMarkerConfig.AuthorizedPlayers = new HashSet<ulong>();

            mapMarkerConfig.text = "map marker";
            mapMarkerConfig.isBusy = false;

            mapMarkerConfig.radius = 0.08f;
            mapMarkerConfig.mainColor = "#FF4500";
            mapMarkerConfig.outlineColor = "#0000FF";
            mapMarkerConfig.alpha = 1f;
            mapMarker.mapMarkerConfig = mapMarkerConfig;

            mapMarker.CreateMapMarker(mapMarkerType, position, rotation);
            MapMarkerRegistry.Instance.RegisterMapMarker(mapMarker);
        }

        #endregion API

        private abstract class AbstractMapMarker
        {
            // private Transform _parent;
            // private bool _shouldRotation;

            public abstract void CreateMapMarker(MapMarkerType mapMarkerType, Vector3 position, Quaternion rotation);

            public abstract void UpdateMapMarker();
        }

        private class BaseMapMarker : AbstractMapMarker
        {
            public MapMarker mapMarker { get; set; }
            public BaseMapMarkerConfig mapMarkerConfig { get; set; }

            public override void CreateMapMarker(MapMarkerType mapMarkerType, Vector3 position, Quaternion rotation)
            {
                mapMarker = PreSpawnMapMarkerEntity<MapMarker>(mapMarkerType, position, rotation);
                mapMarkerConfig.PreProcessMapMarkerEntity(mapMarker);
                mapMarker.Spawn();
            }

            public override void UpdateMapMarker()
            {
                mapMarker.SendNetworkUpdate();
            }
        }

        private class RadiusMapMarker : AbstractMapMarker
        {
            public MapMarkerGenericRadius mapMarker { get; set; }
            public RadiusMapMarkerConfig mapMarkerConfig { get; set; }

            public override void CreateMapMarker(MapMarkerType mapMarkerType, Vector3 position, Quaternion rotation)
            {
                mapMarker = PreSpawnMapMarkerEntity<MapMarkerGenericRadius>(mapMarkerType, position, rotation);
                mapMarkerConfig.PreProcessRadiusMapMarkerEntity(mapMarker);
                mapMarker.Spawn();
            }

            public override void UpdateMapMarker()
            {
                mapMarker.SendUpdate();
            }
        }

        private class VendingMapMarker : AbstractMapMarker
        {
            public VendingMachineMapMarker mapMarker { get; set; }
            public VendingMapMarkerConfig mapMarkerConfig { get; set; }

            public override void CreateMapMarker(MapMarkerType mapMarkerType, Vector3 position, Quaternion rotation)
            {
                mapMarker = PreSpawnMapMarkerEntity<VendingMachineMapMarker>(mapMarkerType, position, rotation);
                mapMarkerConfig.PreProcessVendingMapMarkerEntity(mapMarker);
                mapMarker.Spawn();
            }

            public override void UpdateMapMarker()
            {
                mapMarker.SendNetworkUpdate();
            }
        }

        private class RadiusVendingMapMarker : AbstractMapMarker
        {
            public MapMarkerGenericRadius radiusMapMarker { get; set; }
            public VendingMachineMapMarker vendingMapMarker { get; set; }
            public RadiusVendingMapMarkerConfig mapMarkerConfig { get; set; }

            public override void CreateMapMarker(MapMarkerType mapMarkerType, Vector3 position, Quaternion rotation)
            {
                radiusMapMarker = PreSpawnMapMarkerEntity<MapMarkerGenericRadius>(mapMarkerType, position, rotation);
                vendingMapMarker = PreSpawnMapMarkerEntity<VendingMachineMapMarker>(mapMarkerType, position, rotation);
                mapMarkerConfig.PreProcessMapMarkerEntity(radiusMapMarker, vendingMapMarker);
                radiusMapMarker.Spawn();
                vendingMapMarker.Spawn();

                vendingMapMarker.SetParent(radiusMapMarker, true);
            }

            public override void UpdateMapMarker()
            {
                radiusMapMarker.SendUpdate();
                vendingMapMarker.SendNetworkUpdate();
            }
        }

        #region Helpers

        private static Color ParseHtmlString(string htmlString)
        {
            Color color;
            if (!ColorUtility.TryParseHtmlString(htmlString, out color))
            {
                color = Color.black;
                _instance.PrintError($"Invalid map marker color: {htmlString}");
            }

            return color;
        }

        private static void SetupRadiusMapMarkerEntity(MapMarkerGenericRadius mapMarker,
            IRadiusMapMarkerConfig iRadiusMapMarker)
        {
            mapMarker.radius = iRadiusMapMarker.radius;
            mapMarker.alpha = iRadiusMapMarker.alpha;
            mapMarker.color1 = ParseHtmlString(iRadiusMapMarker.mainColor);
            mapMarker.color2 = ParseHtmlString(iRadiusMapMarker.outlineColor);
        }

        private static void SetupVendingMapMarkerEntity(VendingMachineMapMarker mapMarker,
            IVendingMapMarkerConfig iVendingMapMarker)
        {
            mapMarker.markerShopName = iVendingMapMarker.text;
            mapMarker.SetFlag(BaseEntity.Flags.Busy, iVendingMapMarker.isBusy);
        }

        #endregion Helpers

        private class BaseMapMarkerConfig : IMapMarkerConfig
        {
            public string uid { get; set; }
            public bool disabledInApp { get; set; }
            public Transform parent { get; set; }
            public float duration { get; set; }
            public ulong OwnerID { get; set; }
            public HashSet<ulong> AuthorizedPlayers { get; set; }

            public void PreProcessMapMarkerEntity(MapMarker mapMarker)
            {
                SetupMapMarkerEntity(mapMarker);
            }

            protected void SetupMapMarkerEntity(MapMarker mapMarker)
            {
                if (HasAuthor)
                {
                    mapMarker._limitedNetworking = true;
                }

                if (disabledInApp)
                {
                    mapMarker.appType = 0;
                }

                if (parent != null)
                {
                    mapMarker.transform.SetParent(parent);
                }

                mapMarker.OwnerID = OwnerID;
            }

            public bool HasAuthor => AuthorizedPlayers != null && AuthorizedPlayers.Count > 0;

            public bool IsAuthorized(ulong playerId)
            {
                return AuthorizedPlayers != null && AuthorizedPlayers.Contains(playerId);
            }
        }

        private class RadiusMapMarkerConfig : BaseMapMarkerConfig, IRadiusMapMarkerConfig
        {
            public float radius { get; set; }
            public string mainColor { get; set; }
            public string outlineColor { get; set; }
            public float alpha { get; set; }

            public void PreProcessRadiusMapMarkerEntity(MapMarkerGenericRadius mapMarker)
            {
                if (HasAuthor)
                {
                    mapMarker._limitedNetworking = true;
                }

                SetupRadiusMapMarkerEntity(mapMarker, this);
                mapMarker.OwnerID = OwnerID;
            }
        }

        private class VendingMapMarkerConfig : BaseMapMarkerConfig, IVendingMapMarkerConfig
        {
            public string text { get; set; }
            public bool isBusy { get; set; }

            public void PreProcessVendingMapMarkerEntity(VendingMachineMapMarker mapMarker)
            {
                if (HasAuthor)
                {
                    mapMarker._limitedNetworking = true;
                }

                SetupVendingMapMarkerEntity(mapMarker, this);
                mapMarker.OwnerID = OwnerID;
            }
        }

        private class RadiusVendingMapMarkerConfig : BaseMapMarkerConfig, IVendingMapMarkerConfig, IRadiusMapMarkerConfig
        {
            public string text { get; set; }
            public bool isBusy { get; set; }
            public float radius { get; set; }
            public string mainColor { get; set; }
            public string outlineColor { get; set; }
            public float alpha { get; set; }

            public void PreProcessMapMarkerEntity(MapMarkerGenericRadius radiusMapMarker,
                VendingMachineMapMarker vendingMapMarker)
            {
                radiusMapMarker.SetParent(vendingMapMarker);
                if (HasAuthor)
                {
                    vendingMapMarker._limitedNetworking = radiusMapMarker._limitedNetworking = true;
                }

                SetupRadiusMapMarkerEntity(radiusMapMarker, this);
                SetupVendingMapMarkerEntity(vendingMapMarker, this);
                radiusMapMarker.OwnerID = vendingMapMarker.OwnerID = OwnerID;
            }
        }

        private interface IMapMarkerConfig
        {
            string uid { get; set; }
            bool disabledInApp { get; set; }
            Transform parent { get; set; }
            float duration { get; set; }
            ulong OwnerID { get; set; }
            HashSet<ulong> AuthorizedPlayers { get; set; }
        }

        private interface IVendingMapMarkerConfig
        {
            string text { get; set; }
            bool isBusy { get; set; }
        }

        private interface IRadiusMapMarkerConfig
        {
            float radius { get; set; }
            string mainColor { get; set; }
            string outlineColor { get; set; }
            float alpha { get; set; }
        }

        private class MapMarkerRegistry : SingletonComponent<MapMarkerRegistry>
        {
            public static void CreateMe()
            {
                new GameObject(nameof(MapMarkerRegistry)).AddComponent<MapMarkerRegistry>();
            }

            public static void DestroyMe()
            {
                if (Instance != null)
                {
                    Destroy(Instance.gameObject);
                }
            }

            private readonly List<AbstractMapMarker> _mapMarkers = new List<AbstractMapMarker>();

            public void RegisterMapMarker(AbstractMapMarker mapMarker)
            {
                _mapMarkers.Add(mapMarker);
            }

            public void UnRegisterMapMarker(AbstractMapMarker mapMarker)
            {
                _mapMarkers.Remove(mapMarker);
            }

            public void UpdateMapMarker()
            {
                foreach (var mapMarker in _mapMarkers)
                {
                    mapMarker.UpdateMapMarker();
                }
            }

            public void Update()
            {
            }
        }

        // private class MapMarkerRegistry
        // {
        //     private static MapMarkerRegistry _instance;
        //
        //     public static MapMarkerRegistry Instance => _instance ?? (_instance = new MapMarkerRegistry());
        //
        //     private readonly List<AbstractMapMarker> _mapMarkers = new List<AbstractMapMarker>();
        //
        //     public void RegisterMapMarker(AbstractMapMarker mapMarker)
        //     {
        //         _mapMarkers.Add(mapMarker);
        //     }
        //
        //     public void UnRegisterMapMarker(AbstractMapMarker mapMarker)
        //     {
        //         _mapMarkers.Remove(mapMarker);
        //     }
        //
        //     public void UpdateMapMarker()
        //     {
        //         foreach (var mapMarker in _mapMarkers)
        //         {
        //             mapMarker.UpdateMapMarker();
        //         }
        //     }
        // }
    }
}