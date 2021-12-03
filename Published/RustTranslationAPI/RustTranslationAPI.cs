using Newtonsoft.Json;
using Oxide.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Oxide.Plugins
{
    [Info("Rust Translation API", "Arainrr", "1.0.3")]
    [Description("Provides translation APIs for Rust items, holdables, deployables, etc")]
    public class RustTranslationAPI : RustPlugin
    {
        //NOTE: Everything ignores case

        #region Fields

        private bool _serverInitialized;
        private bool _translationsInitialized;
        private readonly Dictionary<string, TranslationFiles> _translationFilesMap = new Dictionary<string, TranslationFiles>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _translationsOverride = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private const string English = "en";
        private const string Wildcard = "*";

        private static readonly List<string> SupportedLanguages = new List<string>
        {
            "af", "ar", "ca", "cs", "da", "de", "el", "en-PT", "es-ES", "fi", "fr", "he", "hu", "it", "ja", "ko", "nl", "no", "pl", "pt-BR", "pt-PT", "ro", "ru", "sr", "sv-SE", "tr", "uk", "vi", "zh-CN", "zh-TW", English
        };

        #endregion Fields

        #region Oxide Hooks

        private void OnServerInitialized()
        {
            _serverInitialized = true;
            InitializeFiles();
        }

        private void OnTranslationsDownloaded()
        {
            if (!_serverInitialized) return;
            InitializeFiles();
        }

        #endregion Oxide Hooks

        #region Methods

        private void InitializeFiles()
        {
            foreach (var language in SupportedLanguages)
            {
                bool isEnglish = language == English;
                Dictionary<string, string> translations = null;
                try
                {
                    if (ExistsDatafile(language, "engine"))
                    {
                        translations = LoadData<Dictionary<string, string>>(language, "engine");
                    }
                    else if (isEnglish)
                    {
                        translations = new Dictionary<string, string>();
                    }
                }
                catch
                {
                    translations = null;
                }
                if (translations == null)
                {
                    PrintError($"The '{language}' language file does not exist.");
                    continue;
                }
                var translationFiles = new TranslationFiles();
                translationFiles.language = language;
                foreach (var entry in translations)
                {
                    translationFiles.translations.Add(entry.Key, entry.Value);
                }

                UpdateTranslations(translationFiles);
                UpdateItemTranslations(translationFiles, isEnglish);
                UpdateDeployableAndHoldableTranslations(translationFiles);
                UpdateMonumentTranslations(translationFiles, isEnglish);
                UpdateConstructionTranslations(translationFiles, isEnglish);

                _translationFilesMap.Remove(language);
                _translationFilesMap.Add(language, translationFiles);
            }
            _translationsInitialized = true;

            //Make sure that '[PluginReference] Plugin RustTranslationAPI' is not null when the OnTranslationsInitialized hook is called.
            NextTick(() => Interface.CallHook("OnTranslationsInitialized"));
        }

        private class TranslationFiles
        {
            public string language;
            public readonly Dictionary<string, string> translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<int, string> itemIDTranslations = new Dictionary<int, string>();
            public readonly Dictionary<string, string> itemShortNameTranslations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> itemDisplayNameTranslations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> deployableShortPrefabNameTranslations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> holdableShortPrefabNameTranslations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> monumentNameTranslations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> constructionTranslations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private TranslationFiles GetTranslationFiles(string language)
        {
            if (string.IsNullOrEmpty(language))
            {
                language = lang.GetServerLanguage();
            }
            else if (language.Length == 17 && language.IsSteamId())
            {
                language = lang.GetLanguage(language);
            }
            if (string.IsNullOrEmpty(language))
            {
                return null;
            }
            TranslationFiles translationFiles;
            if (!_translationFilesMap.TryGetValue(language, out translationFiles))
            {
                return null;
            }
            return translationFiles;
        }

        private static bool IsValidShortPrefabName(ref string shortPrefabName)
        {
            if (string.IsNullOrEmpty(shortPrefabName))
            {
                return false;
            }
            if (shortPrefabName.Contains('/') || shortPrefabName.Contains('\\'))
            {
                shortPrefabName = Utility.GetFileNameWithoutExtension(shortPrefabName);
                if (string.IsNullOrEmpty(shortPrefabName))
                {
                    return false;
                }
            }
            return true;
        }

        private static string GetItemShortNameTranslation(TranslationFiles translationFiles, string itemShortName)
        {
            string translation;
            if (!translationFiles.itemShortNameTranslations.TryGetValue(itemShortName, out translation))
            {
                return null;
            }
            return translation;
        }

        #region Update Translations

        private void UpdateTranslations(TranslationFiles translationFiles)
        {
            Dictionary<string, string> translationsOverride;
            if (_translationsOverride.TryGetValue(translationFiles.language, out translationsOverride) ||
                _translationsOverride.TryGetValue(Wildcard, out translationsOverride))
            {
                foreach (var entry in translationsOverride)
                {
                    if (!translationFiles.translations.ContainsKey(entry.Key))
                    {
                        translationFiles.translations.Add(entry.Key, entry.Value);
                    }
                    else
                    {
                        translationFiles.translations[entry.Key] = entry.Value;
                    }
                }
            }
        }

        private void UpdateItemTranslations(TranslationFiles translationFiles, bool isEnglish)
        {
            foreach (var itemDefinition in ItemManager.GetItemDefinitions())
            {
                if (!itemDefinition.displayName.IsValid())
                {
                    continue;
                }
                if (isEnglish)
                {
                    translationFiles.itemIDTranslations.Add(itemDefinition.itemid, itemDefinition.displayName.english);
                    translationFiles.itemShortNameTranslations.Add(itemDefinition.shortname, itemDefinition.displayName.english);
                    if (!translationFiles.itemDisplayNameTranslations.ContainsKey(itemDefinition.displayName.english))
                    {
                        translationFiles.itemDisplayNameTranslations.Add(itemDefinition.displayName.english, itemDefinition.displayName.english);
                    }
                }
                else
                {
                    string translation;
                    string token;
                    if (!configData.customItemToken.TryGetValue(itemDefinition.shortname, out token))
                    {
                        token = itemDefinition.displayName.token;
                    }
                    if (translationFiles.translations.TryGetValue(token, out translation))
                    {
                        translationFiles.itemIDTranslations.Add(itemDefinition.itemid, translation);
                        translationFiles.itemShortNameTranslations.Add(itemDefinition.shortname, translation);
                        if (!translationFiles.itemDisplayNameTranslations.ContainsKey(itemDefinition.displayName.english))
                        {
                            translationFiles.itemDisplayNameTranslations.Add(itemDefinition.displayName.english, translation);
                        }
                    }
                    else
                    {
                        PrintError($"No translation was found for the item: {itemDefinition.displayName.english}(shortname: {itemDefinition.shortname} | token: {token})");
                    }
                }
            }

            SaveData(translationFiles.language, "ItemIDTranslations", translationFiles.itemIDTranslations);
            SaveData(translationFiles.language, "ItemDisplayNameTranslations", translationFiles.itemDisplayNameTranslations);
            SaveData(translationFiles.language, "ItemShortNameTranslations", translationFiles.itemShortNameTranslations);
        }

        private void UpdateDeployableAndHoldableTranslations(TranslationFiles translationFiles)
        {
            foreach (var itemDefinition in ItemManager.GetItemDefinitions())
            {
                var translation = GetItemShortNameTranslation(translationFiles, itemDefinition.shortname);
                if (string.IsNullOrEmpty(translation)) continue;
                var itemModDeployable = itemDefinition.GetComponent<ItemModDeployable>();
                if (itemModDeployable != null)
                {
                    var prefabName = itemModDeployable.entityPrefab?.resourcePath;
                    if (!string.IsNullOrEmpty(prefabName))
                    {
                        var shortPrefabName = Utility.GetFileNameWithoutExtension(prefabName);
                        if (!string.IsNullOrEmpty(shortPrefabName))
                        {
                            if (!translationFiles.deployableShortPrefabNameTranslations.ContainsKey(shortPrefabName))
                            {
                                translationFiles.deployableShortPrefabNameTranslations.Add(shortPrefabName, translation);
                            }
                        }
                    }
                }

                var itemModEntity = itemDefinition.GetComponent<ItemModEntity>();
                if (itemModEntity != null)
                {
                    var heldEntity = itemModEntity.entityPrefab?.Get()?.GetComponent<HeldEntity>();
                    if (heldEntity != null && !(heldEntity is Planner) && !(heldEntity is Deployer))
                    {
                        if (!string.IsNullOrEmpty(heldEntity.PrefabName))
                        {
                            var shortPrefabName = Utility.GetFileNameWithoutExtension(heldEntity.PrefabName);
                            if (!string.IsNullOrEmpty(shortPrefabName))
                            {
                                if (!translationFiles.holdableShortPrefabNameTranslations.ContainsKey(shortPrefabName))
                                {
                                    translationFiles.holdableShortPrefabNameTranslations.Add(shortPrefabName, translation);
                                }
                            }
                        }
                        var thrownWeapon = heldEntity as ThrownWeapon;
                        if (thrownWeapon != null)
                        {
                            var prefabName = thrownWeapon.prefabToThrow?.resourcePath;
                            if (!string.IsNullOrEmpty(prefabName))
                            {
                                var shortPrefabName = Utility.GetFileNameWithoutExtension(prefabName);
                                if (!string.IsNullOrEmpty(shortPrefabName))
                                {
                                    if (!translationFiles.holdableShortPrefabNameTranslations.ContainsKey(shortPrefabName))
                                    {
                                        translationFiles.holdableShortPrefabNameTranslations.Add(shortPrefabName, translation);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            SaveData(translationFiles.language, "HoldableShortPrefabNameTranslations", translationFiles.holdableShortPrefabNameTranslations);
            SaveData(translationFiles.language, "DeployableShortPrefabNameTranslations", translationFiles.deployableShortPrefabNameTranslations);
        }

        private void UpdateMonumentTranslations(TranslationFiles translationFiles, bool isEnglish)
        {
            foreach (var monumentInfo in TerrainMeta.Path.Monuments)
            {
                if (!monumentInfo.displayPhrase.IsValid())
                {
                    continue;
                }
                var shortPrefabName = Utility.GetFileNameWithoutExtension(monumentInfo.name);
                if (isEnglish)
                {
                    if (!translationFiles.monumentNameTranslations.ContainsKey(shortPrefabName))
                    {
                        translationFiles.monumentNameTranslations.Add(shortPrefabName, monumentInfo.displayPhrase.english);
                    }
                }
                else
                {
                    string translation;
                    var token = monumentInfo.displayPhrase.token;
                    if (translationFiles.translations.TryGetValue(token, out translation))
                    {
                        if (!translationFiles.monumentNameTranslations.ContainsKey(shortPrefabName))
                        {
                            translationFiles.monumentNameTranslations.Add(shortPrefabName, translation);
                        }
                    }
                    else
                    {
                        PrintError($"No translation was found for the monument: {monumentInfo.displayPhrase.english}(name: {monumentInfo.name} : token: {token})");
                    }
                }
            }
            SaveData(translationFiles.language, "MonumentTranslations", translationFiles.monumentNameTranslations);
        }

        private void UpdateConstructionTranslations(TranslationFiles translationFiles, bool isEnglish)
        {
            foreach (var entry in PrefabAttribute.server.prefabs)
            {
                var construction = entry.Value.Find<Construction>().FirstOrDefault();
                if (construction != null && construction.deployable == null && construction.info.name.IsValid())
                {
                    var shortPrefabName = Utility.GetFileNameWithoutExtension(construction.fullName);
                    if (isEnglish)
                    {
                        if (!translationFiles.constructionTranslations.ContainsKey(shortPrefabName))
                        {
                            translationFiles.constructionTranslations.Add(shortPrefabName, construction.info.name.english);
                        }
                    }
                    else
                    {
                        string translation;
                        var token = construction.info.name.token;
                        if (translationFiles.translations.TryGetValue(token, out translation))
                        {
                            if (!translationFiles.constructionTranslations.ContainsKey(shortPrefabName))
                            {
                                translationFiles.constructionTranslations.Add(shortPrefabName, translation);
                            }
                        }
                        else
                        {
                            PrintError($"No translation was found for the construction: {construction.info.name.english}(name: {construction.fullName} : token: {token})");
                        }
                    }
                }
            }
            SaveData(translationFiles.language, "ConstructionTranslations", translationFiles.constructionTranslations);
        }

        #endregion Update Translations

        #endregion Methods

        #region API

        private bool IsInitialized() => _translationsInitialized;

        private bool IsSupportedLanguage(string language) => SupportedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase);

        #region Translation

        #region Translation Files

        private string GetSerializedTranslationsFiles()
        {
            return JsonConvert.SerializeObject(_translationFilesMap);
        }

        private string GetSerializedTranslationsFiles(string language)
        {
            var translationFiles = GetTranslationFiles(language);
            if (translationFiles == null) return null;
            return JsonConvert.SerializeObject(translationFiles);
            //JObject jObject = new JObject();
            //jObject["Translations"] = JsonConvert.SerializeObject(translationFiles.translations);
            //jObject["ItemIDTranslations"] = JsonConvert.SerializeObject(translationFiles.itemIDTranslations);
            //jObject["ItemShortNameTranslations"] = JsonConvert.SerializeObject(translationFiles.itemShortNameTranslations);
            //jObject["ItemDisplayNameTranslations"] = JsonConvert.SerializeObject(translationFiles.itemDisplayNameTranslations);
            //jObject["DeployableShortPrefabNameTranslations"] = JsonConvert.SerializeObject(translationFiles.deployableShortPrefabNameTranslations);
            //jObject["HoldableShortPrefabNameTranslations"] = JsonConvert.SerializeObject(translationFiles.holdableShortPrefabNameTranslations);
            //jObject["MonumentNameTranslations"] = JsonConvert.SerializeObject(translationFiles.monumentNameTranslations);
            //jObject["ConstructionTranslations"] = JsonConvert.SerializeObject(translationFiles.constructionTranslations);
            //return jObject;
        }

        private Dictionary<string, string> GetTranslations(string language)
        {
            return GetTranslationFiles(language)?.translations;
        }

        private Dictionary<int, string> GetItemIDTranslations(string language)
        {
            return GetTranslationFiles(language)?.itemIDTranslations;
        }

        private Dictionary<string, string> GetItemShortNameTranslations(string language)
        {
            return GetTranslationFiles(language)?.itemShortNameTranslations;
        }

        private Dictionary<string, string> GetItemDisplayNameTranslations(string language)
        {
            return GetTranslationFiles(language)?.itemDisplayNameTranslations;
        }

        private Dictionary<string, string> GetDeployableShortPrefabNameTranslations(string language)
        {
            return GetTranslationFiles(language)?.deployableShortPrefabNameTranslations;
        }

        private Dictionary<string, string> GetHoldableShortPrefabNameTranslations(string language)
        {
            return GetTranslationFiles(language)?.holdableShortPrefabNameTranslations;
        }

        private Dictionary<string, string> GetMonumentTranslations(string language)
        {
            return GetTranslationFiles(language)?.monumentNameTranslations;
        }

        private Dictionary<string, string> GetConstructionTranslations(string language)
        {
            return GetTranslationFiles(language)?.constructionTranslations;
        }

        #endregion Translation Files

        #region Token Translation

        private string GetTranslation(string language, Translate.Phrase phrase)
        {
            if (!phrase.IsValid()) return null;
            return GetTranslation(language, phrase.token);
        }

        private string GetTranslation(string language, string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }
            var translationFiles = GetTranslationFiles(language);
            if (translationFiles == null)
            {
                return null;
            }
            string translation;
            if (!translationFiles.translations.TryGetValue(token, out translation))
            {
                return null;
            }
            return translation;
        }

        #endregion Token Translation

        #region Item Translation

        private string GetItemTranslationByID(string language, int itemID)
        {
            var translationFiles = GetTranslationFiles(language);
            if (translationFiles == null)
            {
                return null;
            }
            string translation;
            if (!translationFiles.itemIDTranslations.TryGetValue(itemID, out translation))
            {
                return null;
            }
            return translation;
        }

        private string GetItemTranslationByDisplayName(string language, string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return null;
            var translationFiles = GetTranslationFiles(language);
            if (translationFiles == null)
            {
                return null;
            }
            string translation;
            if (!translationFiles.itemDisplayNameTranslations.TryGetValue(displayName, out translation))
            {
                return null;
            }
            return translation;
        }

        private string GetItemTranslationByDefinition(string language, ItemDefinition itemDefinition)
        {
            if (itemDefinition == null) return null;
            return GetItemTranslationByShortName(language, itemDefinition.shortname);
        }

        private string GetItemTranslationByShortName(string language, string itemShortName)
        {
            if (string.IsNullOrEmpty(itemShortName)) return null;
            var translationFiles = GetTranslationFiles(language);
            if (translationFiles == null)
            {
                return null;
            }

            return GetItemShortNameTranslation(translationFiles, itemShortName);
        }

        #endregion Item Translation

        #region Deployable & Holdable Translation

        private string GetDeployableTranslation(string language, string deployable)
        {
            if (!IsValidShortPrefabName(ref deployable))
            {
                return null;
            }
            var translationFiles = GetTranslationFiles(language);
            if (translationFiles == null)
            {
                return null;
            }
            string translation;
            if (!translationFiles.deployableShortPrefabNameTranslations.TryGetValue(deployable, out translation))
            {
                return null;
            }
            return translation;
        }

        private string GetHoldableTranslation(string language, string holdable)
        {
            if (!IsValidShortPrefabName(ref holdable))
            {
                return null;
            }
            var translationFiles = GetTranslationFiles(language);
            if (translationFiles == null)
            {
                return null;
            }
            string translation;
            if (!translationFiles.holdableShortPrefabNameTranslations.TryGetValue(holdable, out translation))
            {
                return null;
            }
            return translation;
        }

        #endregion Deployable & Holdable Translation

        #region Monument Translation

        private string GetMonumentTranslation(string language, MonumentInfo monumentInfo)
        {
            if (monumentInfo == null) return null;
            return GetMonumentTranslation(language, monumentInfo.name);
        }

        private string GetMonumentTranslation(string language, string monumentName)
        {
            if (!IsValidShortPrefabName(ref monumentName))
            {
                return null;
            }
            var translationFiles = GetTranslationFiles(language);
            if (translationFiles == null)
            {
                return null;
            }
            string translation;
            if (!translationFiles.monumentNameTranslations.TryGetValue(monumentName, out translation))
            {
                return null;
            }
            return translation;
        }

        #endregion Monument Translation

        #region Construction Translation

        private string GetConstructionTranslation(string language, Construction construction)
        {
            if (construction == null) return null;
            return GetConstructionTranslation(language, construction.fullName);
        }

        private string GetConstructionTranslation(string language, string shortPrefabName)
        {
            if (!IsValidShortPrefabName(ref shortPrefabName))
            {
                return null;
            }
            var translationFiles = GetTranslationFiles(language);
            if (translationFiles == null)
            {
                return null;
            }
            string translation;
            if (!translationFiles.constructionTranslations.TryGetValue(shortPrefabName, out translation))
            {
                return null;
            }
            return translation;
        }

        #endregion Construction Translation

        #endregion Translation

        #endregion API

        #region ConfigurationFile

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Custom item token", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> customItemToken = new Dictionary<string, string>
            {
                ["electric.generator.small"] = "Test generator"
            };

            [JsonProperty(PropertyName = "Translations override", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, Dictionary<string, string>> translationsOverride = new Dictionary<string, Dictionary<string, string>>
            {
                ["zh-CN"] = new Dictionary<string, string>
                {
                    ["fogmachine"] = "喷雾机",
                }
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

            foreach (var entry in configData.translationsOverride)
            {
                _translationsOverride.Add(entry.Key, entry.Value);
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

        #region DataFile

        private static string FormatPath(string language, string filename) => $"Translations\\{language}\\{filename}";

        private static T LoadData<T>(string language, string filename) => Interface.Oxide.DataFileSystem.ReadObject<T>(FormatPath(language, filename));

        private static bool ExistsDatafile(string language, string filename) => Interface.Oxide.DataFileSystem.ExistsDatafile(FormatPath(language, filename));

        private static void SaveData<T>(string language, string filename, T data) => Interface.Oxide.DataFileSystem.WriteObject(FormatPath(language, filename), data);

        #endregion DataFile
    }
}