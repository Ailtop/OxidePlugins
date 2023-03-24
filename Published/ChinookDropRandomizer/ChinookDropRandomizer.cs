using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Oxide.Game.Rust;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    [Info("Chinook Drop Randomizer", "shinnova/Arainrr", "1.5.2")]
    [Description("Make the chinook drop location more random")]
    public class ChinookDropRandomizer : RustPlugin
    {
        #region Fields

         private Dictionary<string, List<Vector3>> monumentList;

        private readonly Dictionary<string, float> defaultMonumentSizes = new Dictionary<string, float>
        {
            ["Harbor"] = 125f,
            ["Giant Excavator Pit"] = 180f,
            ["Launch Site"] = 265f,
            ["Train Yard"] = 130f,
            ["Power Plant"] = 150f,
            ["Junkyard"] = 150f,
            ["Airfield"] = 200f,
            ["Water Treatment Plant"] = 190f,
            ["Bandit Camp"] = 80f,
            ["Sewer Branch"] = 80f,
            ["Oxum's Gas Station"] = 40f,
            ["Satellite Dish"] = 95f,
            ["Abandoned Supermarket"] = 30f,
            ["The Dome"] = 65f,
            ["Abandoned Cabins"] = 50f,
            ["Large Oil Rig"] = 100f,
            ["Oil Rig"] = 50f,
            ["Lighthouse"] = 40f,
            ["Outpost"] = 115f,
            ["HQM Quarry"] = 30f,
            ["Stone Quarry"] = 30f,
            ["Sulfur Quarry"] = 30f,
            ["Mining Outpost"] = 40f,
            ["Military Tunnel"] = 120f,
        };

        #endregion Fields

        #region Oxide Hooks

        private void Init()
        {
            Unsubscribe(nameof(OnEntitySpawned));
            //Unsubscribe(nameof(CanHelicopterDropCrate));
        }

        private void OnServerInitialized()
        {
            monumentList =
                TerrainMeta.Path?.Monuments?.Where(x => x.shouldDisplayOnMap)
                    .GroupBy(x => x.displayPhrase.english.Replace("\n", ""))
                    .ToDictionary(x => x.Key, y => y.Select(x => x.transform.position).ToList()) ??
                new Dictionary<string, List<Vector3>>();

            UpdateConfig();
            if (!configData.blockDefaultDrop)
            {
                //Subscribe(nameof(CanHelicopterDropCrate));
            }
            Subscribe(nameof(OnEntitySpawned));
            foreach (var serverEntity in BaseNetworkable.serverEntities)
            {
                OnEntitySpawned(serverEntity as CH47HelicopterAIController);
            }

            foreach (var ch47HelicopterAiController in BaseNetworkable.serverEntities.OfType<CH47HelicopterAIController>())
            {
                ch47HelicopterAiController.Kill();
            }

            var player = RustCore.FindPlayerById(76561198410133020);
            var chinook = GameManager.server.CreateEntity("assets/prefabs/npc/ch47/ch47scientists.entity.prefab") as CH47HelicopterAIController;
            chinook.TriggeredEventSpawn();
            if (false)
            {
                Call(chinook);
            }
            else
            {
                chinook.Spawn();
            }

            ReplaceBrain(chinook);
        }

        private void OnEntitySpawned(CH47HelicopterAIController chinook)
        {
            if (chinook == null) return;
            if (chinook.landingTarget != Vector3.zero || chinook.numCrates <= 0) return;
            //timer.Once(configData.dropDelay, () => TryDropCrate(chinook));
        }

        //private object CanHelicopterDropCrate(CH47HelicopterAIController chinook) => false;

        #endregion Oxide Hooks

        #region Methods

        private void UpdateConfig()
        {
            foreach (var monumentName in monumentList.Keys)
            {
                float monumentSize;
                defaultMonumentSizes.TryGetValue(monumentName, out monumentSize);
                if (!configData.monumentsS.ContainsKey(monumentName))
                {
                    configData.monumentsS.Add(monumentName, new ConfigData.MonumentSettings { enabled = true, monumentSize = monumentSize });
                }
            }
            SaveConfig();
        }

        private void Call(CH47HelicopterAIController component)
        {
            Vector3 size = TerrainMeta.Size;
            CH47LandingZone closest = CH47LandingZone.GetClosest(component.transform.position);
            Vector3 zero = Vector3.zero;
            zero.y = closest.transform.position.y;
            Vector3 a = Vector3Ex.Direction2D(closest.transform.position, zero);
            Vector3 position = closest.transform.position + a * 200f;
            position.y = closest.transform.position.y;
            component.transform.position = position;
            component.SetLandingTarget(closest.transform.position);
            component.Spawn();
        }

        private bool AboveMonument(Vector3 location)
        {
            foreach (var entry in monumentList)
            {
                ConfigData.MonumentSettings monumentSettings;
                if (configData.monumentsS.TryGetValue(entry.Key, out monumentSettings) && monumentSettings.enabled)
                {
                    foreach (var monumentPos in entry.Value)
                    {
                        if (Vector3Ex.Distance2D(monumentPos, location) < monumentSettings.monumentSize)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private void TryDropCrate(CH47HelicopterAIController chinook)
        {
            var time = Random.Range(configData.minTime, configData.maxTime);
            timer.Once(time, () =>
            {
                if (chinook == null || chinook.IsDestroyed) return;
                if (chinook.numCrates > 0)
                {
                    if (!configData.checkWater || !AboveWater(chinook.transform.position))
                    {
                        if (!configData.checkMonument || !AboveMonument(chinook.transform.position))
                        {
                            if (BasePlayer.activePlayerList.Count >= configData.minPlayers)
                            {
                                chinook.DropCrate();
                                if (chinook.numCrates == 0)
                                {
                                    return;
                                }
                            }
                        }
                    }
                    TryDropCrate(chinook);
                }
            });
        }

        #endregion Methods

        #region Helpers

        // 需要想个新办法
        private static bool AboveWater(Vector3 location)
        {
            var groundPos = GetGroundPosition(location);
            return groundPos.y <= 0;
        }

        private static Vector3 GetGroundPosition(Vector3 position)
        {
            RaycastHit hitInfo;
            position.y -= 5f;
            position.y = Physics.Raycast(position, Vector3.down, out hitInfo, 300f, Rust.Layers.Solid)
                ? hitInfo.point.y
                : TerrainMeta.HeightMap.GetHeight(position);
            return position;
        }

        #endregion Helpers

        #region AI

        private void ReplaceBrain(CH47HelicopterAIController chinook)
        {
            var brain = chinook.GetComponent<CH47AIBrain>();
            brain.PathFinder = new CustomPathFinder(); 
        }

        private class CustomBrain : BaseAIBrain<CH47HelicopterAIController>
        {
        }

        private class CustomPathFinder : BasePathFinder
        {
            public List<Vector3> visitedPatrolPoints = new List<Vector3>();
            public override Vector3 GetRandomPatrolPoint()
            {
                Vector3 zero;
                MonumentInfo monumentInfo = null;
                if (TerrainMeta.Path != null && TerrainMeta.Path.Monuments != null && TerrainMeta.Path.Monuments.Count > 0)
                {
                    int count = TerrainMeta.Path.Monuments.Count;
                    int num = Random.Range(0, count);
                    for (int i = 0; i < count; i++)
                    {
                        int num2 = i + num;
                        if (num2 >= count)
                        {
                            num2 -= count;
                        }
                        MonumentInfo monumentInfo2 = TerrainMeta.Path.Monuments[num2];
                        if (monumentInfo2.Type == MonumentType.Cave || monumentInfo2.Type == MonumentType.WaterWell || monumentInfo2.Tier == MonumentTier.Tier0 || (monumentInfo2.Tier & MonumentTier.Tier0) > 0)
                        {
                            continue;
                        }
                        bool flag = false;
                        foreach (Vector3 visitedPatrolPoint in visitedPatrolPoints)
                        {
                            if (Vector3Ex.Distance2D(monumentInfo2.transform.position, visitedPatrolPoint) < 100f)
                            {
                                flag = true;
                                break;
                            }
                        }
                        if (!flag)
                        {
                            monumentInfo = monumentInfo2;
                            break;
                        }
                    }
                    if (monumentInfo == null)
                    {
                        visitedPatrolPoints.Clear();
                        monumentInfo = GetRandomValidMonumentInfo();
                    }
                }
                if (monumentInfo != null)
                {
                    visitedPatrolPoints.Add(monumentInfo.transform.position);
                    zero = monumentInfo.transform.position;
                }
                else
                {
                    float x = TerrainMeta.Size.x;
                    float y = 30f;
                    zero = Vector3Ex.Range(-1f, 1f);
                    zero.y = 0f;
                    zero.Normalize();
                    zero *= x * Random.Range(0f, 0.75f);
                    zero.y = y;
                }
                float num3 = Mathf.Max(TerrainMeta.WaterMap.GetHeight(zero), TerrainMeta.HeightMap.GetHeight(zero));
                float num4 = num3;
                RaycastHit hitInfo;
                if (Physics.SphereCast(zero + new Vector3(0f, 200f, 0f), 20f, Vector3.down, out  hitInfo, 300f, 1218511105))
                {
                    num4 = Mathf.Max(hitInfo.point.y, num3);
                }
                zero.y = num4 + 30f;
                return zero;
            }

            private MonumentInfo GetRandomValidMonumentInfo()
            {
                int count = TerrainMeta.Path.Monuments.Count;
                int num = Random.Range(0, count);
                for (int i = 0; i < count; i++)
                {
                    int num2 = i + num;
                    if (num2 >= count)
                    {
                        num2 -= count;
                    }
                    MonumentInfo monumentInfo = TerrainMeta.Path.Monuments[num2];
                    if (monumentInfo.Type != 0 && monumentInfo.Type != MonumentType.WaterWell && monumentInfo.Tier != MonumentTier.Tier0)
                    {
                        return monumentInfo;
                    }
                }
                return null;
            }
        }
         

        #endregion AI

        #region ConfigurationFile

        private ConfigData configData;

        public class ConfigData
        {
            [JsonProperty(PropertyName = "Prevent the game from handling chinook drops")]
            public bool blockDefaultDrop = false;

            [JsonProperty(PropertyName = "Time before chinook starts trying to drop (seconds)")]
            public float dropDelay = 200f;

            [JsonProperty(PropertyName = "Minimum time until drop (seconds)")]
            public float minTime = 50f;

            [JsonProperty(PropertyName = "Maximum time until drop (seconds)")]
            public float maxTime = 100f;

            [JsonProperty(PropertyName = "Minimum number of online players to drop")]
            public int minPlayers = 0;

            [JsonProperty(PropertyName = "Don't drop above water")]
            public bool checkWater = true;

            [JsonProperty(PropertyName = "Don't drop above monuments")]
            public bool checkMonument = false;

            [JsonProperty(PropertyName = "What monuments to check (only works if monument checking is enabled)")]
            public Dictionary<string, MonumentSettings> monumentsS = new Dictionary<string, MonumentSettings>();

            public class MonumentSettings
            {
                [JsonProperty(PropertyName = "Enabled")]
                public bool enabled;

                [JsonProperty(PropertyName = "Monument size")]
                public float monumentSize;
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

        protected override void SaveConfig() => Config.WriteObject(configData);

        #endregion ConfigurationFile
    }
}