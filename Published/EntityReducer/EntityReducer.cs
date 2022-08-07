using System;
using System.Collections.Generic;
using System.Text;
using ConVar;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Entity Reducer", "Arainrr", "2.1.4")]
    [Description("Controls all spawn populations on the server")]
    public class EntityReducer : RustPlugin
    {
        #region Oxide Hooks

        private void OnServerInitialized()
        {
            if (SpawnHandler.Instance == null || SpawnHandler.Instance.AllSpawnPopulations == null)
            {
                PrintError("The SpawnHandler is missing on your server, the plugin cannot be used");
                Interface.Oxide.UnloadPlugin(Name);
                return;
            }
            UpdateConfig();
            if (_configData.pluginEnabled)
            {
                ApplySpawnHandler();
            }
        }

        #endregion Oxide Hooks

        #region Methods

        private void UpdateConfig()
        {
            var newPopulationSettings = new Dictionary<string, PopulationSetting>();
            for (var i = 0; i < SpawnHandler.Instance.AllSpawnPopulations.Length; i++)
            {
                var spawnPopulation = SpawnHandler.Instance.AllSpawnPopulations[i];
                if (spawnPopulation == null)
                {
                    continue;
                }
                var spawnDistribution = SpawnHandler.Instance.SpawnDistributions[i];
                if (spawnDistribution == null)
                {
                    continue;
                }
                var targetCount = SpawnHandler.Instance.GetTargetCount(spawnPopulation, spawnDistribution);
                PopulationSetting populationSetting;
                if (_configData.populationSettings.TryGetValue(spawnPopulation.name, out populationSetting))
                {
                    if (!populationSetting.enabled)
                    {
                        populationSetting.targetCount = targetCount;
                    }
                    newPopulationSettings.Add(spawnPopulation.name, populationSetting);
                }
                else
                {
                    newPopulationSettings.Add(spawnPopulation.name, new PopulationSetting { targetCount = targetCount });
                }
            }
            _configData.populationSettings = newPopulationSettings;
            SaveConfig();
        }

        private void ApplySpawnHandler()
        {
            var allSpawnPopulations = SpawnHandler.Instance.AllSpawnPopulations;
            var spawnDistributions = SpawnHandler.Instance.SpawnDistributions;
            for (var i = 0; i < allSpawnPopulations.Length; i++)
            {
                var spawnPopulation = allSpawnPopulations[i];
                if (spawnPopulation == null)
                {
                    continue;
                }
                var spawnDistribution = spawnDistributions[i];
                if (spawnDistribution == null)
                {
                    continue;
                }

                PopulationSetting populationSetting;
                if (_configData.populationSettings.TryGetValue(spawnPopulation.name, out populationSetting) && populationSetting.enabled)
                {
                    var num = TerrainMeta.Size.x * TerrainMeta.Size.z;
                    var num2 = /*2f **/ Spawn.max_density * 1E-06f;
                    if (!spawnPopulation.ScaleWithLargeMaps)
                    {
                        num = Mathf.Min(num, 1.6E+07f);
                    }
                    if (spawnPopulation.ScaleWithSpawnFilter)
                    {
                        num2 *= spawnDistribution.Density;
                    }
                    var densityToMaxPopulation = (float)Mathf.RoundToInt(num * num2);

                    spawnPopulation.ScaleWithServerPopulation = false;
                    var targetDensity = populationSetting.targetCount / densityToMaxPopulation;
                    var convarControlled = spawnPopulation as ConvarControlledSpawnPopulation;
                    if (convarControlled != null)
                    {
                        var command = ConsoleSystem.Index.Server.Find(convarControlled.PopulationConvar);
                        command?.Set(targetDensity);
                    }
                    else
                    {
                        spawnPopulation._targetDensity = targetDensity;
                    }
                }
            }
            SpawnHandler.Instance.EnforceLimits(true);
        }

        public string GetReport()
        {
            var allSpawnPopulations = SpawnHandler.Instance.AllSpawnPopulations;
            var spawnDistributions = SpawnHandler.Instance.SpawnDistributions;
            var stringBuilder = new StringBuilder();
            if (allSpawnPopulations == null)
            {
                stringBuilder.AppendLine("Spawn population array is null.");
            }
            if (spawnDistributions == null)
            {
                stringBuilder.AppendLine("Spawn distribution array is null.");
            }
            if (allSpawnPopulations != null && spawnDistributions != null)
            {
                stringBuilder.AppendLine();
                stringBuilder.AppendLine("SpawnPopulationName".PadRight(40) + "CurrentPopulation".PadRight(25) + "MaximumPopulation");
                for (var i = 0; i < allSpawnPopulations.Length; i++)
                {
                    var spawnPopulation = allSpawnPopulations[i];
                    if (spawnPopulation == null)
                    {
                        continue;
                    }
                    var spawnDistribution = spawnDistributions[i];
                    if (spawnDistribution == null)
                    {
                        continue;
                    }
                    var currentCount = SpawnHandler.Instance.GetCurrentCount(spawnPopulation, spawnDistribution);
                    var targetCount = SpawnHandler.Instance.GetTargetCount(spawnPopulation, spawnDistribution);
                    stringBuilder.AppendLine(spawnPopulation.name.PadRight(40) + currentCount.ToString().PadRight(25) + targetCount);
                }
            }
            return stringBuilder.ToString();
        }

        #endregion Methods

        #region Commands

        [ConsoleCommand("er.fillpopulations")]
        private void CmdFillPopulations(ConsoleSystem.Arg arg)
        {
            SpawnHandler.Instance.FillPopulations();
            SendReply(arg, "Successfully filled all populations");
        }

        [ConsoleCommand("er.getreport")]
        private void CmdGetReport(ConsoleSystem.Arg arg)
        {
            SendReply(arg, GetReport());
        }

        [ConsoleCommand("er.enforcelimits")]
        private void CmdEnforceLimits(ConsoleSystem.Arg arg)
        {
            SpawnHandler.Instance.EnforceLimits(true);
            SendReply(arg, "Successfully enforced all population limits");
        }

        #endregion Commands

        #region ConfigurationFile

        private ConfigData _configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Enabled Plugin")]
            public readonly bool pluginEnabled = false;

            [JsonProperty(PropertyName = "Population Settings")]
            public Dictionary<string, PopulationSetting> populationSettings = new Dictionary<string, PopulationSetting>();
        }

        private class PopulationSetting
        {
            [JsonProperty(PropertyName = "Enabled")]
            public readonly bool enabled = true;

            [JsonProperty(PropertyName = "Target Count")]
            public int targetCount;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _configData = Config.ReadObject<ConfigData>();
                if (_configData == null)
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
            _configData = new ConfigData();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_configData);
        }

        #endregion ConfigurationFile
    }
}