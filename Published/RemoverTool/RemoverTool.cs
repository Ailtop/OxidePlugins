using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using VLB;
using Time = UnityEngine.Time;

namespace Oxide.Plugins
{
    [Info("Remover Tool", "Reneb/Fuji/Arainrr", "4.3.30", ResourceId = 651)]
    [Description("Building and entity removal tool")]
    public class RemoverTool : RustPlugin
    {
        #region Fields

        [PluginReference] private readonly Plugin Friends, ServerRewards, Clans, Economics, ImageLibrary, BuildingOwners, RustTranslationAPI;

        private const string ECONOMICS_KEY = "economics";
        private const string SERVER_REWARDS_KEY = "serverrewards";

        private const string PERMISSION_ALL = "removertool.all";
        private const string PERMISSION_ADMIN = "removertool.admin";
        private const string PERMISSION_NORMAL = "removertool.normal";
        private const string PERMISSION_TARGET = "removertool.target";
        private const string PERMISSION_EXTERNAL = "removertool.external";
        private const string PERMISSION_OVERRIDE = "removertool.override";
        private const string PERMISSION_STRUCTURE = "removertool.structure";

        private const string PREFAB_ITEM_DROP = "assets/prefabs/misc/item drop/item_drop.prefab";

        private const int LAYER_ALL = 1 << 0 | 1 << 8 | 1 << 21;
        private const int LAYER_TARGET = ~(1 << 2 | 1 << 3 | 1 << 4 | 1 << 10 | 1 << 18 | 1 << 28 | 1 << 29);

        private static object False;
        private static RemoverTool _rt;
        private static BUTTON _removeButton;
        private static RemoveMode _removeMode;

        private bool _removeOverride;
        private Coroutine _removeAllCoroutine;
        private Coroutine _removeStructureCoroutine;
        private Coroutine _removeExternalCoroutine;
        private Coroutine _removePlayerEntityCoroutine;

        private Hash<uint, float> _entitySpawnedTimes;
        private Hash<ulong, float> _lastBlockedPlayers;
        private Hash<uint, float> _lastAttackedBuildings;
        private readonly Hash<ulong, float> _cooldownTimes = new Hash<ulong, float>();

        private enum RemoveMode
        {
            None,
            NoHeld,
            MeleeHit,
            SpecificTool
        }

        private enum RemoveType
        {
            None,
            All,
            Admin,
            Normal,
            External,
            Structure
        }

        private enum PlayerEntityRemoveType
        {
            All,
            Cupboard,
            Building,
        }

        #endregion Fields

        #region Oxide Hooks

        private void Init()
        {
            _rt = this;
            False = false;
            permission.RegisterPermission(PERMISSION_ALL, this);
            permission.RegisterPermission(PERMISSION_ADMIN, this);
            permission.RegisterPermission(PERMISSION_NORMAL, this);
            permission.RegisterPermission(PERMISSION_TARGET, this);
            permission.RegisterPermission(PERMISSION_OVERRIDE, this);
            permission.RegisterPermission(PERMISSION_EXTERNAL, this);
            permission.RegisterPermission(PERMISSION_STRUCTURE, this);

            Unsubscribe(nameof(OnEntityDeath));
            Unsubscribe(nameof(OnHammerHit));
            Unsubscribe(nameof(OnEntitySpawned));
            Unsubscribe(nameof(OnEntityKill));
            Unsubscribe(nameof(OnPlayerAttack));
            Unsubscribe(nameof(OnActiveItemChanged));
            Unsubscribe(nameof(OnServerSave));

            foreach (var perm in configData.permS.Keys)
            {
                if (!permission.PermissionExists(perm, this))
                {
                    permission.RegisterPermission(perm, this);
                }
            }
            cmd.AddChatCommand(configData.chatS.command, this, nameof(CmdRemove));
        }

        private void OnServerInitialized()
        {
            Initialize();
            UpdateConfig();
            _removeMode = RemoveMode.None;
            if (configData.removerModeS.noHeldMode) _removeMode = RemoveMode.NoHeld;
            if (configData.removerModeS.meleeHitMode) _removeMode = RemoveMode.MeleeHit;
            if (configData.removerModeS.specificToolMode) _removeMode = RemoveMode.SpecificTool;
            if (_removeMode == RemoveMode.MeleeHit)
            {
                BaseMelee beseMelee;
                ItemDefinition itemDefinition;
                if (string.IsNullOrEmpty(configData.removerModeS.meleeHitItemShortname) ||
                    (itemDefinition = ItemManager.FindItemDefinition(configData.removerModeS.meleeHitItemShortname)) == null ||
                    (beseMelee = itemDefinition.GetComponent<ItemModEntity>()?.entityPrefab.Get()?.GetComponent<BaseMelee>()) == null)
                {
                    PrintError($"{configData.removerModeS.meleeHitItemShortname} is not an item shortname for a melee tool");
                    _removeMode = RemoveMode.None;
                }
                else
                {
                    Subscribe(beseMelee is Hammer ? nameof(OnHammerHit) : nameof(OnPlayerAttack));
                }
            }

            if (configData.raidS.enabled)
            {
                _lastBlockedPlayers = new Hash<ulong, float>();
                _lastAttackedBuildings = new Hash<uint, float>();
                Subscribe(nameof(OnEntityDeath));
            }
            if (configData.globalS.entityTimeLimit)
            {
                _entitySpawnedTimes = new Hash<uint, float>();
                Subscribe(nameof(OnEntitySpawned));
                Subscribe(nameof(OnEntityKill));
                Subscribe(nameof(OnServerSave));
            }
            if (configData.globalS.logToFile)
            {
                debugStringBuilder = new StringBuilder();
                Subscribe(nameof(OnServerSave));
            }

            if (_removeMode == RemoveMode.MeleeHit && configData.removerModeS.meleeHitEnableInHand ||
                _removeMode == RemoveMode.SpecificTool && configData.removerModeS.specificToolEnableInHand)
            {
                Subscribe(nameof(OnActiveItemChanged));
            }

            if (!Enum.TryParse(configData.globalS.removeButton, true, out _removeButton) || !Enum.IsDefined(typeof(BUTTON), _removeButton))
            {
                PrintError($"{configData.globalS.removeButton} is an invalid button. The remove button has been changed to 'FIRE_PRIMARY'.");
                _removeButton = BUTTON.FIRE_PRIMARY;
                configData.globalS.removeButton = _removeButton.ToString();
                SaveConfig();
            }
            if (ImageLibrary != null)
            {
                foreach (var image in configData.imageUrls)
                {
                    AddImageToLibrary(image.Value, image.Key);
                }
                if (configData.uiS.showCrosshair)
                {
                    AddImageToLibrary(configData.uiS.crosshairImageUrl, UINAME_CROSSHAIR);
                }
            }
        }

        private void Unload()
        {
            if (_removeAllCoroutine != null) ServerMgr.Instance.StopCoroutine(_removeAllCoroutine);
            if (_removeStructureCoroutine != null) ServerMgr.Instance.StopCoroutine(_removeStructureCoroutine);
            if (_removeExternalCoroutine != null) ServerMgr.Instance.StopCoroutine(_removeExternalCoroutine);
            if (_removePlayerEntityCoroutine != null) ServerMgr.Instance.StopCoroutine(_removePlayerEntityCoroutine);
            foreach (var player in BasePlayer.activePlayerList)
            {
                player.GetComponent<ToolRemover>()?.DisableTool();
            }

            SaveDebug();
            configData = null;
            False = _rt = null;
        }

        private void OnServerSave()
        {
            if (configData.globalS.logToFile)
            {
                timer.Once(UnityEngine.Random.Range(0f, 60f), SaveDebug);
            }
            if (_entitySpawnedTimes != null)
            {
                var currentTime = Time.realtimeSinceStartup;
                foreach (var entry in _entitySpawnedTimes.ToArray())
                {
                    if (currentTime - entry.Value > configData.globalS.limitTime)
                    {
                        _entitySpawnedTimes.Remove(entry.Key);
                    }
                }
            }
        }

        private void OnEntityDeath(BuildingBlock buildingBlock, HitInfo info)
        {
            if (buildingBlock == null || info == null) return;
            var attacker = info.InitiatorPlayer;
            if (attacker != null && attacker.userID.IsSteamId() && HasAccess(attacker, buildingBlock)) return;
            BlockRemove(buildingBlock);
        }

        private void OnEntitySpawned(BaseEntity entity)
        {
            if (entity == null || entity.net == null) return;
            // if (!CanEntityBeSaved(entity)) return;
            _entitySpawnedTimes[entity.net.ID] = Time.realtimeSinceStartup;
        }

        private void OnEntityKill(BaseEntity entity)
        {
            if (entity == null || entity.net == null) return;
            _entitySpawnedTimes.Remove(entity.net.ID);
        }

        private object OnPlayerAttack(BasePlayer player, HitInfo info) => OnHammerHit(player, info);

        private object OnHammerHit(BasePlayer player, HitInfo info)
        {
            if (player == null || info.HitEntity == null) return null;
            var toolRemover = player.GetComponent<ToolRemover>();
            if (toolRemover == null) return null;
            if (!IsMeleeTool(player)) return null;
            toolRemover.hitEntity = info.HitEntity;
            return False;
        }

        private void OnActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (newItem == null) return;
            if (player == null || !player.userID.IsSteamId()) return;
            if (IsToolRemover(player)) return;
            if (_removeMode == RemoveMode.MeleeHit && IsMeleeTool(newItem))
            {
                ToggleRemove(player, RemoveType.Normal);
                return;
            }
            if (_removeMode == RemoveMode.SpecificTool && IsSpecificTool(newItem))
            {
                ToggleRemove(player, RemoveType.Normal);
                return;
            }
        }

        #endregion Oxide Hooks

        #region Initializing

        private readonly HashSet<Construction> _constructions = new HashSet<Construction>();
        private readonly Dictionary<string, int> _itemShortNameToItemId = new Dictionary<string, int>();
        private readonly Dictionary<string, string> _prefabNameToStructure = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _shortPrefabNameToDeployable = new Dictionary<string, string>();

        private void Initialize()
        {
            foreach (var itemDefinition in ItemManager.GetItemDefinitions())
            {
                if (!_itemShortNameToItemId.ContainsKey(itemDefinition.shortname))
                {
                    _itemShortNameToItemId.Add(itemDefinition.shortname, itemDefinition.itemid);
                }
                var deployablePrefab = itemDefinition.GetComponent<ItemModDeployable>()?.entityPrefab?.resourcePath;
                if (string.IsNullOrEmpty(deployablePrefab))
                {
                    continue;
                }
                var shortPrefabName = Utility.GetFileNameWithoutExtension(deployablePrefab);
                if (!string.IsNullOrEmpty(shortPrefabName) && !_shortPrefabNameToDeployable.ContainsKey(shortPrefabName))
                {
                    _shortPrefabNameToDeployable.Add(shortPrefabName, itemDefinition.shortname);
                }
            }
            foreach (var entry in PrefabAttribute.server.prefabs)
            {
                var construction = entry.Value.Find<Construction>().FirstOrDefault();
                if (construction != null && construction.deployable == null && !string.IsNullOrEmpty(construction.info.name.english))
                {
                    _constructions.Add(construction);
                    if (!_prefabNameToStructure.ContainsKey(construction.fullName))
                    {
                        _prefabNameToStructure.Add(construction.fullName, construction.info.name.english);
                    }
                }
            }
        }

        #endregion Initializing

        #region Methods

        private static string GetRemoveTypeName(RemoveType removeType) => configData.removeTypeS[removeType].displayName;

        private static void DropItemContainer(ItemContainer itemContainer, Vector3 position, Quaternion rotation) => itemContainer?.Drop(PREFAB_ITEM_DROP, position, rotation);

        private static bool IsExternalWall(StabilityEntity stabilityEntity) => stabilityEntity.ShortPrefabName.Contains("external");

        private static bool CanEntityBeDisplayed(BaseEntity entity, BasePlayer player)
        {
            var stash = entity as StashContainer;
            return stash == null || !stash.IsHidden() || stash.PlayerInRange(player);
        }

        private static bool CanEntityBeSaved(BaseEntity entity)
        {
            if (entity is BuildingBlock)
            {
                return true;
            }
            EntitySettings entitySettings;
            if (configData.removeS.entityS.TryGetValue(entity.ShortPrefabName, out entitySettings) && entitySettings.enabled)
            {
                return true;
            }
            return false;
        }

        private static bool HasEntityEnabled(BaseEntity entity)
        {
            var buildingBlock = entity as BuildingBlock;
            if (buildingBlock != null)
            {
                bool valid;
                if (configData.removeS.validConstruction.TryGetValue(buildingBlock.grade, out valid) && valid)
                {
                    return true;
                }
            }
            EntitySettings entitySettings;
            if (configData.removeS.entityS.TryGetValue(entity.ShortPrefabName, out entitySettings) && entitySettings.enabled)
            {
                return true;
            }
            return false;
        }

        private static bool IsRemovableEntity(BaseEntity entity)
        {
            if (_rt._shortPrefabNameToDeployable.ContainsKey(entity.ShortPrefabName)
                || _rt._prefabNameToStructure.ContainsKey(entity.PrefabName)
                || configData.removeS.entityS.ContainsKey(entity.ShortPrefabName))
            {
                var baseCombatEntity = entity as BaseCombatEntity;
                if (baseCombatEntity != null)
                {
                    if (baseCombatEntity.IsDead())
                    {
                        return false;
                    }
                    if (baseCombatEntity.pickup.itemTarget != null)
                    {
                        return true;
                    }
                }
                return true;
            }
            return false;
        }

        private static string GetEntityImage(string name)
        {
            if (configData.imageUrls.ContainsKey(name))
            {
                return GetImageFromLibrary(name);
            }
            if (_rt._itemShortNameToItemId.ContainsKey(name))
            {
                return GetImageFromLibrary(name);
            }
            return null;
        }

        private static string GetItemImage(string shortname)
        {
            switch (shortname.ToLower())
            {
                case ECONOMICS_KEY: return GetImageFromLibrary(ECONOMICS_KEY);
                case SERVER_REWARDS_KEY: return GetImageFromLibrary(SERVER_REWARDS_KEY);
            }
            return GetEntityImage(shortname);
        }

        private static void TryFindEntityName(BasePlayer player, BaseEntity entity, out string displayName, out string imageName)
        {
            var target = entity as BasePlayer;
            if (target != null)
            {
                imageName = target.userID.IsSteamId() ? target.UserIDString : target.ShortPrefabName;
                displayName = $"{target.displayName} ({GetOtherDisplayName(target.ShortPrefabName)})";
                return;
            }
            EntitySettings entitySettings;
            if (configData.removeS.entityS.TryGetValue(entity.ShortPrefabName, out entitySettings))
            {
                imageName = entity.ShortPrefabName;
                displayName = _rt.GetDeployableDisplayName(player, entity.ShortPrefabName, entitySettings.displayName);
                return;
            }

            string structureName;
            if (_rt._prefabNameToStructure.TryGetValue(entity.PrefabName, out structureName))
            {
                BuildingBlocksSettings buildingBlockSettings;
                if (configData.removeS.buildingBlockS.TryGetValue(structureName, out buildingBlockSettings))
                {
                    imageName = structureName;
                    displayName = _rt.GetConstructionDisplayName(player, entity.PrefabName, buildingBlockSettings.displayName);
                    return;
                }
            }

            imageName = entity.ShortPrefabName;
            displayName = GetOtherDisplayName(entity.ShortPrefabName);
        }

        private static string GetDisplayNameByPriceName(string language, string priceName)
        {
            var itemDefinition = ItemManager.FindItemDefinition(priceName);
            if (itemDefinition != null)
            {
                var displayName = _rt.GetItemDisplayName(language, itemDefinition.shortname);
                if (!string.IsNullOrEmpty(displayName))
                {
                    return displayName;
                }

                return GetOtherDisplayName(itemDefinition.displayName.english);
            }
            return GetOtherDisplayName(priceName);
        }

        private static string GetOtherDisplayName(string name)
        {
            string displayName;
            if (configData.displayNames.TryGetValue(name, out displayName))
            {
                return displayName;
            }
            configData.displayNames.Add(name, name);
            _rt.SaveConfig();
            return name;
        }

        private static PermissionSettings GetPermissionS(BasePlayer player)
        {
            int priority = 0;
            PermissionSettings permissionSettings = null;
            foreach (var entry in configData.permS)
            {
                if (entry.Value.priority >= priority && _rt.permission.UserHasPermission(player.UserIDString, entry.Key))
                {
                    priority = entry.Value.priority;
                    permissionSettings = entry.Value;
                }
            }
            return permissionSettings ?? new PermissionSettings();
        }

        private static Vector2 GetAnchor(string anchor)
        {
            var array = anchor.Split(' ');
            return new Vector2(float.Parse(array[0]), float.Parse(array[1]));
        }

        private static bool AddImageToLibrary(string url, string shortname, ulong skin = 0) => (bool)_rt.ImageLibrary.Call("AddImage", url, shortname.ToLower(), skin);

        private static string GetImageFromLibrary(string shortname, ulong skin = 0, bool returnUrl = false) => string.IsNullOrEmpty(shortname) ? null : (string)_rt.ImageLibrary.Call("GetImage", shortname.ToLower(), skin, returnUrl);

        #endregion Methods

        #region RaidBlocker

        private void BlockRemove(BuildingBlock buildingBlock)
        {
            if (configData.raidS.blockBuildingID)
            {
                var buildingID = buildingBlock.buildingID;
                _lastAttackedBuildings[buildingID] = Time.realtimeSinceStartup;
            }
            if (configData.raidS.blockPlayers)
            {
                var players = Pool.GetList<BasePlayer>();
                Vis.Entities(buildingBlock.transform.position, configData.raidS.blockRadius, players, Rust.Layers.Mask.Player_Server);
                foreach (var player in players)
                {
                    if (player.userID.IsSteamId())
                        _lastBlockedPlayers[player.userID] = Time.realtimeSinceStartup;
                }
                Pool.FreeList(ref players);
            }
        }

        private bool IsRaidBlocked(BasePlayer player, BaseEntity targetEntity, out float timeLeft)
        {
            if (configData.raidS.blockBuildingID)
            {
                var buildingBlock = targetEntity as BuildingBlock;
                if (buildingBlock != null)
                {
                    float blockTime;
                    if (_lastAttackedBuildings.TryGetValue(buildingBlock.buildingID, out blockTime))
                    {
                        timeLeft = configData.raidS.blockTime - (Time.realtimeSinceStartup - blockTime);
                        if (timeLeft > 0) return true;
                    }
                }
            }
            if (configData.raidS.blockPlayers)
            {
                float blockTime;
                if (_lastBlockedPlayers.TryGetValue(player.userID, out blockTime))
                {
                    timeLeft = configData.raidS.blockTime - (Time.realtimeSinceStartup - blockTime);
                    if (timeLeft > 0) return true;
                }
            }
            timeLeft = 0;
            return false;
        }

        #endregion RaidBlocker

        #region UI

        private static class UI
        {
            public static CuiElementContainer CreateElementContainer(string parent, string panelName, string backgroundColor, string anchorMin, string anchorMax, string offsetMin = "", string offsetMax = "", bool cursor = false)
            {
                return new CuiElementContainer()
                {
                    {
                        new CuiPanel
                        {
                            Image = { Color = backgroundColor },
                            RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax ,OffsetMin = offsetMin,OffsetMax = offsetMax},
                            CursorEnabled = cursor
                        }, parent, panelName
                    }
                };
            }

            public static void CreatePanel(ref CuiElementContainer container, string panelName, string backgroundColor, string anchorMin, string anchorMax, bool cursor = false)
            {
                container.Add(new CuiPanel
                {
                    Image = { Color = backgroundColor },
                    RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax },
                    CursorEnabled = cursor
                }, panelName);
            }

            public static void CreateLabel(ref CuiElementContainer container, string panelName, string textColor, string text, int fontSize, string anchorMin, string anchorMax, TextAnchor align = TextAnchor.MiddleCenter, float fadeIn = 0f)
            {
                container.Add(new CuiLabel
                {
                    Text = { Color = textColor, FontSize = fontSize, Align = align, Text = text, FadeIn = fadeIn },
                    RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax }
                }, panelName);
            }

            public static void CreateImage(ref CuiElementContainer container, string panelName, string image, string anchorMin, string anchorMax, string color = "1 1 1 1")
            {
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panelName,
                    Components =
                    {
                        new CuiRawImageComponent { Sprite = "assets/content/textures/generic/fulltransparent.tga", Color = color, Png = image},
                        new CuiRectTransformComponent { AnchorMin = anchorMin, AnchorMax = anchorMax }
                    }
                });
            }
        }

        [Flags]
        private enum UiEntry
        {
            None = 0,
            Entity = 1,
            Price = 1 << 1,
            Refund = 1 << 2,
            Auth = 1 << 3,
        }

        private const string UINAME_MAIN = "RemoverToolUI_Main";
        private const string UINAME_TIMELEFT = "RemoverToolUI_TimeLeft";
        private const string UINAME_ENTITY = "RemoverToolUI_Entity";
        private const string UINAME_PRICE = "RemoverToolUI_Price";
        private const string UINAME_REFUND = "RemoverToolUI_Refund";
        private const string UINAME_AUTH = "RemoverToolUI_Auth";
        private const string UINAME_CROSSHAIR = "RemoverToolUI_Crosshair";

        private static void CreateCrosshairUI(BasePlayer player)
        {
            if (_rt.ImageLibrary == null) return;
            var image = GetImageFromLibrary(UINAME_CROSSHAIR);
            if (string.IsNullOrEmpty(image)) return;
            var container = UI.CreateElementContainer("Hud", UINAME_CROSSHAIR, "0 0 0 0", configData.uiS.crosshairAnchorMin, configData.uiS.crosshairAnchorMax, configData.uiS.crosshairOffsetMin, configData.uiS.crosshairOffsetMax);
            UI.CreateImage(ref container, UINAME_CROSSHAIR, image, "0 0", "1 1", configData.uiS.crosshairColor);
            CuiHelper.DestroyUi(player, UINAME_CROSSHAIR);
            CuiHelper.AddUi(player, container);
        }

        private static void CreateMainUI(BasePlayer player, RemoveType removeType)
        {
            var container = UI.CreateElementContainer("Hud", UINAME_MAIN, configData.uiS.removerToolBackgroundColor, configData.uiS.removerToolAnchorMin, configData.uiS.removerToolAnchorMax, configData.uiS.removerToolOffsetMin, configData.uiS.removerToolOffsetMax);
            UI.CreatePanel(ref container, UINAME_MAIN, configData.uiS.removeBackgroundColor, configData.uiS.removeAnchorMin, configData.uiS.removeAnchorMax);
            UI.CreateLabel(ref container, UINAME_MAIN, configData.uiS.removeTextColor, _rt.Lang("RemoverToolType", player.UserIDString, GetRemoveTypeName(removeType)), configData.uiS.removeTextSize, configData.uiS.removeTextAnchorMin, configData.uiS.removeTextAnchorMax, TextAnchor.MiddleLeft);
            CuiHelper.DestroyUi(player, UINAME_MAIN);
            CuiHelper.AddUi(player, container);
        }

        private static void UpdateTimeLeftUI(BasePlayer player, RemoveType removeType, int timeLeft, int currentRemoved, int maxRemovable)
        {
            var container = UI.CreateElementContainer(UINAME_MAIN, UINAME_TIMELEFT, configData.uiS.timeLeftBackgroundColor, configData.uiS.timeLeftAnchorMin, configData.uiS.timeLeftAnchorMax);
            UI.CreateLabel(ref container, UINAME_TIMELEFT, configData.uiS.timeLeftTextColor, _rt.Lang("TimeLeft", player.UserIDString, timeLeft, removeType == RemoveType.Normal || removeType == RemoveType.Admin ? maxRemovable == 0 ? $"{currentRemoved} / {_rt.Lang("Unlimit", player.UserIDString)}" : $"{currentRemoved} / {maxRemovable}" : currentRemoved.ToString()), configData.uiS.timeLeftTextSize, configData.uiS.timeLeftTextAnchorMin, configData.uiS.timeLeftTextAnchorMax, TextAnchor.MiddleLeft);
            CuiHelper.DestroyUi(player, UINAME_TIMELEFT);
            CuiHelper.AddUi(player, container);
        }

        private static void UpdateEntityUI(BasePlayer player, BaseEntity targetEntity, RemovableEntityInfo? info)
        {
            var container = UI.CreateElementContainer(UINAME_MAIN, UINAME_ENTITY, configData.uiS.entityBackgroundColor, configData.uiS.entityAnchorMin, configData.uiS.entityAnchorMax);

            string displayName, imageName;
            TryFindEntityName(player, targetEntity, out displayName, out imageName);
            if (info.HasValue && !string.IsNullOrEmpty(info.Value.DisplayName.Value))
            {
                displayName = info.Value.DisplayName.Value;
            }
            UI.CreateLabel(ref container, UINAME_ENTITY, configData.uiS.entityTextColor, displayName, configData.uiS.entityTextSize, configData.uiS.entityTextAnchorMin, configData.uiS.entityTextAnchorMax, TextAnchor.MiddleLeft);
            if (configData.uiS.entityImageEnabled && !string.IsNullOrEmpty(imageName) && _rt.ImageLibrary != null)
            {
                var imageId = info.HasValue && !string.IsNullOrEmpty(info.Value.ImageId.Value) ? info.Value.ImageId.Value : GetEntityImage(imageName);
                if (!string.IsNullOrEmpty(imageId))
                {
                    UI.CreateImage(ref container, UINAME_ENTITY, imageId, configData.uiS.entityImageAnchorMin, configData.uiS.entityImageAnchorMax);
                }
            }
            CuiHelper.DestroyUi(player, UINAME_ENTITY);
            CuiHelper.AddUi(player, container);
        }

        private static void UpdatePriceUI(BasePlayer player, BaseEntity targetEntity, RemovableEntityInfo? info, bool usePrice)
        {
            Dictionary<string, int> price = null;
            if (usePrice)
            {
                price = _rt.GetPrice(targetEntity, info);
            }
            var container = UI.CreateElementContainer(UINAME_MAIN, UINAME_PRICE, configData.uiS.priceBackgroundColor, configData.uiS.priceAnchorMin, configData.uiS.priceAnchorMax);
            UI.CreateLabel(ref container, UINAME_PRICE, configData.uiS.priceTextColor, _rt.Lang("Price", player.UserIDString), configData.uiS.priceTextSize, configData.uiS.priceTextAnchorMin, configData.uiS.priceTextAnchorMax, TextAnchor.MiddleLeft);
            if (price == null || price.Count == 0)
            {
                UI.CreateLabel(ref container, UINAME_PRICE, configData.uiS.price2TextColor, _rt.Lang("Free", player.UserIDString), configData.uiS.price2TextSize, configData.uiS.price2TextAnchorMin, configData.uiS.price2TextAnchorMax, TextAnchor.MiddleLeft);
            }
            else
            {
                var anchorMin = configData.uiS.Price2TextAnchorMin;
                var anchorMax = configData.uiS.Price2TextAnchorMax;
                float x = (anchorMax.y - anchorMin.y) / price.Count;
                int textSize = configData.uiS.price2TextSize - price.Count;
                string language = _rt.lang.GetLanguage(player.UserIDString);

                int i = 0;
                foreach (var entry in price)
                {
                    var itemInfo = info?.Price[entry.Key];
                    string displayText = !itemInfo.HasValue || string.IsNullOrEmpty(itemInfo.Value.DisplayName.Value)
                        ? $"{GetDisplayNameByPriceName(language, entry.Key)} x{entry.Value}"
                        : $"{itemInfo.Value.DisplayName.Value} x{itemInfo.Value.Amount.Value}";

                    UI.CreateLabel(ref container, UINAME_PRICE, configData.uiS.price2TextColor, displayText, textSize, $"{anchorMin.x} {anchorMin.y + i * x}", $"{anchorMax.x} {anchorMin.y + (i + 1) * x}", TextAnchor.MiddleLeft);
                    if (configData.uiS.imageEnabled && _rt.ImageLibrary != null)
                    {
                        var image = itemInfo.HasValue && !string.IsNullOrEmpty(itemInfo.Value.ImageId.Value) ? itemInfo.Value.ImageId.Value : GetItemImage(entry.Key);
                        if (!string.IsNullOrEmpty(image))
                        {
                            UI.CreateImage(ref container, UINAME_PRICE, image, $"{anchorMax.x - configData.uiS.rightDistance - x * configData.uiS.imageScale} {anchorMin.y + i * x}", $"{anchorMax.x - configData.uiS.rightDistance} {anchorMin.y + (i + 1) * x}");
                        }
                    }
                    i++;
                }
            }
            CuiHelper.DestroyUi(player, UINAME_PRICE);
            CuiHelper.AddUi(player, container);
        }

        private static void UpdateRefundUI(BasePlayer player, BaseEntity targetEntity, RemovableEntityInfo? info, bool useRefund)
        {
            Dictionary<string, int> refund = null;
            if (useRefund)
            {
                refund = _rt.GetRefund(targetEntity, info);
            }
            var container = UI.CreateElementContainer(UINAME_MAIN, UINAME_REFUND, configData.uiS.refundBackgroundColor, configData.uiS.refundAnchorMin, configData.uiS.refundAnchorMax);
            UI.CreateLabel(ref container, UINAME_REFUND, configData.uiS.refundTextColor, _rt.Lang("Refund", player.UserIDString), configData.uiS.refundTextSize, configData.uiS.refundTextAnchorMin, configData.uiS.refundTextAnchorMax, TextAnchor.MiddleLeft);

            if (refund == null || refund.Count == 0)
            {
                UI.CreateLabel(ref container, UINAME_REFUND, configData.uiS.refund2TextColor, _rt.Lang("Nothing", player.UserIDString), configData.uiS.refund2TextSize, configData.uiS.refund2TextAnchorMin, configData.uiS.refund2TextAnchorMax, TextAnchor.MiddleLeft);
            }
            else
            {
                var anchorMin = configData.uiS.Refund2TextAnchorMin;
                var anchorMax = configData.uiS.Refund2TextAnchorMax;
                float x = (anchorMax.y - anchorMin.y) / refund.Count;
                int textSize = configData.uiS.refund2TextSize - refund.Count;
                string language = _rt.lang.GetLanguage(player.UserIDString);

                int i = 0;
                foreach (var entry in refund)
                {
                    var itemInfo = info?.Refund[entry.Key];
                    string displayText = !itemInfo.HasValue || string.IsNullOrEmpty(itemInfo.Value.DisplayName.Value)
                        ? $"{GetDisplayNameByPriceName(language, entry.Key)} x{entry.Value}"
                        : $"{itemInfo.Value.DisplayName.Value} x{itemInfo.Value.Amount.Value}";

                    UI.CreateLabel(ref container, UINAME_REFUND, configData.uiS.refund2TextColor, displayText, textSize, $"{anchorMin.x} {anchorMin.y + i * x}", $"{anchorMax.x} {anchorMin.y + (i + 1) * x}", TextAnchor.MiddleLeft);
                    if (configData.uiS.imageEnabled && _rt.ImageLibrary != null)
                    {
                        var image = itemInfo.HasValue && !string.IsNullOrEmpty(itemInfo.Value.ImageId.Value) ? itemInfo.Value.ImageId.Value : GetItemImage(entry.Key);
                        if (!string.IsNullOrEmpty(image))
                        {
                            UI.CreateImage(ref container, UINAME_REFUND, image, $"{anchorMax.x - configData.uiS.rightDistance - x * configData.uiS.imageScale} {anchorMin.y + i * x}", $"{anchorMax.x - configData.uiS.rightDistance} {anchorMin.y + (i + 1) * x}");
                        }
                    }
                    i++;
                }
            }
            CuiHelper.DestroyUi(player, UINAME_REFUND);
            CuiHelper.AddUi(player, container);
        }

        private static void UpdateAuthorizationUI(BasePlayer player, RemoveType removeType, BaseEntity targetEntity, RemovableEntityInfo? info, bool shouldPay)
        {
            string reason;
            string color = _rt.CanRemoveEntity(player, removeType, targetEntity, info, shouldPay, out reason) ? configData.uiS.allowedBackgroundColor : configData.uiS.refusedBackgroundColor;
            var container = UI.CreateElementContainer(UINAME_MAIN, UINAME_AUTH, color, configData.uiS.authorizationsAnchorMin, configData.uiS.authorizationsAnchorMax);
            UI.CreateLabel(ref container, UINAME_AUTH, configData.uiS.authorizationsTextColor, reason, configData.uiS.authorizationsTextSize, configData.uiS.authorizationsTextAnchorMin, configData.uiS.authorizationsTextAnchorMax, TextAnchor.MiddleLeft);
            CuiHelper.DestroyUi(player, UINAME_AUTH);
            CuiHelper.AddUi(player, container);
        }

        private static void DestroyAllUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UINAME_CROSSHAIR);
            CuiHelper.DestroyUi(player, UINAME_MAIN);
        }

        private static void DestroyUiEntry(BasePlayer player, UiEntry uiEntry)
        {
            switch (uiEntry)
            {
                case UiEntry.Entity:
                    CuiHelper.DestroyUi(player, UINAME_ENTITY);
                    return;

                case UiEntry.Price:
                    CuiHelper.DestroyUi(player, UINAME_PRICE);
                    return;

                case UiEntry.Refund:
                    CuiHelper.DestroyUi(player, UINAME_REFUND);
                    return;

                case UiEntry.Auth:
                    CuiHelper.DestroyUi(player, UINAME_AUTH);
                    return;
            }
        }

        #endregion UI

        #region ToolRemover Component

        #region Tool Helpers

        private static bool IsSpecificTool(BasePlayer player)
        {
            var heldItem = player.GetActiveItem();
            return IsSpecificTool(heldItem);
        }

        private static bool IsSpecificTool(Item heldItem)
        {
            if (heldItem != null && heldItem.info.shortname == configData.removerModeS.specificToolShortname)
            {
                if (configData.removerModeS.specificToolSkin < 0) return true;
                return heldItem.skin == (ulong)configData.removerModeS.specificToolSkin;
            }
            return false;
        }

        private static bool IsMeleeTool(BasePlayer player)
        {
            var heldItem = player.GetActiveItem();
            return IsMeleeTool(heldItem);
        }

        private static bool IsMeleeTool(Item heldItem)
        {
            if (heldItem != null && heldItem.info.shortname == configData.removerModeS.meleeHitItemShortname)
            {
                if (configData.removerModeS.meleeHitModeSkin < 0) return true;
                return heldItem.skin == (ulong)configData.removerModeS.meleeHitModeSkin;
            }
            return false;
        }

        #endregion Tool Helpers

        private class ToolRemover : FacepunchBehaviour
        {
            private const float MinInterval = 0.2f;

            public int currentRemoved { get; set; }
            public BaseEntity hitEntity { get; set; }
            public bool canOverride { get; private set; }
            public BasePlayer player { get; private set; }
            public RemoveType removeType { get; private set; }

            private bool _resetTime;
            private bool _shouldPay;
            private bool _shouldRefund;
            private int _removeTime;
            private int _maxRemovable;
            private float _distance;
            private float _removeInterval;

            private int _timeLeft;
            private float _lastRemove;
            private uint _currentItemId;
            private bool _disableInHand;

            private UiEntry _activeUiEntries;
            private Item _lastHeldItem;
            private BaseEntity _targetEntity;

            private void Awake()
            {
                player = GetComponent<BasePlayer>();
                _currentItemId = player.svActiveItemID;
                _disableInHand = _removeMode == RemoveMode.MeleeHit && configData.removerModeS.meleeHitDisableInHand
                                 || _removeMode == RemoveMode.SpecificTool && configData.removerModeS.specificToolDisableInHand;
                if (_disableInHand)
                {
                    _lastHeldItem = player.GetActiveItem();
                }
                if (_removeMode == RemoveMode.NoHeld)
                {
                    UnEquip();
                }
            }

            public void Init(RemoveType removeType, int removeTime, int maxRemovable, float distance, float removeInterval, bool shouldPay, bool shouldRefund, bool resetTime, bool canOverride)
            {
                this.removeType = removeType;
                this.canOverride = canOverride;

                _distance = distance;
                _resetTime = resetTime;
                _removeTime = _timeLeft = removeTime;
                _removeInterval = Mathf.Max(MinInterval, removeInterval);
                if (this.removeType == RemoveType.Normal)
                {
                    _maxRemovable = maxRemovable;
                    _shouldPay = shouldPay && configData.removeS.priceEnabled;
                    _shouldRefund = shouldRefund && configData.removeS.refundEnabled;
                    _rt.PrintDebug($"{player.displayName}({player.userID}) have Enabled the remover tool.");
                    Interface.CallHook("OnRemoverToolActivated", player);
                }
                else
                {
                    _maxRemovable = currentRemoved = 0;
                    _shouldPay = _shouldRefund = false;
                }

                DestroyAllUI(player);
                if (configData.uiS.showCrosshair)
                {
                    CreateCrosshairUI(player);
                }

                if (configData.uiS.enabled)
                {
                    CreateMainUI(player, this.removeType);
                }

                CancelInvoke(RemoveUpdate);
                InvokeRepeating(RemoveUpdate, 0f, 1f);
            }

            private void RemoveUpdate()
            {
                if (configData.uiS.enabled)
                {
                    _targetEntity = GetTargetEntity();
                    UpdateTimeLeftUI(player, removeType, _timeLeft, currentRemoved, _maxRemovable);

                    var info = removeType == RemoveType.Normal ? GetRemovableEntityInfo(_targetEntity, player) : null;

                    bool canShow = (info.HasValue || _targetEntity != null) && CanEntityBeDisplayed(_targetEntity, player);
                    if (HandleUiEntry(UiEntry.Entity, canShow))
                    {
                        UpdateEntityUI(player, _targetEntity, info);
                    }
                    if (removeType == RemoveType.Normal)
                    {
                        if (configData.uiS.authorizationEnabled)
                        {
                            if (HandleUiEntry(UiEntry.Auth, canShow))
                            {
                                UpdateAuthorizationUI(player, removeType, _targetEntity, info, _shouldPay);
                            }
                        }
                        if (configData.uiS.priceEnabled || configData.uiS.refundEnabled)
                        {
                            canShow = canShow && (info.HasValue || HasEntityEnabled(_targetEntity));
                            if (configData.uiS.priceEnabled)
                            {
                                if (HandleUiEntry(UiEntry.Price, canShow))
                                {
                                    UpdatePriceUI(player, _targetEntity, info, _shouldPay);
                                }
                            }
                            if (configData.uiS.refundEnabled)
                            {
                                if (HandleUiEntry(UiEntry.Refund, canShow))
                                {
                                    UpdateRefundUI(player, _targetEntity, info, _shouldRefund);
                                }
                            }
                        }
                    }
                }

                if (_timeLeft-- <= 0)
                {
                    DisableTool();
                }
            }

            private BaseEntity GetTargetEntity()
            {
                RaycastHit hitInfo;
                return Physics.Raycast(player.eyes.HeadRay(), out hitInfo, _distance, LAYER_TARGET) ? hitInfo.GetEntity() : null;
            }

            private void Update()
            {
                if (player == null || !player.IsConnected || !player.CanInteract())
                {
                    DisableTool();
                    return;
                }
                if (player.svActiveItemID != _currentItemId)
                {
                    if (_disableInHand)
                    {
                        var heldItem = player.GetActiveItem();
                        if (_removeMode == RemoveMode.MeleeHit && IsMeleeTool(_lastHeldItem) && !IsMeleeTool(heldItem) ||
                            _removeMode == RemoveMode.SpecificTool && IsSpecificTool(_lastHeldItem) && !IsSpecificTool(heldItem))
                        {
                            DisableTool();
                            return;
                        }
                        _lastHeldItem = heldItem;
                    }
                    if (_removeMode == RemoveMode.NoHeld)
                    {
                        if (player.svActiveItemID != 0)
                        {
                            if (configData.removerModeS.noHeldDisableInHand)
                            {
                                DisableTool();
                                return;
                            }
                            UnEquip();
                        }
                    }
                    _currentItemId = player.svActiveItemID;
                }
                if (Time.realtimeSinceStartup - _lastRemove >= _removeInterval)
                {
                    if (_removeMode == RemoveMode.MeleeHit)
                    {
                        if (hitEntity == null) return;
                        _targetEntity = hitEntity;
                        hitEntity = null;
                    }
                    else
                    {
                        if (!player.serverInput.IsDown(_removeButton))
                        {
                            return;
                        }
                        if (_removeMode == RemoveMode.SpecificTool && !IsSpecificTool(player))
                        {
                            //rt.Print(player,rt.Lang("UsageOfRemove",player.UserIDString));
                            return;
                        }
                        _targetEntity = GetTargetEntity();
                    }
                    if (_rt.TryRemove(player, _targetEntity, removeType, _shouldPay, _shouldRefund))
                    {
                        if (_resetTime)
                        {
                            _timeLeft = _removeTime;
                        }
                        if (removeType == RemoveType.Normal || removeType == RemoveType.Admin)
                        {
                            currentRemoved++;
                        }
                        if (configData.globalS.startCooldownOnRemoved && removeType == RemoveType.Normal)
                        {
                            _rt._cooldownTimes[player.userID] = Time.realtimeSinceStartup;
                        }
                    }
                    _lastRemove = Time.realtimeSinceStartup;
                }
                if (removeType == RemoveType.Normal && _maxRemovable > 0 && currentRemoved >= _maxRemovable)
                {
                    _rt.Print(player, _rt.Lang("EntityLimit", player.UserIDString, _maxRemovable));
                    DisableTool(false);
                };
            }

            private void UnEquip()
            {
                //player.lastReceivedTick.activeItem = 0;
                var activeItem = player.GetActiveItem();
                if (activeItem?.GetHeldEntity() is HeldEntity)
                {
                    var slot = activeItem.position;
                    activeItem.SetParent(null);
                    player.Invoke(() =>
                    {
                        if (activeItem == null || !activeItem.IsValid()) return;
                        if (player.inventory.containerBelt.GetSlot(slot) == null)
                        {
                            activeItem.position = slot;
                            activeItem.SetParent(player.inventory.containerBelt);
                        }
                        else player.GiveItem(activeItem);
                    }, 0.2f);
                }
            }

            private bool HandleUiEntry(UiEntry uiEntry, bool canShow)
            {
                if (canShow)
                {
                    _activeUiEntries |= uiEntry;
                    return true;
                }

                if (_activeUiEntries.HasFlag(uiEntry))
                {
                    _activeUiEntries &= ~uiEntry;
                    DestroyUiEntry(player, uiEntry);
                }
                return false;
            }

            public void DisableTool(bool showMessage = true)
            {
                if (showMessage)
                {
                    if (_rt != null && player != null && player.IsConnected)
                    {
                        _rt.Print(player, _rt.Lang("ToolDisabled", player.UserIDString));
                    }
                }

                if (removeType == RemoveType.Normal)
                {
                    if (_rt != null && player != null)
                    {
                        _rt.PrintDebug($"{player.displayName}({player.userID}) have Disabled the remover tool.");
                    }
                    Interface.CallHook("OnRemoverToolDeactivated", player);
                }
                DestroyAllUI(player);
                Destroy(this);
            }

            private void OnDestroy()
            {
                if (_rt != null && removeType == RemoveType.Normal)
                {
                    if (configData != null && !configData.globalS.startCooldownOnRemoved)
                    {
                        _rt._cooldownTimes[player.userID] = Time.realtimeSinceStartup;
                    }
                }
            }
        }

        #endregion ToolRemover Component

        #region TryRemove

        private bool TryRemove(BasePlayer player, BaseEntity targetEntity, RemoveType removeType, bool shouldPay, bool shouldRefund)
        {
            switch (removeType)
            {
                case RemoveType.Admin:
                    {
                        var target = targetEntity as BasePlayer;
                        if (target != null)
                        {
                            if (target.userID.IsSteamId() && target.IsConnected)
                            {
                                target.Kick("From RemoverTool Plugin");
                                return true;
                            }
                        }
                        DoRemove(targetEntity, configData.removeTypeS[RemoveType.Admin].gibs ? BaseNetworkable.DestroyMode.Gib : BaseNetworkable.DestroyMode.None);
                        return true;
                    }
                case RemoveType.All:
                    {
                        if (_removeAllCoroutine != null)
                        {
                            Print(player, Lang("AlreadyRemoveAll", player.UserIDString));
                            return false;
                        }
                        _removeAllCoroutine = ServerMgr.Instance.StartCoroutine(RemoveAll(targetEntity, player));
                        Print(player, Lang("StartRemoveAll", player.UserIDString));
                        return true;
                    }
                case RemoveType.External:
                    {
                        var stabilityEntity = targetEntity as StabilityEntity;
                        if (stabilityEntity == null || !IsExternalWall(stabilityEntity))
                        {
                            Print(player, Lang("NotExternalWall", player.UserIDString));
                            return false;
                        }
                        if (_removeExternalCoroutine != null)
                        {
                            Print(player, Lang("AlreadyRemoveExternal", player.UserIDString));
                            return false;
                        }
                        _removeExternalCoroutine = ServerMgr.Instance.StartCoroutine(RemoveExternal(stabilityEntity, player));
                        Print(player, Lang("StartRemoveExternal", player.UserIDString));
                        return true;
                    }
                case RemoveType.Structure:
                    {
                        var decayEntity = targetEntity as DecayEntity;
                        if (decayEntity == null)
                        {
                            Print(player, Lang("NotStructure", player.UserIDString));
                            return false;
                        }
                        if (_removeStructureCoroutine != null)
                        {
                            Print(player, Lang("AlreadyRemoveStructure", player.UserIDString));
                            return false;
                        }
                        _removeStructureCoroutine = ServerMgr.Instance.StartCoroutine(RemoveStructure(decayEntity, player));
                        Print(player, Lang("StartRemoveStructure", player.UserIDString));
                        return true;
                    }
            }

            var info = GetRemovableEntityInfo(targetEntity, player);

            string reason;
            if (!CanRemoveEntity(player, removeType, targetEntity, info, shouldPay, out reason))
            {
                Print(player, reason);
                return false;
            }

            DropContainerEntity(targetEntity);

            if (shouldPay)
            {
                bool flag = TryPay(player, targetEntity, info);
                if (!flag)
                {
                    Print(player, Lang("CantPay", player.UserIDString));
                    return false;
                }
            }

            if (shouldRefund)
            {
                GiveRefund(player, targetEntity, info);
            }

            DoNormalRemove(player, targetEntity, configData.removeTypeS[RemoveType.Normal].gibs);
            return true;
        }

        private bool CanRemoveEntity(BasePlayer player, RemoveType removeType, BaseEntity targetEntity, RemovableEntityInfo? info, bool shouldPay, out string reason)
        {
            if (removeType != RemoveType.Normal)
            {
                reason = null;
                return true;
            }
            if (targetEntity == null || !CanEntityBeDisplayed(targetEntity, player))
            {
                reason = Lang("NotFoundOrFar", player.UserIDString);
                return false;
            }
            if (targetEntity.IsDestroyed)
            {
                reason = Lang("InvalidEntity", player.UserIDString);
                return false;
            }
            if (!info.HasValue)
            {
                if (!IsRemovableEntity(targetEntity))
                {
                    reason = Lang("InvalidEntity", player.UserIDString);
                    return false;
                }
                if (!HasEntityEnabled(targetEntity))
                {
                    reason = Lang("EntityDisabled", player.UserIDString);
                    return false;
                }
            }
            var result = Interface.CallHook("canRemove", player, targetEntity);
            if (result != null)
            {
                reason = result is string ? (string)result : Lang("BeBlocked", player.UserIDString);
                return false;
            }
            if (!configData.damagedEntityS.enabled && IsDamagedEntity(targetEntity))
            {
                reason = Lang("DamagedEntity", player.UserIDString);
                return false;
            }
            float timeLeft;
            if (configData.raidS.enabled && IsRaidBlocked(player, targetEntity, out timeLeft))
            {
                reason = Lang("RaidBlocked", player.UserIDString, Math.Ceiling(timeLeft));
                return false;
            }
            if (configData.globalS.entityTimeLimit && IsEntityTimeLimit(targetEntity))
            {
                reason = Lang("EntityTimeLimit", player.UserIDString, configData.globalS.limitTime);
                return false;
            }
            if (!configData.containerS.removeNotEmptyStorage)
            {
                var storageContainer = targetEntity as StorageContainer;
                if (storageContainer?.inventory?.itemList?.Count > 0)
                {
                    reason = Lang("StorageNotEmpty", player.UserIDString);
                    return false;
                }
            }
            if (!configData.containerS.removeNotEmptyIoEntity)
            {
                var containerIOEntity = targetEntity as ContainerIOEntity;
                if (containerIOEntity?.inventory?.itemList?.Count > 0)
                {
                    reason = Lang("StorageNotEmpty", player.UserIDString);
                    return false;
                }
            }
            if (shouldPay && !CanPay(player, targetEntity, info))
            {
                reason = Lang("NotEnoughCost", player.UserIDString);
                return false;
            }
            if (!HasAccess(player, targetEntity))
            {
                reason = Lang("NotRemoveAccess", player.UserIDString);
                return false;
            }
            // Prevent not access players from knowing that there has a stash
            if (configData.globalS.checkStash && HasStashUnderFoundation(targetEntity as BuildingBlock))
            {
                reason = Lang("HasStash", player.UserIDString);
                return false;
            }
            reason = Lang("CanRemove", player.UserIDString);
            return true;
        }

        private bool HasAccess(BasePlayer player, BaseEntity targetEntity)
        {
            if (configData.globalS.useBuildingOwners && BuildingOwners != null)
            {
                var buildingBlock = targetEntity as BuildingBlock;
                if (buildingBlock != null)
                {
                    var result = BuildingOwners?.Call("FindBlockData", buildingBlock) as string;
                    if (result != null)
                    {
                        ulong ownerID = ulong.Parse(result);
                        if (AreFriends(ownerID, player.userID))
                        {
                            return true;
                        }
                    }
                }
            }
            //var 1 = configData.globalS.excludeTwigs && (targetEntity as BuildingBlock)?.grade == BuildingGrade.Enum.Twigs;
            if (configData.globalS.useEntityOwners)
            {
                if (AreFriends(targetEntity.OwnerID, player.userID))
                {
                    if (!configData.globalS.useToolCupboards)
                    {
                        return true;
                    }
                    if (HasTotalAccess(player, targetEntity))
                    {
                        return true;
                    }
                }

                return false;
            }
            if (configData.globalS.useToolCupboards)
            {
                if (HasTotalAccess(player, targetEntity))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasTotalAccess(BasePlayer player, BaseEntity targetEntity)
        {
            if (player.IsBuildingBlocked(targetEntity.WorldSpaceBounds()))
            {
                return false;
            }
            if (configData.globalS.useBuildingLocks && !CanOpenAllLocks(player, targetEntity))
            {
                //reason = Lang("Can'tOpenAllLocks", player.UserIDString);
                return false;
            }
            return true;
        }

        private static bool CanOpenAllLocks(BasePlayer player, BaseEntity targetEntity)
        {
            var decayEntities = Pool.GetList<DecayEntity>();
            var building = targetEntity.GetBuildingPrivilege()?.GetBuilding() ?? (targetEntity as DecayEntity)?.GetBuilding();
            if (building != null)
            {
                decayEntities.AddRange(building.decayEntities);
            }
            /*else//An entity placed outside
            {
                Vis.Entities(targetEntity.transform.position, 9f, decayEntities, Layers.Mask.Construction | Layers.Mask.Deployed);
            }*/
            foreach (var decayEntity in decayEntities)
            {
                if ((decayEntity is Door || decayEntity is BoxStorage) && decayEntity.OwnerID.IsSteamId())
                {
                    var lockEntity = decayEntity.GetSlot(BaseEntity.Slot.Lock) as BaseLock;
                    if (lockEntity != null && !OnTryToOpen(player, lockEntity))
                    {
                        Pool.FreeList(ref decayEntities);
                        return false;
                    }
                }
            }
            Pool.FreeList(ref decayEntities);
            return true;
        }

        private static bool OnTryToOpen(BasePlayer player, BaseLock baseLock)
        {
            var codeLock = baseLock as CodeLock;
            if (codeLock != null)
            {
                var obj = Interface.CallHook("CanUseLockedEntity", player, codeLock);
                if (obj is bool)
                {
                    return (bool)obj;
                }
                if (!codeLock.IsLocked())
                {
                    return true;
                }
                if (codeLock.whitelistPlayers.Contains(player.userID) || codeLock.guestPlayers.Contains(player.userID))
                {
                    return true;
                }
                return false;
            }
            var keyLock = baseLock as KeyLock;
            if (keyLock != null)
            {
                return keyLock.OnTryToOpen(player);
            }

            return false;
        }

        private static bool HasStashUnderFoundation(BuildingBlock buildingBlock)
        {
            if (buildingBlock == null) return false;
            if (buildingBlock.ShortPrefabName.Contains("foundation"))
            {
                return GamePhysics.CheckOBB<StashContainer>(buildingBlock.WorldSpaceBounds());
            }
            return false;
        }

        private static bool IsDamagedEntity(BaseEntity entity)
        {
            var baseCombatEntity = entity as BaseCombatEntity;
            if (baseCombatEntity == null || !baseCombatEntity.repair.enabled)
            {
                return false;
            }
            if (configData.damagedEntityS.excludeBuildingBlocks &&
                (baseCombatEntity is BuildingBlock || baseCombatEntity is SimpleBuildingBlock))
            {
                return false;
            }
            if (configData.damagedEntityS.excludeQuarries && !(baseCombatEntity is BuildingBlock) &&
                baseCombatEntity.repair.itemTarget?.Blueprint == null) //Quarry
            {
                return false;
            }

            if (baseCombatEntity.healthFraction * 100f >= configData.damagedEntityS.percentage)
            {
                return false;
            }
            return true;
        }

        private static bool IsEntityTimeLimit(BaseEntity entity)
        {
            if (entity.net == null) return true;
            float spawnedTime;
            if (_rt._entitySpawnedTimes.TryGetValue(entity.net.ID, out spawnedTime))
            {
                return Time.realtimeSinceStartup - spawnedTime > configData.globalS.limitTime;
            }
            return true;
        }

        private static void DropContainerEntity(BaseEntity targetEntity)
        {
            var storageContainer = targetEntity as StorageContainer;
            if (storageContainer != null && storageContainer.inventory?.itemList?.Count > 0)
            {
                if (configData.containerS.dropContainerStorage || configData.containerS.dropItemsStorage)
                {
                    if (Interface.CallHook("OnDropContainerEntity", storageContainer) == null)
                    {
                        if (configData.containerS.dropContainerStorage)
                        {
                            DropItemContainer(storageContainer.inventory, storageContainer.GetDropPosition(),
                                storageContainer.transform.rotation);
                        }
                        else if (configData.containerS.dropItemsStorage)
                        {
                            storageContainer.DropItems();
                            //DropUtil.DropItems(storageContainer.inventory, storageContainer.GetDropPosition());
                        }
                    }
                }
            }
            else
            {
                var containerIoEntity = targetEntity as ContainerIOEntity;
                if (containerIoEntity != null && containerIoEntity.inventory?.itemList?.Count > 0)
                {
                    if (configData.containerS.dropContainerIoEntity || configData.containerS.dropItemsIoEntity)
                    {
                        if (Interface.CallHook("OnDropContainerEntity", containerIoEntity) == null)
                        {
                            if (configData.containerS.dropContainerIoEntity)
                            {
                                DropItemContainer(containerIoEntity.inventory, containerIoEntity.GetDropPosition(), containerIoEntity.transform.rotation);
                            }
                            else if (configData.containerS.dropItemsIoEntity)
                            {
                                containerIoEntity.DropItems();
                                //DropUtil.DropItems(containerIoEntity.inventory, containerIoEntity.GetDropPosition());
                            }
                        }
                    }
                }
            }
        }

        #region AreFriends

        private bool AreFriends(ulong playerID, ulong friendID)
        {
            if (!playerID.IsSteamId()) return false;
            if (playerID == friendID) return true;
            if (configData.globalS.useTeams && SameTeam(playerID, friendID)) return true;
            if (configData.globalS.useFriends && HasFriend(playerID, friendID)) return true;
            if (configData.globalS.useClans && SameClan(playerID, friendID)) return true;
            return false;
        }

        private static bool SameTeam(ulong playerID, ulong friendID)
        {
            if (!RelationshipManager.TeamsEnabled()) return false;
            var playerTeam = RelationshipManager.ServerInstance.FindPlayersTeam(playerID);
            if (playerTeam == null) return false;
            var friendTeam = RelationshipManager.ServerInstance.FindPlayersTeam(friendID);
            if (friendTeam == null) return false;
            return playerTeam == friendTeam;
        }

        private bool HasFriend(ulong playerID, ulong friendID)
        {
            if (Friends == null) return false;
            return (bool)Friends.Call("HasFriend", playerID, friendID);
        }

        private bool SameClan(ulong playerID, ulong friendID)
        {
            if (Clans == null) return false;
            //Clans
            var isMember = Clans.Call("IsClanMember", playerID.ToString(), friendID.ToString());
            if (isMember != null) return (bool)isMember;
            //Rust:IO Clans
            var playerClan = Clans.Call("GetClanOf", playerID);
            if (playerClan == null) return false;
            var friendClan = Clans.Call("GetClanOf", friendID);
            if (friendClan == null) return false;
            return (string)playerClan == (string)friendClan;
        }

        #endregion AreFriends

        #endregion TryRemove

        #region Pay

        private bool TryPay(BasePlayer player, BaseEntity targetEntity, RemovableEntityInfo? info)
        {
            var price = GetPrice(targetEntity, info);
            if (price == null || price.Count == 0)
            {
                return true;
            }
            var collect = Pool.GetList<Item>();
            try
            {
                foreach (var entry in price)
                {
                    if (entry.Value <= 0)
                    {
                        continue;
                    }
                    int itemId;
                    if (_itemShortNameToItemId.TryGetValue(entry.Key, out itemId))
                    {
                        player.inventory.Take(collect, itemId, entry.Value);
                        player.Command("note.inv", itemId, -entry.Value);
                    }
                    else if (!CheckOrPay(targetEntity, player, entry.Key, entry.Value, false, info))
                    {
                        return false;
                    }
                }
            }
            catch (Exception e)
            {
                PrintError($"{player} couldn't pay to remove entity. Error: {e}");
                return false;
            }
            finally
            {
                foreach (Item item in collect)
                {
                    item.Remove();
                }
                Pool.FreeList(ref collect);
            }
            return true;
        }

        private Dictionary<string, int> GetPrice(BaseEntity targetEntity, RemovableEntityInfo? info)
        {
            if (info.HasValue)
            {
                return info.Value.Price.ValueName2Amount;
            }
            var buildingBlock = targetEntity as BuildingBlock;
            if (buildingBlock != null)
            {
                var entityName = _prefabNameToStructure[buildingBlock.PrefabName];
                BuildingBlocksSettings buildingBlockSettings;
                if (configData.removeS.buildingBlockS.TryGetValue(entityName, out buildingBlockSettings))
                {
                    BuildingGradeSettings buildingGradeSettings;
                    if (buildingBlockSettings.buildingGradeS.TryGetValue(buildingBlock.grade, out buildingGradeSettings))
                    {
                        if (buildingGradeSettings.priceDict != null)
                        {
                            return buildingGradeSettings.priceDict;
                        }
                        if (buildingGradeSettings.pricePercentage > 0f)
                        {
                            var currentGrade = buildingBlock.currentGrade;
                            if (currentGrade != null)
                            {
                                var price = new Dictionary<string, int>();
                                foreach (var itemAmount in currentGrade.costToBuild)
                                {
                                    var amount = Mathf.RoundToInt(itemAmount.amount * buildingGradeSettings.pricePercentage / 100);
                                    if (amount <= 0) continue;
                                    price.Add(itemAmount.itemDef.shortname, amount);
                                }

                                return price;
                            }
                        }
                        else if (buildingGradeSettings.pricePercentage < 0f)
                        {
                            var currentGrade = buildingBlock.currentGrade;
                            if (currentGrade != null)
                            {
                                return currentGrade.costToBuild.ToDictionary(x => x.itemDef.shortname, y => Mathf.RoundToInt(y.amount));
                            }
                        }
                    }
                }
            }
            else
            {
                EntitySettings entitySettings;
                if (configData.removeS.entityS.TryGetValue(targetEntity.ShortPrefabName, out entitySettings))
                {
                    return entitySettings.price;
                }
            }
            return null;
        }

        private bool CanPay(BasePlayer player, BaseEntity targetEntity, RemovableEntityInfo? info)
        {
            var price = GetPrice(targetEntity, info);
            if (price == null || price.Count == 0)
            {
                return true;
            }
            foreach (var p in price)
            {
                if (p.Value <= 0)
                {
                    continue;
                }
                int itemId;
                if (_itemShortNameToItemId.TryGetValue(p.Key, out itemId))
                {
                    int amount = player.inventory.GetAmount(itemId);
                    if (amount < p.Value)
                    {
                        return false;
                    }
                }
                else if (!CheckOrPay(targetEntity, player, p.Key, p.Value, true, info))
                {
                    return false;
                }
            }
            return true;
        }

        private bool CheckOrPay(BaseEntity targetEntity, BasePlayer player, string itemName, int itemAmount, bool check, RemovableEntityInfo? info)
        {
            if (itemAmount <= 0)
            {
                return true;
            }
            switch (itemName.ToLower())
            {
                case ECONOMICS_KEY:
                    if (Economics == null)
                    {
                        return false;
                    }
                    if (check)
                    {
                        var balance = Economics.Call("Balance", player.userID);
                        if (balance == null)
                        {
                            return false;
                        }
                        if ((double)balance < itemAmount)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        var withdraw = Economics.Call("Withdraw", player.userID, (double)itemAmount);
                        if (withdraw == null || !(bool)withdraw)
                        {
                            return false;
                        }
                    }
                    return true;

                case SERVER_REWARDS_KEY:
                    if (ServerRewards == null)
                    {
                        return false;
                    }
                    if (check)
                    {
                        var points = ServerRewards.Call("CheckPoints", player.userID);
                        if (points == null)
                        {
                            return false;
                        }

                        if ((int)points < itemAmount)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        var takePoints = ServerRewards.Call("TakePoints", player.userID, itemAmount);
                        if (takePoints == null || !(bool)takePoints)
                        {
                            return false;
                        }
                    }
                    return true;

                default:
                    {
                        var result = Interface.CallHook("OnRemovableEntityCheckOrPay", targetEntity, player, itemName, itemAmount, check);
                        if (result is bool)
                        {
                            return (bool)result;
                        }
                    }

                    return true;
            }
        }

        #endregion Pay

        #region Refund

        private void GiveRefund(BasePlayer player, BaseEntity targetEntity, RemovableEntityInfo? info)
        {
            var refund = GetRefund(targetEntity, info);
            if (refund == null || refund.Count == 0)
            {
                return;
            }
            foreach (var entry in refund)
            {
                var itemName = entry.Key;
                var itemAmount = entry.Value;
                if (itemAmount <= 0)
                {
                    continue;
                }
                int itemId; string shortname;
                _shortPrefabNameToDeployable.TryGetValue(targetEntity.ShortPrefabName, out shortname);
                if (_itemShortNameToItemId.TryGetValue(itemName, out itemId))
                {
                    var isOriginalItem = itemName == shortname;
                    var item = ItemManager.CreateByItemID(itemId, itemAmount, isOriginalItem ? targetEntity.skinID : 0);
                    if (isOriginalItem && item.hasCondition && targetEntity is BaseCombatEntity)
                    {
                        item.condition = item.maxCondition * (targetEntity.Health() / targetEntity.MaxHealth());
                    }
                    player.GiveItem(item);
                }
                else
                {
                    bool flag = false;
                    switch (itemName.ToLower())
                    {
                        case ECONOMICS_KEY:
                            {
                                if (Economics == null) continue;
                                var result = Economics.Call("Deposit", player.userID, (double)itemAmount);
                                if (result != null)
                                {
                                    flag = true;
                                }
                                break;
                            }

                        case SERVER_REWARDS_KEY:
                            {
                                if (ServerRewards == null) continue;
                                var result = ServerRewards.Call("AddPoints", player.userID, itemAmount);
                                if (result != null)
                                {
                                    flag = true;
                                }
                                break;
                            }

                        default:
                            {
                                var result = Interface.CallHook("OnRemovableEntityGiveRefund", targetEntity, player, itemName, itemAmount);
                                if (result == null)
                                {
                                    flag = true;
                                }
                                break;
                            }
                    }

                    if (!flag)
                    {
                        PrintError($"{player} didn't receive refund maybe {itemName} doesn't seem to be a valid item name");
                    }
                }
            }
        }

        private Dictionary<string, int> GetRefund(BaseEntity targetEntity, RemovableEntityInfo? info)
        {
            if (info.HasValue)
            {
                return info.Value.Refund.ValueName2Amount;
            }
            var buildingBlock = targetEntity.GetComponent<BuildingBlock>();
            if (buildingBlock != null)
            {
                var entityName = _prefabNameToStructure[buildingBlock.PrefabName];
                BuildingBlocksSettings buildingBlockSettings;
                if (configData.removeS.buildingBlockS.TryGetValue(entityName, out buildingBlockSettings))
                {
                    BuildingGradeSettings buildingGradeSettings;
                    if (buildingBlockSettings.buildingGradeS.TryGetValue(buildingBlock.grade, out buildingGradeSettings))
                    {
                        if (buildingGradeSettings.refundDict != null)
                        {
                            return buildingGradeSettings.refundDict;
                        }
                        if (buildingGradeSettings.refundPercentage > 0f)
                        {
                            var currentGrade = buildingBlock.currentGrade;
                            if (currentGrade != null)
                            {
                                var refund = new Dictionary<string, int>();
                                foreach (var itemAmount in currentGrade.costToBuild)
                                {
                                    var amount = Mathf.RoundToInt(itemAmount.amount * buildingGradeSettings.refundPercentage / 100);
                                    if (amount <= 0) continue;
                                    refund.Add(itemAmount.itemDef.shortname, amount);
                                }
                                return refund;
                            }
                        }
                        else if (buildingGradeSettings.refundPercentage < 0f)
                        {
                            var currentGrade = buildingBlock.currentGrade;
                            if (currentGrade != null)
                            {
                                return currentGrade.costToBuild.ToDictionary(x => x.itemDef.shortname, y => Mathf.RoundToInt(y.amount));
                            }
                        }
                    }
                }
            }
            else
            {
                EntitySettings entitySettings;
                if (configData.removeS.entityS.TryGetValue(targetEntity.ShortPrefabName, out entitySettings))
                {
                    if (configData.removeS.refundSlot)
                    {
                        var slots = GetSlots(targetEntity);
                        if (slots.Any())
                        {
                            var refund = new Dictionary<string, int>(entitySettings.refund);
                            foreach (var slotName in slots)
                            {
                                if (!refund.ContainsKey(slotName))
                                {
                                    refund.Add(slotName, 0);
                                }
                                refund[slotName]++;
                            }
                            return refund;
                        }
                    }
                    return entitySettings.refund;
                }
            }
            return null;
        }

        private IEnumerable<string> GetSlots(BaseEntity targetEntity)
        {
            foreach (BaseEntity.Slot slot in Enum.GetValues(typeof(BaseEntity.Slot)))
            {
                if (targetEntity.HasSlot(slot))
                {
                    var entity = targetEntity.GetSlot(slot);
                    if (entity != null)
                    {
                        string slotName;
                        if (_shortPrefabNameToDeployable.TryGetValue(entity.ShortPrefabName, out slotName))
                        {
                            yield return slotName;
                        }
                    }
                }
            }
        }

        #endregion Refund

        #region RemoveEntity

        private IEnumerator RemoveAll(BaseEntity sourceEntity, BasePlayer player)
        {
            var removeList = Pool.Get<HashSet<BaseEntity>>();
            yield return GetNearbyEntities(sourceEntity, removeList, LAYER_ALL);
            yield return ProcessContainers(removeList);
            yield return DelayRemove(removeList, player, RemoveType.All);
            Pool.Free(ref removeList);
            _removeAllCoroutine = null;
        }

        private IEnumerator RemoveExternal(StabilityEntity sourceEntity, BasePlayer player)
        {
            var removeList = Pool.Get<HashSet<StabilityEntity>>();
            yield return GetNearbyEntities(sourceEntity, removeList, Rust.Layers.Mask.Construction, IsExternalWall);
            yield return DelayRemove(removeList, player, RemoveType.External);
            Pool.Free(ref removeList);
            _removeExternalCoroutine = null;
        }

        private IEnumerator RemoveStructure(DecayEntity sourceEntity, BasePlayer player)
        {
            var removeList = Pool.Get<HashSet<BaseEntity>>();
            yield return ProcessBuilding(sourceEntity, removeList);
            yield return DelayRemove(removeList, player, RemoveType.Structure);
            Pool.Free(ref removeList);
            _removeStructureCoroutine = null;
        }

        private IEnumerator RemovePlayerEntity(ConsoleSystem.Arg arg, ulong targetID, PlayerEntityRemoveType playerEntityRemoveType)
        {
            int current = 0;
            var removeList = Pool.Get<HashSet<BaseEntity>>();
            switch (playerEntityRemoveType)
            {
                case PlayerEntityRemoveType.All:
                case PlayerEntityRemoveType.Building:
                    bool onlyBuilding = playerEntityRemoveType == PlayerEntityRemoveType.Building;
                    foreach (var serverEntity in BaseNetworkable.serverEntities)
                    {
                        if (++current % 500 == 0) yield return CoroutineEx.waitForEndOfFrame;
                        var entity = serverEntity as BaseEntity;
                        if (entity == null || entity.OwnerID != targetID) continue;
                        if (!onlyBuilding || entity is BuildingBlock)
                        {
                            removeList.Add(entity);
                        }
                    }
                    foreach (var player in BasePlayer.allPlayerList)
                    {
                        if (player.userID == targetID)
                        {
                            if (player.IsConnected) player.Kick("From RemoverTool Plugin");
                            removeList.Add(player);
                            break;
                        }
                    }
                    break;

                case PlayerEntityRemoveType.Cupboard:
                    foreach (var serverEntity in BaseNetworkable.serverEntities)
                    {
                        if (++current % 500 == 0) yield return CoroutineEx.waitForEndOfFrame;
                        var entity = serverEntity as BuildingPrivlidge;
                        if (entity == null || entity.OwnerID != targetID) continue;
                        yield return ProcessBuilding(entity, removeList);
                    }
                    break;
            }
            int removed = removeList.Count(x => x != null && !x.IsDestroyed);
            yield return DelayRemove(removeList);
            Pool.Free(ref removeList);
            Print(arg, $"You have successfully removed {removed} entities of player {targetID}.");
            _removePlayerEntityCoroutine = null;
        }

        private IEnumerator DelayRemove(IEnumerable<BaseEntity> entities, BasePlayer player = null, RemoveType removeType = RemoveType.None)
        {
            int removed = 0;
            var destroyMode = removeType == RemoveType.None ? BaseNetworkable.DestroyMode.None : configData.removeTypeS[removeType].gibs ? BaseNetworkable.DestroyMode.Gib : BaseNetworkable.DestroyMode.None;
            foreach (var entity in entities)
            {
                if (DoRemove(entity, destroyMode) && ++removed % configData.globalS.removePerFrame == 0)
                {
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            if (removeType == RemoveType.None)
            {
                yield break;
            }
            var toolRemover = player?.GetComponent<ToolRemover>();
            if (toolRemover != null && toolRemover.removeType == removeType)
            {
                toolRemover.currentRemoved += removed;
            }
            if (player != null)
            {
                Print(player, Lang($"CompletedRemove{removeType}", player.UserIDString, removed));
            }
        }

        #region RemoveEntity Helpers

        private static IEnumerator GetNearbyEntities<T>(T sourceEntity, HashSet<T> removeList, int layers, Func<T, bool> filter = null) where T : BaseEntity
        {
            int current = 0;
            var checkFrom = Pool.Get<Queue<Vector3>>();
            var nearbyEntities = Pool.GetList<T>();
            checkFrom.Enqueue(sourceEntity.transform.position);
            while (checkFrom.Count > 0)
            {
                nearbyEntities.Clear();
                var position = checkFrom.Dequeue();
                Vis.Entities(position, 3f, nearbyEntities, layers);
                for (var i = 0; i < nearbyEntities.Count; i++)
                {
                    var entity = nearbyEntities[i];
                    if (filter != null && !filter(entity)) continue;
                    if (!removeList.Add(entity)) continue;
                    checkFrom.Enqueue(entity.transform.position);
                }
                if (++current % configData.globalS.removePerFrame == 0) yield return CoroutineEx.waitForEndOfFrame;
            }
            Pool.Free(ref checkFrom);
            Pool.FreeList(ref nearbyEntities);
        }

        private static IEnumerator ProcessContainers(HashSet<BaseEntity> removeList)
        {
            foreach (var entity in removeList)
            {
                var storageContainer = entity as StorageContainer;
                if (storageContainer != null && storageContainer.inventory?.itemList?.Count > 0)
                {
                    if (configData.globalS.noItemContainerDrop) storageContainer.inventory.Clear();
                    else DropItemContainer(storageContainer.inventory, storageContainer.GetDropPosition(), storageContainer.transform.rotation);
                    continue;
                }
                var containerIoEntity = entity as ContainerIOEntity;
                if (containerIoEntity != null && containerIoEntity.inventory?.itemList?.Count > 0)
                {
                    if (configData.globalS.noItemContainerDrop) containerIoEntity.inventory.Clear();
                    else DropItemContainer(containerIoEntity.inventory, containerIoEntity.GetDropPosition(), containerIoEntity.transform.rotation);
                }
            }
            if (configData.globalS.noItemContainerDrop) ItemManager.DoRemoves();
            yield break;
        }

        private static IEnumerator ProcessBuilding(DecayEntity sourceEntity, HashSet<BaseEntity> removeList)
        {
            var building = sourceEntity.GetBuilding();
            if (building != null)
            {
                foreach (var entity in building.decayEntities)
                {
                    if (!removeList.Add(entity)) continue;
                    var storageContainer = entity as StorageContainer;
                    if (storageContainer != null && storageContainer.inventory?.itemList?.Count > 0)
                    {
                        if (configData.globalS.noItemContainerDrop) storageContainer.inventory.Clear();
                        else DropItemContainer(storageContainer.inventory, storageContainer.GetDropPosition(), storageContainer.transform.rotation);
                    }
                }
            }
            else removeList.Add(sourceEntity);
            if (configData.globalS.noItemContainerDrop) ItemManager.DoRemoves();
            yield break;
        }

        private static bool DoRemove(BaseEntity entity, BaseNetworkable.DestroyMode destroyMode)
        {
            if (entity != null && !entity.IsDestroyed)
            {
                entity.Kill(destroyMode);
                return true;
            }
            return false;
        }

        private static void DoNormalRemove(BasePlayer player, BaseEntity entity, bool gibs = true)
        {
            if (entity != null && !entity.IsDestroyed)
            {
                _rt.PrintDebug($"{player.displayName}({player.userID}) has removed {entity.ShortPrefabName}({entity.OwnerID} | {entity.transform.position})", true);
                Interface.CallHook("OnNormalRemovedEntity", player, entity);
                entity.Kill(gibs ? BaseNetworkable.DestroyMode.Gib : BaseNetworkable.DestroyMode.None);
            }
        }

        #endregion RemoveEntity Helpers

        #endregion RemoveEntity

        #region API

        private struct ValueCache<T>
        {
            private T _value;
            private bool _flag;
            private readonly string _key;
            private readonly Dictionary<string, object> _dictionary;

            public ValueCache(string key, Dictionary<string, object> dictionary)
            {
                _flag = false;
                _key = key;
                _value = default(T);
                _dictionary = dictionary;
            }

            public T Value
            {
                get
                {
                    if (!_flag)
                    {
                        _flag = true;
                        object value;
                        if (_dictionary.TryGetValue(_key, out value))
                        {
                            try
                            {
                                _value = (T)value;
                            }
                            catch (Exception ex)
                            {
                                _rt.PrintError($"Incorrect type for {_key}( {typeof(T)})");
                            }
                        }
                    }

                    return _value;
                }
            }
        }

        private struct ItemInfoDictCache
        {
            private bool _name2InfoFlag;
            private bool _name2AmountFlag;
            private Dictionary<string, int> _valueName2Amount;
            private Dictionary<string, ItemInfo> _valueName2Info;
            private ValueCache<Dictionary<string, object>> _dictionary;

            public ItemInfoDictCache(string key, Dictionary<string, object> dictionary)
            {
                _name2InfoFlag = false;
                _name2AmountFlag = false;
                _valueName2Info = null;
                _valueName2Amount = null;
                _dictionary = new ValueCache<Dictionary<string, object>>(key, dictionary);
            }

            public ItemInfo? this[string key]
            {
                get
                {
                    if (ValueName2Info == null)
                    {
                        return null;
                    }

                    ItemInfo itemInfo;
                    if (!ValueName2Info.TryGetValue(key, out itemInfo))
                    {
                        return null;
                    }
                    return itemInfo;
                }
            }

            public Dictionary<string, int> ValueName2Amount
            {
                get
                {
                    if (!_name2AmountFlag)
                    {
                        _name2AmountFlag = true;
                        if (ValueName2Info == null)
                        {
                            return null;
                        }

                        _valueName2Amount = new Dictionary<string, int>();
                        foreach (var entry in ValueName2Info)
                        {
                            _valueName2Amount.Add(entry.Key, entry.Value.Amount.Value);
                        }
                    }

                    return _valueName2Amount;
                }
            }

            private Dictionary<string, ItemInfo> ValueName2Info
            {
                get
                {
                    if (!_name2InfoFlag)
                    {
                        _name2InfoFlag = true;
                        if (_dictionary.Value == null)
                        {
                            return null;
                        }

                        _valueName2Info = new Dictionary<string, ItemInfo>();
                        foreach (var entry in _dictionary.Value)
                        {
                            _valueName2Info.Add(entry.Key, new ItemInfo(entry.Value as Dictionary<string, object>));
                        }
                    }

                    return _valueName2Info;
                }
            }
        }

        private struct ItemInfo
        {
            public ItemInfo(Dictionary<string, object> dictionary)
            {
                Amount = new ValueCache<int>(nameof(Amount), dictionary);
                ImageId = new ValueCache<string>(nameof(ImageId), dictionary);
                DisplayName = new ValueCache<string>(nameof(DisplayName), dictionary);
            }

            public ValueCache<int> Amount { get; }
            public ValueCache<string> ImageId { get; }
            public ValueCache<string> DisplayName { get; }
        }

        private struct RemovableEntityInfo
        {
            public RemovableEntityInfo(Dictionary<string, object> dictionary)
            {
                ImageId = new ValueCache<string>(nameof(ImageId), dictionary);
                DisplayName = new ValueCache<string>(nameof(DisplayName), dictionary);
                Price = new ItemInfoDictCache(nameof(Price), dictionary);
                Refund = new ItemInfoDictCache(nameof(Refund), dictionary);
            }

            public ValueCache<string> ImageId { get; }

            public ValueCache<string> DisplayName { get; }

            public ItemInfoDictCache Price { get; }

            public ItemInfoDictCache Refund { get; }
        }

        private static RemovableEntityInfo? GetRemovableEntityInfo(BaseEntity entity, BasePlayer player)
        {
            if (entity == null) return null;
            var result = Interface.CallHook("OnRemovableEntityInfo", entity, player) as Dictionary<string, object>;
            if (result != null)
            {
                return new RemovableEntityInfo(result);
            }

            return null;
        }

        /*
        private class RemovableEntityInfo
        {
            /// <summary>
            /// Id of the entity image.
            /// </summary>
            public string ImageId { get; set; }

            /// <summary>
            /// Display name of the entity.
            /// </summary>
            public string DisplayName { get; set; }

            /// <summary>
            /// Remove the price of the entity. ItemName to ItemInfo
            /// </summary>
            public Dictionary<string, ItemInfo> Price { get; set; }

            /// <summary>
            /// Remove the refund of the entity. ItemName to ItemInfo
            /// </summary>
            public Dictionary<string, ItemInfo> Refund { get; set; }

            /// <summary>
            /// Called when giving refund items.
            /// It is only called when there is a custom item name in the refund.
            /// </summary>
            /// <param name="entity">Entity</param>
            /// <param name="player">Player</param>
            /// <param name="itemName">Item name</param>
            /// <param name="itemAmount">Item amount</param>
            /// <returns>Please return a non-null value</returns>
            public Func<BaseEntity, BasePlayer, string, int, bool> OnGiveRefund { get; set; }

            /// <summary>
            /// Used to check if the player can pay.
            /// It is only called when there is a custom ItemName in the price
            /// </summary>
            /// <param name="entity">Entity</param>
            /// <param name="player">Player</param>
            /// <param name="itemName">Item name</param>
            /// <param name="itemAmount">Item amount</param>
            /// <param name="check">If true, check if the player can pay. If false, consume the item</param>
            /// <returns>Returns whether payment can be made or whether payment was successful</returns>
            public Func<BaseEntity, BasePlayer, string, int, bool, bool> OnCheckOrPay { get; set; }

            public struct ItemInfo
            {
                /// <summary>
                /// Amount of the item.
                /// </summary>
                public int Amount { get; set; }

                /// <summary>
                /// Id of the item image.
                /// </summary>
                public string ImageId { get; set; }

                /// <summary>
                /// Display name of the item.
                /// </summary>
                public string DisplayName { get; set; }
            }
        }
        */

        private bool IsToolRemover(BasePlayer player) => player?.GetComponent<ToolRemover>() != null;

        private string GetPlayerRemoveType(BasePlayer player) => player?.GetComponent<ToolRemover>()?.removeType.ToString();

        #endregion API

        #region Commands

        private void CmdRemove(BasePlayer player, string command, string[] args)
        {
            if (args == null || args.Length == 0)
            {
                var sourceRemover = player.GetComponent<ToolRemover>();
                if (sourceRemover != null)
                {
                    sourceRemover.DisableTool();
                    return;
                }
            }
            if (_removeOverride && !permission.UserHasPermission(player.UserIDString, PERMISSION_OVERRIDE))
            {
                Print(player, Lang("CurrentlyDisabled", player.UserIDString));
                return;
            }
            RemoveType removeType = RemoveType.Normal;
            int time = configData.removeTypeS[removeType].defaultTime;
            if (args != null && args.Length > 0)
            {
                switch (args[0].ToLower())
                {
                    case "n":
                    case "normal":
                        break;

                    case "a":
                    case "admin":
                        removeType = RemoveType.Admin;
                        time = configData.removeTypeS[removeType].defaultTime;
                        if (!permission.UserHasPermission(player.UserIDString, PERMISSION_ADMIN))
                        {
                            Print(player, Lang("NotAllowed", player.UserIDString, PERMISSION_ADMIN));
                            return;
                        }
                        break;

                    case "all":
                        removeType = RemoveType.All;
                        time = configData.removeTypeS[removeType].defaultTime;
                        if (!permission.UserHasPermission(player.UserIDString, PERMISSION_ALL))
                        {
                            Print(player, Lang("NotAllowed", player.UserIDString, PERMISSION_ALL));
                            return;
                        }
                        break;

                    case "s":
                    case "structure":
                        removeType = RemoveType.Structure;
                        time = configData.removeTypeS[removeType].defaultTime;
                        if (!permission.UserHasPermission(player.UserIDString, PERMISSION_STRUCTURE))
                        {
                            Print(player, Lang("NotAllowed", player.UserIDString, PERMISSION_STRUCTURE));
                            return;
                        }
                        break;

                    case "e":
                    case "external":
                        removeType = RemoveType.External;
                        time = configData.removeTypeS[removeType].defaultTime;
                        if (!permission.UserHasPermission(player.UserIDString, PERMISSION_EXTERNAL))
                        {
                            Print(player, Lang("NotAllowed", player.UserIDString, PERMISSION_EXTERNAL));
                            return;
                        }
                        break;

                    case "h":
                    case "help":
                        StringBuilder stringBuilder = Pool.Get<StringBuilder>();
                        stringBuilder.AppendLine(Lang("Syntax", player.UserIDString, configData.chatS.command, GetRemoveTypeName(RemoveType.Normal)));
                        stringBuilder.AppendLine(Lang("Syntax1", player.UserIDString, configData.chatS.command, GetRemoveTypeName(RemoveType.Admin)));
                        stringBuilder.AppendLine(Lang("Syntax2", player.UserIDString, configData.chatS.command, GetRemoveTypeName(RemoveType.All)));
                        stringBuilder.AppendLine(Lang("Syntax3", player.UserIDString, configData.chatS.command, GetRemoveTypeName(RemoveType.Structure)));
                        stringBuilder.AppendLine(Lang("Syntax4", player.UserIDString, configData.chatS.command, GetRemoveTypeName(RemoveType.External)));
                        Print(player, stringBuilder.ToString());
                        stringBuilder.Clear();
                        Pool.Free(ref stringBuilder);
                        return;

                    default:
                        if (int.TryParse(args[0], out time)) break;
                        Print(player, Lang("SyntaxError", player.UserIDString, configData.chatS.command));
                        return;
                }
            }
            if (args != null && args.Length > 1) int.TryParse(args[1], out time);
            ToggleRemove(player, removeType, time);
        }

        private bool ToggleRemove(BasePlayer player, RemoveType removeType, int time = 0)
        {
            if (removeType == RemoveType.Normal && !permission.UserHasPermission(player.UserIDString, PERMISSION_NORMAL))
            {
                Print(player, Lang("NotAllowed", player.UserIDString, PERMISSION_NORMAL));
                return false;
            }

            int maxRemovable = 0;
            bool pay = false, refund = false;
            var removeTypeS = configData.removeTypeS[removeType];
            float distance = removeTypeS.distance;
            int maxTime = removeTypeS.maxTime;
            bool resetTime = removeTypeS.resetTime;
            float interval = configData.globalS.removeInterval;
            if (removeType == RemoveType.Normal)
            {
                var permissionS = GetPermissionS(player);
                var cooldown = permissionS.cooldown;
                if (cooldown > 0 && !(configData.globalS.cooldownExclude && player.IsAdmin))
                {
                    float lastUse;
                    if (_cooldownTimes.TryGetValue(player.userID, out lastUse))
                    {
                        var timeLeft = cooldown - (Time.realtimeSinceStartup - lastUse);
                        if (timeLeft > 0)
                        {
                            Print(player, Lang("Cooldown", player.UserIDString, Math.Ceiling(timeLeft)));
                            return false;
                        }
                    }
                }
                if (_removeMode == RemoveMode.MeleeHit && configData.removerModeS.meleeHitRequires)
                {
                    if (!IsMeleeTool(player))
                    {
                        Print(player, Lang("MeleeToolNotHeld", player.UserIDString));
                        return false;
                    }
                }
                if (_removeMode == RemoveMode.SpecificTool && configData.removerModeS.specificToolRequires)
                {
                    if (!IsSpecificTool(player))
                    {
                        Print(player, Lang("SpecificToolNotHeld", player.UserIDString));
                        return false;
                    }
                }

                interval = permissionS.removeInterval;
                resetTime = permissionS.resetTime;
                maxTime = permissionS.maxTime;
                maxRemovable = permissionS.maxRemovable;
                if (configData.globalS.maxRemovableExclude && player.IsAdmin) maxRemovable = 0;
                distance = permissionS.distance;
                pay = permissionS.pay;
                refund = permissionS.refund;
            }
            if (time == 0) time = configData.removeTypeS[removeType].defaultTime;
            if (time > maxTime) time = maxTime;
            var toolRemover = player.GetOrAddComponent<ToolRemover>();
            if (toolRemover.removeType == RemoveType.Normal)
            {
                if (!configData.globalS.startCooldownOnRemoved)
                {
                    _cooldownTimes[player.userID] = Time.realtimeSinceStartup;
                }
            }
            toolRemover.Init(removeType, time, maxRemovable, distance, interval, pay, refund, resetTime, true);
            Print(player, Lang("ToolEnabled", player.UserIDString, time, maxRemovable == 0 ? Lang("Unlimit", player.UserIDString) : maxRemovable.ToString(), GetRemoveTypeName(removeType)));
            return true;
        }

        [ConsoleCommand("remove.toggle")]
        private void CCmdRemoveToggle(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                Print(arg, "Syntax error!!! Please type the commands in the F1 console");
                return;
            }
            CmdRemove(player, null, arg.Args);
        }

        [ConsoleCommand("remove.target")]
        private void CCmdRemoveTarget(ConsoleSystem.Arg arg)
        {
            if (arg.Args == null || arg.Args.Length <= 1)
            {
                StringBuilder stringBuilder = Pool.Get<StringBuilder>();
                stringBuilder.AppendLine("Syntax error of target command");
                stringBuilder.AppendLine("remove.target <disable | d> <player (name or id)> - Disable remover tool for player");
                stringBuilder.AppendLine("remove.target <normal | n> <player (name or id)> [time (seconds)] [max removable objects (integer)] - Enable remover tool for player (Normal)");
                stringBuilder.AppendLine("remove.target <admin | a> <player (name or id)> [time (seconds)] - Enable remover tool for player (Admin)");
                stringBuilder.AppendLine("remove.target <all> <player (name or id)> [time (seconds)] - Enable remover tool for player (All)");
                stringBuilder.AppendLine("remove.target <structure | s> <player (name or id)> [time (seconds)] - Enable remover tool for player (Structure)");
                stringBuilder.AppendLine("remove.target <external | e> <player (name or id)> [time (seconds)] - Enable remover tool for player (External)");
                Print(arg, stringBuilder.ToString());
                stringBuilder.Clear();
                Pool.Free(ref stringBuilder);
                return;
            }
            var player = arg.Player();
            if (player != null && !permission.UserHasPermission(player.UserIDString, PERMISSION_TARGET))
            {
                Print(arg, Lang("NotAllowed", player.UserIDString, PERMISSION_TARGET));
                return;
            }
            var target = RustCore.FindPlayer(arg.Args[1]);
            if (target == null || !target.IsConnected)
            {
                Print(arg, target == null ? $"'{arg.Args[0]}' cannot be found." : $"'{target}' is offline.");
                return;
            }
            RemoveType removeType = RemoveType.Normal;
            switch (arg.Args[0].ToLower())
            {
                case "n":
                case "normal":
                    break;

                case "a":
                case "admin":
                    removeType = RemoveType.Admin;
                    break;

                case "all":
                    removeType = RemoveType.All;
                    break;

                case "s":
                case "structure":
                    removeType = RemoveType.Structure;
                    break;

                case "e":
                case "external":
                    removeType = RemoveType.External;
                    break;

                case "d":
                case "disable":
                    var toolRemover = target.GetComponent<ToolRemover>();
                    if (toolRemover != null)
                    {
                        toolRemover.DisableTool();
                        Print(arg, $"{target}'s remover tool is disabled");
                    }
                    else Print(arg, $"{target} did not enable the remover tool");
                    return;

                default:
                    StringBuilder stringBuilder = Pool.Get<StringBuilder>();
                    stringBuilder.AppendLine("Syntax error of target command");
                    stringBuilder.AppendLine("remove.target <disable | d> <player (name or id)> - Disable remover tool for player");
                    stringBuilder.AppendLine("remove.target <normal | n> <player (name or id)> [time (seconds)] [max removable objects (integer)] - Enable remover tool for player (Normal)");
                    stringBuilder.AppendLine("remove.target <admin | a> <player (name or id)> [time (seconds)] - Enable remover tool for player (Admin)");
                    stringBuilder.AppendLine("remove.target <all> <player (name or id)> [time (seconds)] - Enable remover tool for player (All)");
                    stringBuilder.AppendLine("remove.target <structure | s> <player (name or id)> [time (seconds)] - Enable remover tool for player (Structure)");
                    stringBuilder.AppendLine("remove.target <external | e> <player (name or id)> [time (seconds)] - Enable remover tool for player (External)");
                    Print(arg, stringBuilder.ToString());
                    stringBuilder.Clear();
                    Pool.Free(ref stringBuilder);
                    return;
            }
            int maxRemovable = 0;
            int time = configData.removeTypeS[removeType].defaultTime;
            if (arg.Args.Length > 2) int.TryParse(arg.Args[2], out time);
            if (arg.Args.Length > 3 && removeType == RemoveType.Normal) int.TryParse(arg.Args[3], out maxRemovable);
            var permissionS = configData.permS[PERMISSION_NORMAL];
            var targetRemover = target.GetOrAddComponent<ToolRemover>();
            targetRemover.Init(removeType, time, maxRemovable, configData.removeTypeS[removeType].distance, permissionS.removeInterval, permissionS.pay, permissionS.refund, permissionS.resetTime, false);
            Print(arg, Lang("TargetEnabled", player?.UserIDString, target, time, maxRemovable, GetRemoveTypeName(removeType)));
        }

        [ConsoleCommand("remove.building")]
        private void CCmdConstruction(ConsoleSystem.Arg arg)
        {
            if (arg.Args == null || arg.Args.Length <= 1 || !arg.IsAdmin)
            {
                Print(arg, $"Syntax error, Please type 'remove.building <price / refund / priceP / refundP> <percentage>', e.g.'remove.building price 60'");
                return;
            }
            float value;
            switch (arg.Args[0].ToLower())
            {
                case "price":
                    if (!float.TryParse(arg.Args[1], out value)) value = 50f;
                    foreach (var construction in _constructions)
                    {
                        BuildingBlocksSettings buildingBlocksSettings;
                        if (configData.removeS.buildingBlockS.TryGetValue(construction.info.name.english, out buildingBlocksSettings))
                        {
                            foreach (var entry in buildingBlocksSettings.buildingGradeS)
                            {
                                var grade = construction.grades[(int)entry.Key];
                                entry.Value.price = grade.costToBuild.ToDictionary(x => x.itemDef.shortname, y => value <= 0 ? 0 : Mathf.RoundToInt(y.amount * value / 100));
                            }
                        }
                    }
                    Print(arg, $"Successfully modified all building prices to {value}% of the initial cost.");
                    SaveConfig();
                    return;

                case "refund":
                    if (!float.TryParse(arg.Args[1], out value)) value = 40f;
                    foreach (var construction in _constructions)
                    {
                        BuildingBlocksSettings buildingBlocksSettings;
                        if (configData.removeS.buildingBlockS.TryGetValue(construction.info.name.english, out buildingBlocksSettings))
                        {
                            foreach (var entry in buildingBlocksSettings.buildingGradeS)
                            {
                                var grade = construction.grades[(int)entry.Key];
                                entry.Value.refund = grade.costToBuild.ToDictionary(x => x.itemDef.shortname, y => value <= 0 ? 0 : Mathf.RoundToInt(y.amount * value / 100));
                            }
                        }
                    }
                    Print(arg, $"Successfully modified all building refunds to {value}% of the initial cost.");
                    SaveConfig();
                    return;

                case "pricep":
                    if (!float.TryParse(arg.Args[1], out value)) value = 40f;
                    foreach (var buildingBlockS in configData.removeS.buildingBlockS.Values)
                    {
                        foreach (var data in buildingBlockS.buildingGradeS.Values)
                            data.price = value <= 0 ? 0 : value;
                    }

                    Print(arg, $"Successfully modified all building prices to {value}% of the initial cost.");
                    SaveConfig();
                    return;

                case "refundp":
                    if (!float.TryParse(arg.Args[1], out value)) value = 50f;
                    foreach (var buildingBlockS in configData.removeS.buildingBlockS.Values)
                    {
                        foreach (var data in buildingBlockS.buildingGradeS.Values)
                            data.refund = value <= 0 ? 0 : value;
                    }

                    Print(arg, $"Successfully modified all building refunds to {value}% of the initial cost.");
                    SaveConfig();
                    return;

                default:
                    Print(arg, $"Syntax error, Please type 'remove.building <price / refund / priceP / refundP> <percentage>', e.g.'remove.building price 60'");
                    return;
            }
        }

        [ConsoleCommand("remove.allow")]
        private void CCmdRemoveAllow(ConsoleSystem.Arg arg)
        {
            if (arg.Args == null || arg.Args.Length == 0)
            {
                Print(arg, "Syntax error, Please type 'remove.allow <true | false>'");
                return;
            }
            var player = arg.Player();
            if (player != null && !permission.UserHasPermission(player.UserIDString, PERMISSION_OVERRIDE))
            {
                Print(arg, Lang("NotAllowed", player.UserIDString, PERMISSION_OVERRIDE));
                return;
            }
            switch (arg.Args[0].ToLower())
            {
                case "true":
                case "1":
                    _removeOverride = false;
                    Print(arg, "Remove is now allowed depending on your settings.");
                    return;

                case "false":
                case "0":
                    _removeOverride = true;
                    Print(arg, "Remove is now restricted for all players (exept admins)");
                    foreach (var p in BasePlayer.activePlayerList)
                    {
                        var toolRemover = p.GetComponent<ToolRemover>();
                        if (toolRemover == null) continue;
                        if (toolRemover.removeType == RemoveType.Normal && toolRemover.canOverride)
                        {
                            Print(toolRemover.player, "The remover tool has been disabled by the admin");
                            toolRemover.DisableTool(false);
                        }
                    }
                    return;

                default:
                    Print(arg, "This is not a valid argument");
                    return;
            }
        }

        [ConsoleCommand("remove.playerentity")]
        private void CCmdRemoveEntity(ConsoleSystem.Arg arg)
        {
            if (arg.Args == null || arg.Args.Length <= 1 || !arg.IsAdmin)
            {
                StringBuilder stringBuilder = Pool.Get<StringBuilder>();
                stringBuilder.AppendLine("Syntax error of remove.playerentity command");
                stringBuilder.AppendLine("remove.playerentity <all | a> <player id> - Remove all entities of the player");
                stringBuilder.AppendLine("remove.playerentity <building | b> <player id> - Remove all buildings of the player");
                stringBuilder.AppendLine("remove.playerentity <cupboard | c> <player id> - Remove buildings of the player owned cupboard");
                Print(arg, stringBuilder.ToString());
                stringBuilder.Clear();
                Pool.Free(ref stringBuilder);
                return;
            }
            if (_removePlayerEntityCoroutine != null)
            {
                Print(arg, "There is already a RemovePlayerEntity running, please wait.");
                return;
            }
            ulong targetID;
            if (!ulong.TryParse(arg.Args[1], out targetID) || !targetID.IsSteamId())
            {
                Print(arg, "Please enter the player's steamID.");
                return;
            }
            PlayerEntityRemoveType playerEntityRemoveType;
            switch (arg.Args[0].ToLower())
            {
                case "a":
                case "all":
                    playerEntityRemoveType = PlayerEntityRemoveType.All;
                    break;

                case "b":
                case "building":
                    playerEntityRemoveType = PlayerEntityRemoveType.Building;
                    break;

                case "c":
                case "cupboard":
                    playerEntityRemoveType = PlayerEntityRemoveType.Cupboard;
                    break;

                default:
                    Print(arg, "This is not a valid argument");
                    return;
            }
            _removePlayerEntityCoroutine = ServerMgr.Instance.StartCoroutine(RemovePlayerEntity(arg, targetID, playerEntityRemoveType));
            Print(arg, "Start running RemovePlayerEntity, please wait.");
        }

        #endregion Commands

        #region Debug

        private StringBuilder debugStringBuilder;

        private void PrintDebug(string message, bool warning = false)
        {
            if (configData.globalS.debugEnabled)
            {
                if (warning) PrintWarning(message);
                else Puts(message);
            }
            if (configData.globalS.logToFile)
            {
                debugStringBuilder.AppendLine($"[{DateTime.Now.ToString(CultureInfo.InstalledUICulture)}] | {message}");
            }
        }

        private void SaveDebug()
        {
            if (!configData.globalS.logToFile) return;
            var debugText = debugStringBuilder.ToString().Trim();
            debugStringBuilder.Clear();
            if (!string.IsNullOrEmpty(debugText))
            {
                LogToFile("debug", debugText, this);
            }
        }

        #endregion Debug

        #region RustTranslationAPI

        private string GetItemTranslationByShortName(string language, string itemShortName) => (string)RustTranslationAPI.Call("GetItemTranslationByShortName", language, itemShortName);

        private string GetConstructionTranslation(string language, string prefabName) => (string)RustTranslationAPI.Call("GetConstructionTranslation", language, prefabName);

        private string GetDeployableTranslation(string language, string deployable) => (string)RustTranslationAPI.Call("GetDeployableTranslation", language, deployable);

        private string GetItemDisplayName(string language, string itemShortName)
        {
            if (RustTranslationAPI != null)
            {
                return GetItemTranslationByShortName(language, itemShortName);
            }
            return null;
        }

        private string GetConstructionDisplayName(BasePlayer player, string shortPrefabName, string displayName)
        {
            if (RustTranslationAPI != null)
            {
                var displayName1 = GetConstructionTranslation(lang.GetLanguage(player.UserIDString), shortPrefabName);
                if (!string.IsNullOrEmpty(displayName1))
                {
                    return displayName1;
                }
            }
            return displayName;
        }

        private string GetDeployableDisplayName(BasePlayer player, string deployable, string displayName)
        {
            if (RustTranslationAPI != null)
            {
                var displayName1 = GetDeployableTranslation(lang.GetLanguage(player.UserIDString), deployable);
                if (!string.IsNullOrEmpty(displayName1))
                {
                    return displayName1;
                }
            }
            return displayName;
        }

        #endregion RustTranslationAPI

        #region ConfigurationFile

        private void UpdateConfig()
        {
            var buildingGrades = new[] { BuildingGrade.Enum.Twigs, BuildingGrade.Enum.Wood, BuildingGrade.Enum.Stone, BuildingGrade.Enum.Metal, BuildingGrade.Enum.TopTier };
            foreach (var value in buildingGrades)
            {
                if (!configData.removeS.validConstruction.ContainsKey(value))
                {
                    configData.removeS.validConstruction.Add(value, true);
                }
            }

            var newBuildingBlocksS = new Dictionary<string, BuildingBlocksSettings>();
            foreach (var construction in _constructions)
            {
                BuildingBlocksSettings buildingBlocksSettings;
                if (!configData.removeS.buildingBlockS.TryGetValue(construction.info.name.english, out buildingBlocksSettings))
                {
                    var buildingGrade = new Dictionary<BuildingGrade.Enum, BuildingGradeSettings>();
                    foreach (var value in buildingGrades)
                    {
                        var grade = construction.grades[(int)value];
                        buildingGrade.Add(value, new BuildingGradeSettings { refund = grade.costToBuild.ToDictionary(x => x.itemDef.shortname, y => Mathf.RoundToInt(y.amount * 0.4f)), price = grade.costToBuild.ToDictionary(x => x.itemDef.shortname, y => Mathf.RoundToInt(y.amount * 0.6f)) });
                    }
                    buildingBlocksSettings = new BuildingBlocksSettings { displayName = construction.info.name.english, buildingGradeS = buildingGrade };
                }
                newBuildingBlocksS.Add(construction.info.name.english, buildingBlocksSettings);
            }
            configData.removeS.buildingBlockS = newBuildingBlocksS;

            foreach (var entry in _shortPrefabNameToDeployable)
            {
                EntitySettings entitySettings;
                if (!configData.removeS.entityS.TryGetValue(entry.Key, out entitySettings))
                {
                    var itemDefinition = ItemManager.FindItemDefinition(entry.Value);
                    entitySettings = new EntitySettings
                    {
                        enabled = configData.globalS.defaultEntityS.removeAllowed && itemDefinition.category != ItemCategory.Food,
                        displayName = itemDefinition.displayName.english,
                        refund = new Dictionary<string, int> { [entry.Value] = 1 },
                        price = new Dictionary<string, int>()
                    };
                    configData.removeS.entityS.Add(entry.Key, entitySettings);
                }
            }
            SaveConfig();
        }

        private static ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Settings")]
            public readonly GlobalSettings globalS = new GlobalSettings();

            [JsonProperty(PropertyName = "Container Settings")]
            public readonly ContainerSettings containerS = new ContainerSettings();

            [JsonProperty(PropertyName = "Remove Damaged Entities")]
            public readonly DamagedEntitySettings damagedEntityS = new DamagedEntitySettings();

            [JsonProperty(PropertyName = "Chat Settings")]
            public readonly ChatSettings chatS = new ChatSettings();

            [JsonProperty(PropertyName = "Permission Settings (Just for normal type)")]
            public readonly Dictionary<string, PermissionSettings> permS = new Dictionary<string, PermissionSettings>
            {
                [PERMISSION_NORMAL] = new PermissionSettings { priority = 0, distance = 3, cooldown = 60, maxTime = 300, maxRemovable = 50, removeInterval = 0.8f, pay = true, refund = true, resetTime = false }
            };

            [JsonProperty(PropertyName = "Remove Type Settings")]
            public readonly Dictionary<RemoveType, RemoveTypeSettings> removeTypeS = new Dictionary<RemoveType, RemoveTypeSettings>
            {
                [RemoveType.Normal] = new RemoveTypeSettings { displayName = RemoveType.Normal.ToString(), distance = 3, gibs = true, defaultTime = 60, maxTime = 300, resetTime = false },
                [RemoveType.Structure] = new RemoveTypeSettings { displayName = RemoveType.Structure.ToString(), distance = 100, gibs = false, defaultTime = 300, maxTime = 600, resetTime = true },
                [RemoveType.All] = new RemoveTypeSettings { displayName = RemoveType.All.ToString(), distance = 50, gibs = false, defaultTime = 300, maxTime = 600, resetTime = true },
                [RemoveType.Admin] = new RemoveTypeSettings { displayName = RemoveType.Admin.ToString(), distance = 20, gibs = true, defaultTime = 300, maxTime = 600, resetTime = true },
                [RemoveType.External] = new RemoveTypeSettings { displayName = RemoveType.External.ToString(), distance = 20, gibs = true, defaultTime = 300, maxTime = 600, resetTime = true }
            };

            [JsonProperty(PropertyName = "Remove Mode Settings (Only one model works)")]
            public readonly RemoverModeSettings removerModeS = new RemoverModeSettings();

            [JsonProperty(PropertyName = "Raid Blocker Settings")]
            public readonly RaidBlockerSettings raidS = new RaidBlockerSettings();

            [JsonProperty(PropertyName = "Image Urls (Used to UI image)")]
            public readonly Dictionary<string, string> imageUrls = new Dictionary<string, string>
            {
                [ECONOMICS_KEY] = "https://i.imgur.com/znPwdcv.png",
                [SERVER_REWARDS_KEY] = "https://i.imgur.com/04rJsV3.png"
            };

            [JsonProperty(PropertyName = "GUI")]
            public readonly UiSettings uiS = new UiSettings();

            [JsonProperty(PropertyName = "Remove Info (Refund & Price)")]
            public readonly RemoveSettings removeS = new RemoveSettings();

            [JsonProperty(PropertyName = "Display Names Of Other Things")]
            public readonly Dictionary<string, string> displayNames = new Dictionary<string, string>();

            [JsonProperty(PropertyName = "Version")]
            public VersionNumber version;
        }

        public class GlobalSettings
        {
            [JsonProperty(PropertyName = "Enable Debug Mode")]
            public bool debugEnabled;

            [JsonProperty(PropertyName = "Log Debug To File")]
            public bool logToFile;

            [JsonProperty(PropertyName = "Use Teams")]
            public bool useTeams = false;

            [JsonProperty(PropertyName = "Use Clans")]
            public bool useClans = true;

            [JsonProperty(PropertyName = "Use Friends")]
            public bool useFriends = true;

            [JsonProperty(PropertyName = "Use Entity Owners")]
            public bool useEntityOwners = true;

            [JsonProperty(PropertyName = "Use Building Locks")]
            public bool useBuildingLocks = false;

            [JsonProperty(PropertyName = "Use Tool Cupboards (Strongly unrecommended)")]
            public bool useToolCupboards = false;

            [JsonProperty(PropertyName = "Use Building Owners (You will need BuildingOwners plugin)")]
            public bool useBuildingOwners = false;

            //[JsonProperty(PropertyName = "Exclude Twigs (Used for \"Use Tool Cupboards\" and \"Use Entity Owners\")")]
            //public bool excludeTwigs;

            [JsonProperty(PropertyName = "Remove Button")]
            public string removeButton = BUTTON.FIRE_PRIMARY.ToString();

            [JsonProperty(PropertyName = "Remove Interval (Min = 0.2)")]
            public float removeInterval = 0.5f;

            [JsonProperty(PropertyName = "Only start cooldown when an entity is removed")]
            public bool startCooldownOnRemoved;

            [JsonProperty(PropertyName = "RemoveType - All/Structure - Remove per frame")]
            public int removePerFrame = 15;

            [JsonProperty(PropertyName = "RemoveType - All/Structure - No item container dropped")]
            public bool noItemContainerDrop = true;

            [JsonProperty(PropertyName = "RemoveType - Normal - Max Removable Objects - Exclude admins")]
            public bool maxRemovableExclude = true;

            [JsonProperty(PropertyName = "RemoveType - Normal - Cooldown - Exclude admins")]
            public bool cooldownExclude = true;

            [JsonProperty(PropertyName = "RemoveType - Normal - Check stash under the foundation")]
            public bool checkStash = false;

            [JsonProperty(PropertyName = "RemoveType - Normal - Entity Spawned Time Limit - Enabled")]
            public bool entityTimeLimit = false;

            [JsonProperty(PropertyName = "RemoveType - Normal - Entity Spawned Time Limit - Cannot be removed when entity spawned time more than it")]
            public float limitTime = 300f;

            [JsonProperty(PropertyName = "Default Entity Settings (When automatically adding new entities to 'Other Entity Settings')")]
            public DefaultEntitySettings defaultEntityS = new DefaultEntitySettings();

            public class DefaultEntitySettings
            {
                [JsonProperty(PropertyName = "Default Remove Allowed")]
                public bool removeAllowed = true;
            }
        }

        public class ContainerSettings
        {
            [JsonProperty(PropertyName = "Storage Container - Enable remove of not empty storages")]
            public bool removeNotEmptyStorage = true;

            [JsonProperty(PropertyName = "Storage Container - Drop items from container")]
            public bool dropItemsStorage = false;

            [JsonProperty(PropertyName = "Storage Container - Drop a item container from container")]
            public bool dropContainerStorage = true;

            [JsonProperty(PropertyName = "IOEntity Container - Enable remove of not empty storages")]
            public bool removeNotEmptyIoEntity = true;

            [JsonProperty(PropertyName = "IOEntity Container - Drop items from container")]
            public bool dropItemsIoEntity = false;

            [JsonProperty(PropertyName = "IOEntity Container - Drop a item container from container")]
            public bool dropContainerIoEntity = true;
        }

        public class DamagedEntitySettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool enabled = false;

            [JsonProperty(PropertyName = "Exclude Quarries")]
            public bool excludeQuarries = true;

            [JsonProperty(PropertyName = "Exclude Building Blocks")]
            public bool excludeBuildingBlocks = true;

            [JsonProperty(PropertyName = "Percentage (Can be removed when (health / max health * 100) is not less than it)")]
            public float percentage = 90f;
        }

        public class ChatSettings
        {
            [JsonProperty(PropertyName = "Chat Command")]
            public string command = "remove";

            [JsonProperty(PropertyName = "Chat Prefix")]
            public string prefix = "<color=#00FFFF>[RemoverTool]</color>: ";

            [JsonProperty(PropertyName = "Chat SteamID Icon")]
            public ulong steamIDIcon = 0;
        }

        public class PermissionSettings
        {
            [JsonProperty(PropertyName = "Priority")]
            public int priority;

            [JsonProperty(PropertyName = "Distance")]
            public float distance;

            [JsonProperty(PropertyName = "Cooldown")]
            public float cooldown;

            [JsonProperty(PropertyName = "Max Time")]
            public int maxTime;

            [JsonProperty(PropertyName = "Remove Interval (Min = 0.2)")]
            public float removeInterval;

            [JsonProperty(PropertyName = "Max Removable Objects (0 = Unlimit)")]
            public int maxRemovable;

            [JsonProperty(PropertyName = "Pay")]
            public bool pay;

            [JsonProperty(PropertyName = "Refund")]
            public bool refund;

            [JsonProperty(PropertyName = "Reset the time after removing an entity")]
            public bool resetTime;
        }

        public class RemoveTypeSettings
        {
            [JsonProperty(PropertyName = "Display Name")]
            public string displayName;

            [JsonProperty(PropertyName = "Distance")]
            public float distance;

            [JsonProperty(PropertyName = "Default Time")]
            public int defaultTime;

            [JsonProperty(PropertyName = "Max Time")]
            public int maxTime;

            [JsonProperty(PropertyName = "Gibs")]
            public bool gibs;

            [JsonProperty(PropertyName = "Reset the time after removing an entity")]
            public bool resetTime;
        }

        public class RemoverModeSettings
        {
            [JsonProperty(PropertyName = "No Held Item Mode")]
            public bool noHeldMode = true;

            [JsonProperty(PropertyName = "No Held Item Mode - Disable remover tool when you have any item in hand")]
            public bool noHeldDisableInHand = true;

            [JsonProperty(PropertyName = "Melee Tool Hit Mode")]
            public bool meleeHitMode = false;

            [JsonProperty(PropertyName = "Melee Tool Hit Mode - Item shortname")]
            public string meleeHitItemShortname = "hammer";

            [JsonProperty(PropertyName = "Melee Tool Hit Mode - Item skin (-1 = All skins)")]
            public long meleeHitModeSkin = -1;

            [JsonProperty(PropertyName = "Melee Tool Hit Mode - Auto enable remover tool when you hold a melee tool")]
            public bool meleeHitEnableInHand = false;

            [JsonProperty(PropertyName = "Melee Tool Hit Mode - Requires a melee tool in your hand when remover tool is enabled")]
            public bool meleeHitRequires = false;

            [JsonProperty(PropertyName = "Melee Tool Hit Mode - Disable remover tool when you are not holding a melee tool")]
            public bool meleeHitDisableInHand = false;

            [JsonProperty(PropertyName = "Specific Tool Mode")]
            public bool specificToolMode = false;

            [JsonProperty(PropertyName = "Specific Tool Mode - Item shortname")]
            public string specificToolShortname = "hammer";

            [JsonProperty(PropertyName = "Specific Tool Mode - Item skin (-1 = All skins)")]
            public long specificToolSkin = -1;

            [JsonProperty(PropertyName = "Specific Tool Mode - Auto enable remover tool when you hold a specific tool")]
            public bool specificToolEnableInHand = false;

            [JsonProperty(PropertyName = "Specific Tool Mode - Requires a specific tool in your hand when remover tool is enabled")]
            public bool specificToolRequires = false;

            [JsonProperty(PropertyName = "Specific Tool Mode - Disable remover tool when you are not holding a specific tool")]
            public bool specificToolDisableInHand = false;
        }

        public class RaidBlockerSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool enabled = false;

            [JsonProperty(PropertyName = "Block Time")]
            public float blockTime = 300;

            [JsonProperty(PropertyName = "By Buildings")]
            public bool blockBuildingID = true;

            [JsonProperty(PropertyName = "By Surrounding Players")]
            public bool blockPlayers = true;

            [JsonProperty(PropertyName = "By Surrounding Players - Radius")]
            public float blockRadius = 120;
        }

        public class UiSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool enabled = true;

            [JsonProperty(PropertyName = "Main Box - Min Anchor (in Rust Window)")]
            public string removerToolAnchorMin = "0 1";

            [JsonProperty(PropertyName = "Main Box - Max Anchor (in Rust Window)")]
            public string removerToolAnchorMax = "0 1";

            [JsonProperty(PropertyName = "Main Box - Min Offset (in Rust Window)")]
            public string removerToolOffsetMin = "30 -330";

            [JsonProperty(PropertyName = "Main Box - Max Offset (in Rust Window)")]
            public string removerToolOffsetMax = "470 -40";

            [JsonProperty(PropertyName = "Main Box - Background Color")]
            public string removerToolBackgroundColor = "0 0 0 0";

            [JsonProperty(PropertyName = "Remove Title - Box - Min Anchor (in Main Box)")]
            public string removeAnchorMin = "0 0.84";

            [JsonProperty(PropertyName = "Remove Title - Box - Max Anchor (in Main Box)")]
            public string removeAnchorMax = "0.996 1";

            [JsonProperty(PropertyName = "Remove Title - Box - Background Color")]
            public string removeBackgroundColor = "0.31 0.88 0.71 1";

            [JsonProperty(PropertyName = "Remove Title - Text - Min Anchor (in Main Box)")]
            public string removeTextAnchorMin = "0.05 0.84";

            [JsonProperty(PropertyName = "Remove Title - Text - Max Anchor (in Main Box)")]
            public string removeTextAnchorMax = "0.6 1";

            [JsonProperty(PropertyName = "Remove Title - Text - Text Color")]
            public string removeTextColor = "1 0.1 0.1 1";

            [JsonProperty(PropertyName = "Remove Title - Text - Text Size")]
            public int removeTextSize = 18;

            [JsonProperty(PropertyName = "Timeleft - Box - Min Anchor (in Main Box)")]
            public string timeLeftAnchorMin = "0.6 0.84";

            [JsonProperty(PropertyName = "Timeleft - Box - Max Anchor (in Main Box)")]
            public string timeLeftAnchorMax = "1 1";

            [JsonProperty(PropertyName = "Timeleft - Box - Background Color")]
            public string timeLeftBackgroundColor = "0 0 0 0";

            [JsonProperty(PropertyName = "Timeleft - Text - Min Anchor (in Timeleft Box)")]
            public string timeLeftTextAnchorMin = "0 0";

            [JsonProperty(PropertyName = "Timeleft - Text - Max Anchor (in Timeleft Box)")]
            public string timeLeftTextAnchorMax = "0.9 1";

            [JsonProperty(PropertyName = "Timeleft - Text - Text Color")]
            public string timeLeftTextColor = "0 0 0 0.9";

            [JsonProperty(PropertyName = "Timeleft - Text - Text Size")]
            public int timeLeftTextSize = 15;

            [JsonProperty(PropertyName = "Entity - Box - Min Anchor (in Main Box)")]
            public string entityAnchorMin = "0 0.68";

            [JsonProperty(PropertyName = "Entity - Box - Max Anchor (in Main Box)")]
            public string entityAnchorMax = "1 0.84";

            [JsonProperty(PropertyName = "Entity - Box - Background Color")]
            public string entityBackgroundColor = "0.82 0.58 0.30 1";

            [JsonProperty(PropertyName = "Entity - Text - Min Anchor (in Entity Box)")]
            public string entityTextAnchorMin = "0.05 0";

            [JsonProperty(PropertyName = "Entity - Text - Max Anchor (in Entity Box)")]
            public string entityTextAnchorMax = "1 1";

            [JsonProperty(PropertyName = "Entity - Text - Text Color")]
            public string entityTextColor = "1 1 1 1";

            [JsonProperty(PropertyName = "Entity - Text - Text Size")]
            public int entityTextSize = 16;

            [JsonProperty(PropertyName = "Entity - Image - Enabled")]
            public bool entityImageEnabled = true;

            [JsonProperty(PropertyName = "Entity - Image - Min Anchor (in Entity Box)")]
            public string entityImageAnchorMin = "0.795 0.01";

            [JsonProperty(PropertyName = "Entity - Image - Max Anchor (in Entity Box)")]
            public string entityImageAnchorMax = "0.9 0.99";

            [JsonProperty(PropertyName = "Authorization Check Enabled")]
            public bool authorizationEnabled = true;

            [JsonProperty(PropertyName = "Authorization Check - Box - Min Anchor (in Main Box)")]
            public string authorizationsAnchorMin = "0 0.6";

            [JsonProperty(PropertyName = "Authorization Check - Box - Max Anchor (in Main Box)")]
            public string authorizationsAnchorMax = "1 0.68";

            [JsonProperty(PropertyName = "Authorization Check - Box - Allowed Background Color")]
            public string allowedBackgroundColor = "0.22 0.78 0.27 1";

            [JsonProperty(PropertyName = "Authorization Check - Box - Refused Background Color")]
            public string refusedBackgroundColor = "0.78 0.22 0.27 1";

            [JsonProperty(PropertyName = "Authorization Check - Text - Min Anchor (in Authorization Check Box)")]
            public string authorizationsTextAnchorMin = "0.05 0";

            [JsonProperty(PropertyName = "Authorization Check - Text - Max Anchor (in Authorization Check Box)")]
            public string authorizationsTextAnchorMax = "1 1";

            [JsonProperty(PropertyName = "Authorization Check - Text - Text Color")]
            public string authorizationsTextColor = "1 1 1 0.9";

            [JsonProperty(PropertyName = "Authorization Check Box - Text - Text Size")]
            public int authorizationsTextSize = 14;

            [JsonProperty(PropertyName = "Price & Refund - Image Enabled")]
            public bool imageEnabled = true;

            [JsonProperty(PropertyName = "Price & Refund - Image Scale")]
            public float imageScale = 0.18f;

            [JsonProperty(PropertyName = "Price & Refund - Distance of image from right border")]
            public float rightDistance = 0.05f;

            [JsonProperty(PropertyName = "Price Enabled")]
            public bool priceEnabled = true;

            [JsonProperty(PropertyName = "Price - Box - Min Anchor (in Main Box)")]
            public string priceAnchorMin = "0 0.3";

            [JsonProperty(PropertyName = "Price - Box - Max Anchor (in Main Box)")]
            public string priceAnchorMax = "1 0.6";

            [JsonProperty(PropertyName = "Price - Box - Background Color")]
            public string priceBackgroundColor = "0 0 0 0.8";

            [JsonProperty(PropertyName = "Price - Text - Min Anchor (in Price Box)")]
            public string priceTextAnchorMin = "0.05 0";

            [JsonProperty(PropertyName = "Price - Text - Max Anchor (in Price Box)")]
            public string priceTextAnchorMax = "0.25 1";

            [JsonProperty(PropertyName = "Price - Text - Text Color")]
            public string priceTextColor = "1 1 1 0.9";

            [JsonProperty(PropertyName = "Price - Text - Text Size")]
            public int priceTextSize = 18;

            [JsonProperty(PropertyName = "Price - Text2 - Min Anchor (in Price Box)")]
            public string price2TextAnchorMin = "0.3 0";

            [JsonProperty(PropertyName = "Price - Text2 - Max Anchor (in Price Box)")]
            public string price2TextAnchorMax = "1 1";

            [JsonProperty(PropertyName = "Price - Text2 - Text Color")]
            public string price2TextColor = "1 1 1 0.9";

            [JsonProperty(PropertyName = "Price - Text2 - Text Size")]
            public int price2TextSize = 16;

            [JsonProperty(PropertyName = "Refund Enabled")]
            public bool refundEnabled = true;

            [JsonProperty(PropertyName = "Refund - Box - Min Anchor (in Main Box)")]
            public string refundAnchorMin = "0 0";

            [JsonProperty(PropertyName = "Refund - Box - Max Anchor (in Main Box)")]
            public string refundAnchorMax = "1 0.3";

            [JsonProperty(PropertyName = "Refund - Box - Background Color")]
            public string refundBackgroundColor = "0 0 0 0.8";

            [JsonProperty(PropertyName = "Refund - Text - Min Anchor (in Refund Box)")]
            public string refundTextAnchorMin = "0.05 0";

            [JsonProperty(PropertyName = "Refund - Text - Max Anchor (in Refund Box)")]
            public string refundTextAnchorMax = "0.25 1";

            [JsonProperty(PropertyName = "Refund - Text - Text Color")]
            public string refundTextColor = "1 1 1 0.9";

            [JsonProperty(PropertyName = "Refund - Text - Text Size")]
            public int refundTextSize = 18;

            [JsonProperty(PropertyName = "Refund - Text2 - Min Anchor (in Refund Box)")]
            public string refund2TextAnchorMin = "0.3 0";

            [JsonProperty(PropertyName = "Refund - Text2 - Max Anchor (in Refund Box)")]
            public string refund2TextAnchorMax = "1 1";

            [JsonProperty(PropertyName = "Refund - Text2 - Text Color")]
            public string refund2TextColor = "1 1 1 0.9";

            [JsonProperty(PropertyName = "Refund - Text2 - Text Size")]
            public int refund2TextSize = 16;

            [JsonProperty(PropertyName = "Crosshair - Enabled")]
            public bool showCrosshair = true;

            [JsonProperty(PropertyName = "Crosshair - Image Url")]
            public string crosshairImageUrl = "https://i.imgur.com/SqLCJaQ.png";

            [JsonProperty(PropertyName = "Crosshair - Box - Min Anchor (in Rust Window)")]
            public string crosshairAnchorMin = "0.5 0.5";

            [JsonProperty(PropertyName = "Crosshair - Box - Max Anchor (in Rust Window)")]
            public string crosshairAnchorMax = "0.5 0.5";

            [JsonProperty(PropertyName = "Crosshair - Box - Min Offset (in Rust Window)")]
            public string crosshairOffsetMin = "-15 -15";

            [JsonProperty(PropertyName = "Crosshair - Box - Max Offset (in Rust Window)")]
            public string crosshairOffsetMax = "15 15";

            [JsonProperty(PropertyName = "Crosshair - Box - Image Color")]
            public string crosshairColor = "1 0 0 1";

            [JsonIgnore]
            public Vector2 Price2TextAnchorMin, Price2TextAnchorMax, Refund2TextAnchorMin, Refund2TextAnchorMax;
        }

        public class RemoveSettings
        {
            [JsonProperty(PropertyName = "Price Enabled")]
            public bool priceEnabled = true;

            [JsonProperty(PropertyName = "Refund Enabled")]
            public bool refundEnabled = true;

            [JsonProperty(PropertyName = "Refund Items In Entity Slot")]
            public bool refundSlot = true;

            [JsonProperty(PropertyName = "Allowed Building Grade")]
            public Dictionary<BuildingGrade.Enum, bool> validConstruction = new Dictionary<BuildingGrade.Enum, bool>();

            [JsonProperty(PropertyName = "Building Blocks Settings")]
            public Dictionary<string, BuildingBlocksSettings> buildingBlockS = new Dictionary<string, BuildingBlocksSettings>();

            [JsonProperty(PropertyName = "Other Entity Settings")]
            public Dictionary<string, EntitySettings> entityS = new Dictionary<string, EntitySettings>();
        }

        public class BuildingBlocksSettings
        {
            [JsonProperty(PropertyName = "Display Name")]
            public string displayName;

            [JsonProperty(PropertyName = "Building Grade")]
            public Dictionary<BuildingGrade.Enum, BuildingGradeSettings> buildingGradeS = new Dictionary<BuildingGrade.Enum, BuildingGradeSettings>();
        }

        public class BuildingGradeSettings
        {
            [JsonProperty(PropertyName = "Price")]
            public object price;

            [JsonProperty(PropertyName = "Refund")]
            public object refund;

            [JsonIgnore] public float pricePercentage = -1, refundPercentage = -1;
            [JsonIgnore] public Dictionary<string, int> priceDict, refundDict;
        }

        public class EntitySettings
        {
            [JsonProperty(PropertyName = "Remove Allowed")]
            public bool enabled = false;

            [JsonProperty(PropertyName = "Display Name")]
            public string displayName = string.Empty;

            [JsonProperty(PropertyName = "Price")]
            public Dictionary<string, int> price = new Dictionary<string, int>();

            [JsonProperty(PropertyName = "Refund")]
            public Dictionary<string, int> refund = new Dictionary<string, int>();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                PreprocessOldConfig();
                configData = Config.ReadObject<ConfigData>();
                if (configData == null)
                {
                    LoadDefaultConfig();
                }
                else
                {
                    UpdateConfigValues();
                }
            }
            catch (Exception ex)
            {
                PrintError($"The configuration file is corrupted. \n{ex}");

                LoadDefaultConfig();
            }
            SaveConfig();
            PreprocessConfigValues();
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Creating a new configuration file");
            configData = new ConfigData();
            configData.version = Version;
        }

        protected override void SaveConfig() => Config.WriteObject(configData);

        private void PreprocessConfigValues()
        {
            configData.uiS.Price2TextAnchorMin = GetAnchor(configData.uiS.price2TextAnchorMin);
            configData.uiS.Price2TextAnchorMax = GetAnchor(configData.uiS.price2TextAnchorMax);
            configData.uiS.Refund2TextAnchorMin = GetAnchor(configData.uiS.refund2TextAnchorMin);
            configData.uiS.Refund2TextAnchorMax = GetAnchor(configData.uiS.refund2TextAnchorMax);

            foreach (var entry in configData.removeS.buildingBlockS)
            {
                foreach (var gradeEntry in entry.Value.buildingGradeS)
                {
                    var price = gradeEntry.Value.price;
                    float pricePercentage;
                    if (float.TryParse(price.ToString(), out pricePercentage))
                    {
                        gradeEntry.Value.pricePercentage = pricePercentage;
                    }
                    else
                    {
                        var priceDic = price as Dictionary<string, int>;
                        if (priceDic != null)
                        {
                            gradeEntry.Value.priceDict = priceDic;
                        }
                        else
                        {
                            try { gradeEntry.Value.priceDict = JsonConvert.DeserializeObject<Dictionary<string, int>>(price.ToString()); }
                            catch (Exception e)
                            {
                                gradeEntry.Value.priceDict = null;
                                PrintError($"Wrong price format for '{gradeEntry.Key}' of '{entry.Key}' in 'Building Blocks Settings'. Error Message: {e.Message}");
                            }
                        }
                    }

                    var refund = gradeEntry.Value.refund;
                    float refundPercentage;
                    if (float.TryParse(refund.ToString(), out refundPercentage))
                    {
                        gradeEntry.Value.refundPercentage = refundPercentage;
                    }
                    else
                    {
                        var refundDic = refund as Dictionary<string, int>;
                        if (refundDic != null)
                        {
                            gradeEntry.Value.refundDict = refundDic;
                        }
                        else
                        {
                            try { gradeEntry.Value.refundDict = JsonConvert.DeserializeObject<Dictionary<string, int>>(refund.ToString()); }
                            catch (Exception e)
                            {
                                gradeEntry.Value.refundDict = null;
                                PrintError($"Wrong refund format for '{gradeEntry.Key}' of '{entry.Key}' in 'Building Blocks Settings'. Error Message: {e.Message}");
                            }
                        }
                    }
                }
            }
        }

        private void UpdateConfigValues()
        {
            if (configData.version < Version)
            {
                if (configData.version <= default(VersionNumber))
                {
                    string prefix, prefixColor;
                    if (GetConfigValue(out prefix, "Chat Settings", "Chat Prefix") && GetConfigValue(out prefixColor, "Chat Settings", "Chat Prefix Color"))
                    {
                        configData.chatS.prefix = $"<color={prefixColor}>{prefix}</color>: ";
                    }

                    if (configData.uiS.removerToolAnchorMin == "0.1 0.55")
                    {
                        configData.uiS.removerToolAnchorMin = "0.04 0.55";
                    }

                    if (configData.uiS.removerToolAnchorMax == "0.4 0.95")
                    {
                        configData.uiS.removerToolAnchorMax = "0.37 0.95";
                    }
                }

                //if (configData.version <= new VersionNumber(4, 3, 18))
                //{
                //    configData.removerModeS.crosshairAnchorMin = "0.5 0.5";
                //    configData.removerModeS.crosshairAnchorMax = "0.5 0.5";
                //    configData.uiS.removerToolAnchorMin = "0 1";
                //    configData.uiS.removerToolAnchorMax = "0 1";
                //}

                //if (configData.version <= new VersionNumber(4, 3, 21))
                //{
                //    if (configData.removerModeS.crosshairAnchorMin == "0.5 0")
                //    {
                //        configData.removerModeS.crosshairAnchorMin = "0.5 0.5";
                //    }
                //    if (configData.removerModeS.crosshairAnchorMax == "0.5 0")
                //    {
                //        configData.removerModeS.crosshairAnchorMax = "0.5 0.5";
                //    }
                //}

                if (configData.version <= new VersionNumber(4, 3, 22))
                {
                    bool enabled;
                    if (GetConfigValue(out enabled, "Remove Mode Settings (Only one model works)", "Hammer Hit Mode"))
                    {
                        configData.removerModeS.meleeHitMode = true;
                        configData.removerModeS.meleeHitItemShortname = "hammer";
                    }
                    if (GetConfigValue(out enabled, "Remove Mode Settings (Only one model works)", "Hammer Hit Mode - Requires a hammer in your hand when remover tool is enabled"))
                    {
                        configData.removerModeS.meleeHitRequires = true;
                    }
                    if (GetConfigValue(out enabled, "Remove Mode Settings (Only one model works)", "Hammer Hit Mode - Disable remover tool when you are not holding a hammer"))
                    {
                        configData.removerModeS.meleeHitDisableInHand = true;
                    }
                }

                if (configData.version <= new VersionNumber(4, 3, 23))
                {
                    string value;
                    if (GetConfigValue(out value, "GUI", "Authorization Check - Box - Allowed Background"))
                    {
                        configData.uiS.allowedBackgroundColor = value == "0 1 0 0.8" ? "0.22 0.78 0.27 1" : value;
                    }
                    if (GetConfigValue(out value, "GUI", "Authorization Check - Box - Refused Background"))
                    {
                        configData.uiS.refusedBackgroundColor = value == "1 0 0 0.8" ? "0.78 0.22 0.27 1" : value;
                    }
                    if (configData.uiS.removeBackgroundColor == "0.42 0.88 0.88 1")
                        configData.uiS.removeBackgroundColor = "0.31 0.88 0.71 1";
                    if (configData.uiS.entityBackgroundColor == "0 0 0 0.8")
                        configData.uiS.entityBackgroundColor = "0.82 0.58 0.30 1";
                }
                configData.version = Version;
            }
        }

        private bool GetConfigValue<T>(out T value, params string[] path)
        {
            var configValue = Config.Get(path);
            if (configValue != null)
            {
                if (configValue is T)
                {
                    value = (T)configValue;
                    return true;
                }
                try
                {
                    value = Config.ConvertValue<T>(configValue);
                    return true;
                }
                catch (Exception ex)
                {
                    PrintError($"GetConfigValue ERROR: path: {string.Join("\\", path)}\n{ex}");
                }
            }

            value = default(T);
            return false;
        }

        private void SetConfigValue(params object[] pathAndTrailingValue)
        {
            Config.Set(pathAndTrailingValue);
        }

        #region Preprocess Old Config

        private void PreprocessOldConfig()
        {
            var jObject = Config.ReadObject<JObject>();
            if (jObject == null) return;
            //Interface.Oxide.DataFileSystem.WriteObject(Name + "_old", jObject);
            VersionNumber oldVersion;
            if (GetConfigVersionPre(jObject, out oldVersion))
            {
                if (oldVersion < Version)
                {
                    //Fixed typos
                    if (oldVersion <= new VersionNumber(4, 3, 23))
                    {
                        foreach (RemoveType value in Enum.GetValues(typeof(RemoveType)))
                        {
                            if (value == RemoveType.None) continue;
                            bool enabled;
                            if (GetConfigValuePre(jObject, out enabled, "Remove Type Settings", value.ToString(), "Reset the time after removing a entity"))
                            {
                                SetConfigValuePre(jObject, enabled, "Remove Type Settings", value.ToString(), "Reset the time after removing an entity");
                            }
                        }
                        Dictionary<string, object> values;
                        if (GetConfigValuePre(jObject, out values, "Permission Settings (Just for normal type)"))
                        {
                            foreach (var entry in values)
                            {
                                object value;
                                if (GetConfigValuePre(jObject, out value, "Permission Settings (Just for normal type)", entry.Key, "Reset the time after removing a entity"))
                                {
                                    SetConfigValuePre(jObject, value, "Permission Settings (Just for normal type)", entry.Key, "Reset the time after removing an entity");
                                }
                            }
                        }
                    }

                    if (oldVersion <= new VersionNumber(4, 3, 25))
                    {
                        bool enabled;
                        if (GetConfigValuePre(jObject, out enabled, "Remove Mode Settings (Only one model works)", "No Held Item Mode - Show Crosshair"))
                        {
                            SetConfigValuePre(jObject, enabled, "GUI", "Crosshair - Enabled");
                        }
                        object value;
                        if (GetConfigValuePre(jObject, out value, "Remove Mode Settings (Only one model works)", "No Held Item Mode - Crosshair Image Url"))
                        {
                            SetConfigValuePre(jObject, value, "GUI", "Crosshair - Image Url");
                        }
                        if (GetConfigValuePre(jObject, out value, "Remove Mode Settings (Only one model works)", "No Held Item Mode - Crosshair Box - Min Anchor (in Rust Window)"))
                        {
                            SetConfigValuePre(jObject, value, "GUI", "Crosshair - Box - Min Anchor (in Rust Window)");
                        }
                        if (GetConfigValuePre(jObject, out value, "Remove Mode Settings (Only one model works)", "No Held Item Mode - Crosshair Box - Max Anchor (in Rust Window)"))
                        {
                            SetConfigValuePre(jObject, value, "GUI", "Crosshair - Box - Max Anchor (in Rust Window)");
                        }
                        if (GetConfigValuePre(jObject, out value, "Remove Mode Settings (Only one model works)", "No Held Item Mode - Crosshair Box - Min Offset (in Rust Window)"))
                        {
                            SetConfigValuePre(jObject, value, "GUI", "Crosshair - Box - Min Offset (in Rust Window)");
                        }
                        if (GetConfigValuePre(jObject, out value, "Remove Mode Settings (Only one model works)", "No Held Item Mode - Crosshair Box - Max Offset (in Rust Window)"))
                        {
                            SetConfigValuePre(jObject, value, "GUI", "Crosshair - Box - Max Offset (in Rust Window)");
                        }
                        if (GetConfigValuePre(jObject, out value, "Remove Mode Settings (Only one model works)", "No Held Item Mode - Crosshair Box - Image Color"))
                        {
                            SetConfigValuePre(jObject, value, "GUI", "Crosshair - Box - Image Color");
                        }
                    }
                    Config.WriteObject(jObject);
                    //Interface.Oxide.DataFileSystem.WriteObject(Name + "_new", jObject);
                }
            }
        }

        private bool GetConfigValuePre<T>(JObject config, out T value, params string[] path)
        {
            if (path.Length < 1)
            {
                throw new ArgumentException("path is empty");
            }

            try
            {
                JToken jToken;
                if (!config.TryGetValue(path[0], out jToken))
                {
                    value = default(T);
                    return false;
                }

                for (int i = 1; i < path.Length; i++)
                {
                    var jObject = jToken.ToObject<JObject>();
                    if (jObject == null || !jObject.TryGetValue(path[i], out jToken))
                    {
                        value = default(T);
                        return false;
                    }
                }
                value = jToken.ToObject<T>();
                return true;
            }
            catch (Exception ex)
            {
                PrintError($"GetConfigValuePre ERROR: path: {string.Join("\\", path)}\n{ex}");
            }
            value = default(T);
            return false;
        }

        private void SetConfigValuePre(JObject config, object value, params string[] path)
        {
            if (path.Length < 1)
            {
                throw new ArgumentException("path is empty");
            }

            try
            {
                JToken jToken;
                if (!config.TryGetValue(path[0], out jToken))
                {
                    if (path.Length == 1)
                    {
                        jToken = JToken.FromObject(value);
                        config.Add(path[0], jToken);
                        return;
                    }
                    jToken = new JObject();
                    config.Add(path[0], jToken);
                }

                for (int i = 1; i < path.Length - 1; i++)
                {
                    var jObject = jToken as JObject;
                    if (jObject == null || !jObject.TryGetValue(path[i], out jToken))
                    {
                        jToken = new JObject();
                        jObject?.Add(path[i], jToken);
                    }
                }
                (jToken as JObject)?.Add(path[path.Length - 1], JToken.FromObject(value));
            }
            catch (Exception ex)
            {
                PrintError($"SetConfigValuePre ERROR: value: {value} path: {string.Join("\\", path)}\n{ex}");
            }
        }

        private bool GetConfigVersionPre(JObject config, out VersionNumber version)
        {
            try
            {
                JToken jToken;
                if (config.TryGetValue("Version", out jToken))
                {
                    version = jToken.ToObject<VersionNumber>();
                    return true;
                }
            }
            catch
            {
                // ignored
            }
            version = default(VersionNumber);
            return false;
        }

        #endregion Preprocess Old Config

        #endregion ConfigurationFile

        #region LanguageFile

        private void Print(BasePlayer player, string message)
        {
            Player.Message(player, message, configData.chatS.prefix, configData.chatS.steamIDIcon);
        }

        private void Print(ConsoleSystem.Arg arg, string message)
        {
            //SendReply(arg, message);
            var player = arg.Player();
            if (player == null)
            {
                Puts(message);
            }
            else
            {
                PrintToConsole(player, message);
            }
        }

        private string Lang(string key, string id = null, params object[] args)
        {
            try
            {
                return string.Format(lang.GetMessage(key, this, id), args);
            }
            catch (Exception)
            {
                PrintError($"Error in the language formatting of '{key}'. (userid: {id}. lang: {lang.GetLanguage(id)}. args: {string.Join(" ,", args)})");
                throw;
            }
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NotAllowed"] = "You don't have '{0}' permission to use this command.",
                ["TargetDisabled"] = "{0}'s Remover Tool has been disabled.",
                ["TargetEnabled"] = "{0} is now using Remover Tool; Enabled for {1} seconds (Max Removable Objects: {2}, Remove Type: {3}).",
                ["ToolDisabled"] = "Remover Tool has been disabled.",
                ["ToolEnabled"] = "Remover Tool enabled for {0} seconds (Max Removable Objects: {1}, Remove Type: {2}).",
                ["Cooldown"] = "You need to wait {0} seconds before using Remover Tool again.",
                ["CurrentlyDisabled"] = "Remover Tool is currently disabled.",
                ["EntityLimit"] = "Entity limit reached, you have removed {0} entities, Remover Tool was automatically disabled.",
                ["MeleeToolNotHeld"] = "You need to be holding a melee tool in order to use the Remover Tool.",
                ["SpecificToolNotHeld"] = "You need to be holding a specific tool in order to use the Remover Tool.",

                ["StartRemoveAll"] = "Start running RemoveAll, please wait.",
                ["StartRemoveStructure"] = "Start running RemoveStructure, please wait.",
                ["StartRemoveExternal"] = "Start running RemoveExternal, please wait.",
                ["AlreadyRemoveAll"] = "There is already a RemoveAll running, please wait.",
                ["AlreadyRemoveStructure"] = "There is already a RemoveStructure running, please wait.",
                ["AlreadyRemoveExternal"] = "There is already a RemoveExternal running, please wait.",
                ["CompletedRemoveAll"] = "You've successfully removed {0} entities using RemoveAll.",
                ["CompletedRemoveStructure"] = "You've successfully removed {0} entities using RemoveStructure.",
                ["CompletedRemoveExternal"] = "You've successfully removed {0} entities using RemoveExternal.",

                ["CanRemove"] = "You can remove this entity.",
                ["NotEnoughCost"] = "Can't remove: You don't have enough resources.",
                ["EntityDisabled"] = "Can't remove: Server has disabled the entity from being removed.",
                ["DamagedEntity"] = "Can't remove: Server has disabled damaged objects from being removed.",
                ["BeBlocked"] = "Can't remove: An external plugin blocked the usage.",
                ["InvalidEntity"] = "Can't remove: No valid entity targeted.",
                ["NotFoundOrFar"] = "Can't remove: The entity is not found or too far away.",
                ["StorageNotEmpty"] = "Can't remove: The entity storage is not empty.",
                ["RaidBlocked"] = "Can't remove: Raid blocked for {0} seconds.",
                ["NotRemoveAccess"] = "Can't remove: You don't have any rights to remove this.",
                ["NotStructure"] = "Can't remove: The entity is not a structure.",
                ["NotExternalWall"] = "Can't remove: The entity is not an external wall.",
                ["HasStash"] = "Can't remove: There are stashes under the foundation.",
                ["EntityTimeLimit"] = "Can't remove: The entity was built more than {0} seconds ago.",
                //["Can'tOpenAllLocks"] = "Can't remove: There is a lock in the building that you cannot open.",
                ["CantPay"] = "Can't remove: Paying system crashed! Contact an administrator with the time and date to help him understand what happened.",
                //["UsageOfRemove"] = "You have to hold a hammer in your hand and press the left mouse button.",

                ["Refund"] = "Refund:",
                ["Nothing"] = "Nothing",
                ["Price"] = "Price:",
                ["Free"] = "Free",
                ["TimeLeft"] = "Timeleft: {0}s\nRemoved: {1}",
                ["RemoverToolType"] = "Remover Tool ({0})",
                ["Unlimit"] = "∞",

                ["SyntaxError"] = "Syntax error, please type '<color=#ce422b>/{0} <help | h></color>' to view help",
                ["Syntax"] = "<color=#ce422b>/{0} [time (seconds)]</color> - Enable RemoverTool ({1})",
                ["Syntax1"] = "<color=#ce422b>/{0} <admin | a> [time (seconds)]</color> - Enable RemoverTool ({1})",
                ["Syntax2"] = "<color=#ce422b>/{0} <all> [time (seconds)]</color> - Enable RemoverTool ({1})",
                ["Syntax3"] = "<color=#ce422b>/{0} <structure | s> [time (seconds)]</color> - Enable RemoverTool ({1})",
                ["Syntax4"] = "<color=#ce422b>/{0} <external | e> [time (seconds)]</color> - Enable RemoverTool ({1})",
            }, this);

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NotAllowed"] = "您没有 '{0}' 权限来使用该命令",
                ["TargetDisabled"] = "'{0}' 的拆除工具已禁用",
                ["TargetEnabled"] = "'{0}' 的拆除工具已启用 {1} 秒 (可拆除数: {2}, 拆除模式: {3}).",
                ["ToolDisabled"] = "您的拆除工具已禁用",
                ["ToolEnabled"] = "您的拆除工具已启用 {0} 秒 (可拆除数: {1}, 拆除模式: {2}).",
                ["Cooldown"] = "您需要等待 {0} 秒才可以再次使用拆除工具",
                ["CurrentlyDisabled"] = "服务器当前已禁用了拆除工具",
                ["EntityLimit"] = "您已经拆除了 '{0}' 个实体，拆除工具已自动禁用",
                ["MeleeToolNotHeld"] = "您必须拿着近战工具才可以使用拆除工具",
                ["SpecificToolNotHeld"] = "您必须拿着指定工具才可以使用拆除工具",

                ["StartRemoveAll"] = "开始运行 '所有拆除'，请稍等片刻",
                ["StartRemoveStructure"] = "开始运行 '建筑拆除'，请稍等片刻",
                ["StartRemoveExternal"] = "开始运行 '外墙拆除'，请稍等片刻",
                ["AlreadyRemoveAll"] = "已经有一个 '所有拆除' 正在运行，请稍等片刻",
                ["AlreadyRemoveStructure"] = "已经有一个 '建筑拆除' 正在运行，请稍等片刻",
                ["AlreadyRemoveExternal"] = "已经有一个 '外墙拆除' 正在运行，请稍等片刻",
                ["CompletedRemoveAll"] = "您使用 '所有拆除' 成功拆除了 {0} 个实体",
                ["CompletedRemoveStructure"] = "您使用 '建筑拆除' 成功拆除了 {0} 个实体",
                ["CompletedRemoveExternal"] = "您使用 '外墙拆除' 成功拆除了 {0} 个实体",

                ["CanRemove"] = "您可以拆除该实体",
                ["NotEnoughCost"] = "无法拆除该实体: 拆除所需资源不足",
                ["EntityDisabled"] = "无法拆除该实体: 服务器已禁用拆除这种实体",
                ["DamagedEntity"] = "无法拆除该实体: 服务器已禁用拆除已损坏的实体",
                ["BeBlocked"] = "无法拆除该实体: 其他插件阻止您拆除该实体",
                ["InvalidEntity"] = "无法拆除该实体: 无效的实体",
                ["NotFoundOrFar"] = "无法拆除该实体: 没有找到实体或者距离太远",
                ["StorageNotEmpty"] = "无法拆除该实体: 该实体内含有物品",
                ["RaidBlocked"] = "无法拆除该实体: 拆除工具被突袭阻止了 {0} 秒",
                ["NotRemoveAccess"] = "无法拆除该实体: 您无权拆除该实体",
                ["NotStructure"] = "无法拆除该实体: 该实体不是建筑物",
                ["NotExternalWall"] = "无法拆除该实体: 该实体不是外高墙",
                ["HasStash"] = "无法拆除该实体: 地基下藏有小藏匿",
                ["EntityTimeLimit"] = "无法拆除该实体: 该实体的存活时间大于 {0} 秒",
                //["Can'tOpenAllLocks"] = "无法拆除该实体: 该建筑中有您无法打开的锁",
                ["CantPay"] = "无法拆除该实体: 支付失败，请联系管理员，告诉他详情",

                ["Refund"] = "退还:",
                ["Nothing"] = "没有",
                ["Price"] = "价格:",
                ["Free"] = "免费",
                ["TimeLeft"] = "剩余时间: {0}s\n已拆除数: {1} ",
                ["RemoverToolType"] = "拆除工具 ({0})",
                ["Unlimit"] = "∞",

                ["SyntaxError"] = "语法错误，输入 '<color=#ce422b>/{0} <help | h></color>' 查看帮助",
                ["Syntax"] = "<color=#ce422b>/{0} [time (seconds)]</color> - 启用拆除工具 ({1})",
                ["Syntax1"] = "<color=#ce422b>/{0} <admin | a> [time (seconds)]</color> - 启用拆除工具 ({1})",
                ["Syntax2"] = "<color=#ce422b>/{0} <all> [time (seconds)]</color> - 启用拆除工具 ({1})",
                ["Syntax3"] = "<color=#ce422b>/{0} <structure | s> [time (seconds)]</color> - 启用拆除工具 ({1})",
                ["Syntax4"] = "<color=#ce422b>/{0} <external | e> [time (seconds)]</color> - 启用拆除工具 ({1})",
            }, this, "zh-CN");
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NotAllowed"] = "У вас нет разрешения '{0}' чтобы использовать эту команду.",
                ["TargetDisabled"] = "{0}'s Remover Tool отключен.",
                ["TargetEnabled"] = "{0} теперь использует Remover Tool; Включено на {1} секунд (Макс. объектов для удаления: {2}, Тип удаления: {3}).",
                ["ToolDisabled"] = "Remover Tool отключен.",
                ["ToolEnabled"] = "Remover Tool включен на {0} секунд (Макс. объектов для удаления: {1}, Тип удаления: {2}).",
                ["Cooldown"] = "Необходимо подождать {0} секунд, прежде чем использовать Remover Tool снова.",
                ["CurrentlyDisabled"] = "Remover Tool в данный момент отключен.",
                ["EntityLimit"] = "Достигнут предел, удалено {0} объектов, Remover Tool автоматически отключен.",
                ["MeleeToolNotHeld"] = "Вы должны держать Инструмент ближнего боя, чтобы использовать инструмент для удаления.",
                ["SpecificToolNotHeld"] = "Вы должны держать Инструмент определенный, чтобы использовать инструмент для удаления.",

                ["StartRemoveAll"] = "Запускается RemoveAll, пожалуйста, подождите.",
                ["StartRemoveStructure"] = "Запускается RemoveStructure, пожалуйста, подождите.",
                ["StartRemoveExternal"] = "Запускается RemoveExternal, пожалуйста, подождите.",
                ["AlreadyRemoveAll"] = "RemoveAll уже выполняется, пожалуйста, подождите.",
                ["AlreadyRemoveStructure"] = "RemoveStructure уже выполняется, пожалуйста, подождите.",
                ["AlreadyRemoveExternal"] = "RemoveExternal уже выполняется, пожалуйста, подождите.",
                ["CompletedRemoveAll"] = "Вы успешно удалили {0} объектов используя RemoveAll.",
                ["CompletedRemoveStructure"] = "Вы успешно удалили {0} объектов используя RemoveStructure.",
                ["CompletedRemoveExternal"] = "Вы успешно удалили {0} объектов используя RemoveExternal.",

                ["CanRemove"] = "Вы можете удалить этот объект.",
                ["NotEnoughCost"] = "Нельзя удалить: У вас не достаточно ресурсов.",
                ["EntityDisabled"] = "Нельзя удалить: Сервер отключил возможность удаления этого объекта.",
                ["DamagedEntity"] = "Нельзя удалить: Сервер отключил возможность удалять повреждённые объекты.",
                ["BeBlocked"] = "Нельзя удалить: Внешний plugin блокирует использование.",
                ["InvalidEntity"] = "Нельзя удалить: Неверный объект.",
                ["NotFoundOrFar"] = "Нельзя удалить: Объект не найден, либо слишком далеко.",
                ["StorageNotEmpty"] = "Нельзя удалить: Хранилище объекта не пусто.",
                ["RaidBlocked"] = "Нельзя удалить: Рэйд-блок {0} секунд.",
                ["NotRemoveAccess"] = "Нельзя удалить: У вас нет прав удалять это.",
                ["NotStructure"] = "Нельзя удалить: Объект не конструкция.",
                ["NotExternalWall"] = "Нельзя удалить: Объект не внешняя стена.",
                ["HasStash"] = "Нельзя удалить: Обнаружены тайники под фундаментом.",
                ["EntityTimeLimit"] = "Нельзя удалить: Объект был построен более {0} секунд назад.",
                //["Can'tOpenAllLocks"] = "Нельзя удалить: в здании есть замок, который вы не можете открыть",
                ["CantPay"] = "Нельзя удалить: Система оплаты дала сбой! Свяжитесь с админом указав дату и время, чтобы помочь ему понять что случилось.",

                ["Refund"] = "Возврат:",
                ["Nothing"] = "Ничего",
                ["Price"] = "Цена:",
                ["Free"] = "Бесплатно",
                ["TimeLeft"] = "Осталось времени: {0}s\nУдалено: {1}",
                ["RemoverToolType"] = "Remover Tool ({0})",
                ["Unlimit"] = "∞",

                ["SyntaxError"] = "Синтаксическая ошибка! Пожалуйста, введите '<color=#ce422b>/{0} <help | h></color>' для отображения помощи",
                ["Syntax"] = "<color=#ce422b>/{0} [время (секунд)]</color> - Включить RemoverTool ({1})",
                ["Syntax1"] = "<color=#ce422b>/{0} <admin | a> [время (секунд)]</color> - Включить RemoverTool ({1})",
                ["Syntax2"] = "<color=#ce422b>/{0} <all> [время (секунд)]</color> - Включить RemoverTool ({1})",
                ["Syntax3"] = "<color=#ce422b>/{0} <structure | s> [время (секунд)]</color> - Включить RemoverTool ({1})",
                ["Syntax4"] = "<color=#ce422b>/{0} <external | e> [время (секунд)]</color> - Включить RemoverTool ({1})",
            }, this, "ru");
        }

        #endregion LanguageFile
    }
}