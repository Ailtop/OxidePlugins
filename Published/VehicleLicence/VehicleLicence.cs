// #define DEBUG

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Facepunch;
using Network;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust;
using Rust.Modular;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Vehicle Licence", "Sorrow/TheDoc/Arainrr", "1.8.0")]
    [Description("Allows players to buy vehicles and then spawn or store it")]
    public class VehicleLicence : RustPlugin
    {
        #region Fields

        [PluginReference] private readonly Plugin Economics, ServerRewards, Friends, Clans, NoEscape, LandOnCargoShip, RustTranslationAPI;

        private const string PERMISSION_USE = "vehiclelicence.use";
        private const string PERMISSION_ALL = "vehiclelicence.all";
        private const string PERMISSION_ADMIN = "vehiclelicence.admin";

        private const string PERMISSION_BYPASS_COST = "vehiclelicence.bypasscost";

        private const int ITEMID_FUEL = -946369541;
        private const string PREFAB_ITEM_DROP = "assets/prefabs/misc/item drop/item_drop.prefab";

        private const string PREFAB_ROWBOAT = "assets/content/vehicles/boats/rowboat/rowboat.prefab";
        private const string PREFAB_RHIB = "assets/content/vehicles/boats/rhib/rhib.prefab";
        private const string PREFAB_SEDAN = "assets/content/vehicles/sedan_a/sedantest.entity.prefab";
        private const string PREFAB_HOTAIRBALLOON = "assets/prefabs/deployable/hot air balloon/hotairballoon.prefab";
        private const string PREFAB_MINICOPTER = "assets/content/vehicles/minicopter/minicopter.entity.prefab";
        private const string PREFAB_TRANSPORTCOPTER = "assets/content/vehicles/scrap heli carrier/scraptransporthelicopter.prefab";
        private const string PREFAB_CHINOOK = "assets/prefabs/npc/ch47/ch47.entity.prefab";
        private const string PREFAB_RIDABLEHORSE = "assets/rust.ai/nextai/testridablehorse.prefab";
        private const string PREFAB_WORKCART = "assets/content/vehicles/workcart/workcart.entity.prefab";
        private const string PREFAB_MAGNET_CRANE = "assets/content/vehicles/crane_magnet/magnetcrane.entity.prefab";
        private const string PREFAB_SUBMARINE_DUO = "assets/content/vehicles/submarine/submarineduo.entity.prefab";
        private const string PREFAB_SUBMARINE_SOLO = "assets/content/vehicles/submarine/submarinesolo.entity.prefab";

        private const string PREFAB_CHASSIS_SMALL = "assets/content/vehicles/modularcar/car_chassis_2module.entity.prefab";
        private const string PREFAB_CHASSIS_MEDIUM = "assets/content/vehicles/modularcar/car_chassis_3module.entity.prefab";
        private const string PREFAB_CHASSIS_LARGE = "assets/content/vehicles/modularcar/car_chassis_4module.entity.prefab";

        private const string PREFAB_SNOWMOBILE = "assets/content/vehicles/snowmobiles/snowmobile.prefab";
        private const string PREFAB_SNOWMOBILE_TOMAHA = "assets/content/vehicles/snowmobiles/tomahasnowmobile.prefab";

        private const string PREFAB_TRAINWAGON_A = "assets/content/vehicles/train/trainwagona.entity.prefab";
        private const string PREFAB_TRAINWAGON_B = "assets/content/vehicles/train/trainwagonb.entity.prefab";
        private const string PREFAB_TRAINWAGON_C = "assets/content/vehicles/train/trainwagonc.entity.prefab";
        private const string PREFAB_TRAINWAGON_D = "assets/content/vehicles/train/trainwagond.entity.prefab";
        private const string PREFAB_WORKCART_ABOVEGROUND = "assets/content/vehicles/workcart/workcart_aboveground.entity.prefab";

        private const int LAYER_GROUND = Rust.Layers.Solid | Rust.Layers.Mask.Water;

        private readonly object _false = false;

        public static VehicleLicence Instance { get; private set; }

        public readonly Dictionary<BaseEntity, Vehicle> vehiclesCache = new Dictionary<BaseEntity, Vehicle>();
        public readonly Dictionary<string, BaseVehicleSettings> allVehicleSettings = new Dictionary<string, BaseVehicleSettings>();
        public readonly Dictionary<string, string> commandToVehicleType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public enum NormalVehicleType
        {
            Rowboat,
            RHIB,
            Sedan,
            HotAirBalloon,
            MiniCopter,
            TransportHelicopter,
            Chinook,
            RidableHorse,
            WorkCart,
            MagnetCrane,
            SubmarineSolo,
            SubmarineDuo,
            Snowmobile,
            TomahaSnowmobile,
        }

        public enum ChassisType
        {
            Small,
            Medium,
            Large
        }

        public enum TrainComponentType
        {
            Engine,
            A,
            B,
            C,
            D,
        }

        #endregion Fields

        #region Oxide Hooks

        private void Init()
        {
            LoadData();
            Instance = this;
            permission.RegisterPermission(PERMISSION_USE, this);
            permission.RegisterPermission(PERMISSION_ALL, this);
            permission.RegisterPermission(PERMISSION_ADMIN, this);
            permission.RegisterPermission(PERMISSION_BYPASS_COST, this);

            foreach (NormalVehicleType value in Enum.GetValues(typeof(NormalVehicleType)))
            {
                allVehicleSettings.Add(value.ToString(), GetBaseVehicleSettings(value));
            }
            foreach (var entry in configData.modularVehicles)
            {
                allVehicleSettings.Add(entry.Key, entry.Value);
            }
            foreach (var entry in configData.trainVehicles)
            {
                allVehicleSettings.Add(entry.Key, entry.Value);
            }

            foreach (var entry in allVehicleSettings)
            {
                var baseVehicleSettings = entry.Value;
                if (baseVehicleSettings.UsePermission && !string.IsNullOrEmpty(baseVehicleSettings.Permission))
                {
                    if (!permission.PermissionExists(baseVehicleSettings.Permission, this))
                    {
                        permission.RegisterPermission(baseVehicleSettings.Permission, this);
                    }
                }

                foreach (var perm in baseVehicleSettings.CooldownPermissions.Keys)
                {
                    if (!permission.PermissionExists(perm, this))
                    {
                        permission.RegisterPermission(perm, this);
                    }
                }

                foreach (var command in baseVehicleSettings.Commands)
                {
                    if (string.IsNullOrEmpty(command))
                    {
                        continue;
                    }
                    if (!commandToVehicleType.ContainsKey(command))
                    {
                        commandToVehicleType.Add(command, entry.Key);
                    }
                    else
                    {
                        PrintError($"You have the same two commands({command}).");
                    }
                    if (configData.chatSettings.useUniversalCommand)
                    {
                        cmd.AddChatCommand(command, this, nameof(CmdUniversal));
                    }
                    if (!string.IsNullOrEmpty(configData.chatSettings.customKillCommandPrefix))
                    {
                        cmd.AddChatCommand(configData.chatSettings.customKillCommandPrefix + command, this, nameof(CmdCustomKill));
                    }
                }
            }

            cmd.AddChatCommand(configData.chatSettings.helpCommand, this, nameof(CmdLicenseHelp));
            cmd.AddChatCommand(configData.chatSettings.buyCommand, this, nameof(CmdBuyVehicle));
            cmd.AddChatCommand(configData.chatSettings.spawnCommand, this, nameof(CmdSpawnVehicle));
            cmd.AddChatCommand(configData.chatSettings.recallCommand, this, nameof(CmdRecallVehicle));
            cmd.AddChatCommand(configData.chatSettings.killCommand, this, nameof(CmdKillVehicle));

            Unsubscribe(nameof(CanMountEntity));
            Unsubscribe(nameof(OnEntityTakeDamage));
            Unsubscribe(nameof(OnEntityDismounted));
            Unsubscribe(nameof(OnEntityEnter));
            Unsubscribe(nameof(CanLootEntity));
            Unsubscribe(nameof(OnEntitySpawned));
        }

        private void OnServerInitialized()
        {
            if (configData.globalSettings.storeVehicle)
            {
                var currentTimestamp = TimeEx.currentTimestamp;
                foreach (var playerData in storedData.playerData)
                {
                    foreach (var entry in playerData.Value)
                    {
                        entry.Value.LastRecall = entry.Value.LastDismount = currentTimestamp;
                        entry.Value.PlayerId = playerData.Key;
                        entry.Value.VehicleType = entry.Key;
                        if (entry.Value.EntityId == 0)
                        {
                            continue;
                        }
                        entry.Value.Entity = BaseNetworkable.serverEntities.Find(entry.Value.EntityId) as BaseEntity;
                        if (entry.Value.Entity == null || entry.Value.Entity.IsDestroyed)
                        {
                            entry.Value.EntityId = 0;
                        }
                        else
                        {
                            vehiclesCache.Add(entry.Value.Entity, entry.Value);
                        }
                    }
                }
            }
            if (configData.globalSettings.preventMounting)
            {
                Subscribe(nameof(CanMountEntity));
            }
            if (configData.globalSettings.noDecay)
            {
                Subscribe(nameof(OnEntityTakeDamage));
            }
            if (configData.globalSettings.preventDamagePlayer)
            {
                Subscribe(nameof(OnEntityEnter));
            }
            if (configData.globalSettings.preventLooting)
            {
                Subscribe(nameof(CanLootEntity));
            }
            if (configData.globalSettings.autoClaimFromVendor)
            {
                Subscribe(nameof(OnEntitySpawned));
                Subscribe(nameof(OnRidableAnimalClaimed));
            }
            if (configData.globalSettings.checkVehiclesInterval > 0 && allVehicleSettings.Any(x => x.Value.WipeTime > 0))
            {
                Subscribe(nameof(OnEntityDismounted));
                timer.Every(configData.globalSettings.checkVehiclesInterval, CheckVehicles);
            }
        }

        private void Unload()
        {
            if (!configData.globalSettings.storeVehicle)
            {
                foreach (var entry in vehiclesCache.ToArray())
                {
                    if (entry.Key != null && !entry.Key.IsDestroyed)
                    {
                        RefundVehicleItems(entry.Value, isUnload: true);
                        entry.Key.Kill(BaseNetworkable.DestroyMode.Gib);
                    }
                    entry.Value.EntityId = 0;
                }
            }
            SaveData();
            Instance = null;
        }

        private void OnServerSave() => timer.Once(UnityEngine.Random.Range(0f, 60f), SaveData);

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null || !player.userID.IsSteamId())
            {
                return;
            }
            if (permission.UserHasPermission(player.UserIDString, PERMISSION_BYPASS_COST))
            {
                PurchaseAllVehicles(player.userID);
            }
        }

        private void OnEntityDismounted(BaseMountable entity, BasePlayer player)
        {
            if (entity == null)
            {
                return;
            }
            var vehicleParent = entity.VehicleParent();
            if (vehicleParent == null || vehicleParent.IsDestroyed)
            {
                return;
            }
            Vehicle vehicle;
            if (!vehiclesCache.TryGetValue(vehicleParent, out vehicle))
            {
                return;
            }
            vehicle.OnDismount();
        }

        #region Mount

        private object CanMountEntity(BasePlayer friend, BaseMountable entity)
        {
            if (friend == null || entity == null)
            {
                return null;
            }
            var vehicleParent = entity.VehicleParent();
            if (vehicleParent == null || vehicleParent.IsDestroyed)
            {
                return null;
            }
            Vehicle vehicle;
            if (!vehiclesCache.TryGetValue(vehicleParent, out vehicle))
            {
                return null;
            }
            if (AreFriends(vehicle.PlayerId, friend.userID))
            {
                return null;
            }
            if (configData.globalSettings.preventDriverSeat && vehicleParent.HasMountPoints())
            {
                foreach (var mountPointInfo in vehicleParent.allMountPoints)
                {
                    if (mountPointInfo != null && mountPointInfo.mountable == entity)
                    {
                        if (!mountPointInfo.isDriver)
                        {
                            return null;
                        }
                        break;
                    }
                }
            }
            if (HasAdminPermission(friend))
            {
                return null;
            }
            SendCantUseMessage(friend, vehicle);
            return _false;
        }

        #endregion Mount

        #region Loot

        private object CanLootEntity(BasePlayer friend, StorageContainer container)
        {
            if (friend == null || container == null)
            {
                return null;
            }
            var parentEntity = container.GetParentEntity();
            if (parentEntity == null)
            {
                return null;
            }
            Vehicle vehicle;
            if (!vehiclesCache.TryGetValue(parentEntity, out vehicle))
            {
                var vehicleModule = parentEntity as BaseVehicleModule;
                if (vehicleModule == null)
                {
                    return null;
                }
                var vehicleParent = vehicleModule.Vehicle;
                if (vehicleParent == null || !vehiclesCache.TryGetValue(vehicleParent, out vehicle))
                {
                    return null;
                }
            }
            if (AreFriends(vehicle.PlayerId, friend.userID))
            {
                return null;
            }
            if (HasAdminPermission(friend))
            {
                return null;
            }
            SendCantUseMessage(friend, vehicle);
            return _false;
        }

        #endregion Loot

        #region Claim

        private void OnEntitySpawned(BaseSubmarine baseSubmarine)
        {
            TryClaimVehicle(baseSubmarine);
        }

        private void OnEntitySpawned(MotorRowboat motorRowboat)
        {
            TryClaimVehicle(motorRowboat);
        }

        private void OnEntitySpawned(MiniCopter miniCopter)
        {
            TryClaimVehicle(miniCopter);
        }

        private void OnRidableAnimalClaimed(BaseRidableAnimal baseRidableAnimal, BasePlayer player)
        {
            TryClaimVehicle(baseRidableAnimal, player);
        }

        #endregion Claim

        #region Decay

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if (entity == null || hitInfo?.damageTypes == null)
            {
                return;
            }
            if (hitInfo.damageTypes.Has(Rust.DamageType.Decay))
            {
                if (!vehiclesCache.ContainsKey(entity))
                {
                    var vehicleModule = entity as BaseVehicleModule;
                    if (vehicleModule == null)
                    {
                        return;
                    }
                    var vehicle = vehicleModule.Vehicle;
                    if (vehicle == null || !vehiclesCache.ContainsKey(vehicle))
                    {
                        return;
                    }
                }
                hitInfo.damageTypes.Scale(Rust.DamageType.Decay, 0);
            }
        }

        #endregion Decay

        #region Damage

        // ScrapTransportHelicopter / ModularCar / TrainEngine / MagnetCrane
        private object OnEntityEnter(TriggerHurtNotChild triggerHurtNotChild, BasePlayer player)
        {
            if (triggerHurtNotChild == null || player == null || triggerHurtNotChild.SourceEntity == null)
            {
                return null;
            }
            var sourceEntity = triggerHurtNotChild.SourceEntity;
            if (vehiclesCache.ContainsKey(sourceEntity))
            {
                var baseVehicle = sourceEntity as BaseVehicle;
                if (baseVehicle != null && player.userID.IsSteamId())
                {
                    if (baseVehicle is TrainEngine)
                    {
                        var transform = triggerHurtNotChild.transform;
                        MoveToPosition(player, transform.position + (UnityEngine.Random.value >= 0.5f ? -transform.right : transform.right) * 2.5f);
                        return _false;
                    }
                    Vector3 pos;
                    if (GetDismountPosition(baseVehicle, player, out pos))
                    {
                        MoveToPosition(player, pos);
                    }
                }
                //triggerHurtNotChild.enabled = false;
                return _false;
            }
            return null;
        }

        // HotAirBalloon
        private object OnEntityEnter(TriggerHurt triggerHurt, BasePlayer player)
        {
            if (triggerHurt == null || player == null)
            {
                return null;
            }
            var sourceEntity = triggerHurt.gameObject.ToBaseEntity();
            if (sourceEntity == null)
            {
                return null;
            }
            if (vehiclesCache.ContainsKey(sourceEntity))
            {
                if (player.userID.IsSteamId())
                {
                    MoveToPosition(player, sourceEntity.CenterPoint() + Vector3.down);
                }
                //triggerHurt.enabled = false;
                return _false;
            }
            return null;
        }

        #endregion Damage

        #region Destroy

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info) => OnEntitySpawnedOrKill(entity, true);

        private void OnEntityKill(BaseCombatEntity entity) => OnEntitySpawnedOrKill(entity);

        #endregion Destroy

        #region Reskin

        private object OnEntityReskin(BaseEntity entity, ItemSkinDirectory skin, BasePlayer player)
        {
            if (entity == null || player == null)
            {
                return null;
            }
            Vehicle vehicle;
            if (vehiclesCache.TryGetValue(entity, out vehicle))
            {
                return _false;
            }
            return null;
        }

        #endregion Reskin

        #endregion Oxide Hooks

        #region Methods

        #region Message

        private void SendCantUseMessage(BasePlayer friend, Vehicle vehicle)
        {
            var baseVehicleSettings = GetBaseVehicleSettings(vehicle.VehicleType);
            if (baseVehicleSettings != null)
            {
                var player = RustCore.FindPlayerById(vehicle.PlayerId);
                var playerName = player?.displayName ?? ServerMgr.Instance.persistance.GetPlayerName(vehicle.PlayerId) ?? "Unknown";
                Print(friend, Lang("CantUse", friend.UserIDString, baseVehicleSettings.DisplayName, $"<color=#{(player != null && player.IsConnected ? "69D214" : "FF6347")}>{playerName}</color>"));
            }
        }

        #endregion Message

        #region CheckEntity

        private void OnEntitySpawnedOrKill(BaseCombatEntity entity, bool isCrash = false)
        {
            if (entity == null)
            {
                return;
            }
            Vehicle vehicle;
            if (!vehiclesCache.TryGetValue(entity, out vehicle))
            {
                return;
            }
            vehiclesCache.Remove(entity);
            vehicle.OnDeath();

            RefundVehicleItems(vehicle, isCrash);

            var baseVehicleSettings = GetBaseVehicleSettings(vehicle.VehicleType);
            if (isCrash && baseVehicleSettings.RemoveLicenseOnceCrash)
            {
                RemoveVehicleLicense(vehicle.PlayerId, vehicle.VehicleType);
            }
        }

        #endregion CheckEntity

        #region CheckVehicles

        private void CheckVehicles()
        {
            var currentTimestamp = TimeEx.currentTimestamp;
            foreach (var entry in vehiclesCache.ToArray())
            {
                if (entry.Key == null || entry.Key.IsDestroyed)
                {
                    continue;
                }
                if (VehicleIsActive(entry.Key, entry.Value, currentTimestamp))
                {
                    continue;
                }

                if (VehicleAnyMounted(entry.Key))
                {
                    continue;
                }
                entry.Key.Kill(BaseNetworkable.DestroyMode.Gib);
            }
        }

        private bool VehicleIsActive(BaseEntity entity, Vehicle vehicle, double currentTimestamp)
        {
            var baseVehicleSettings = GetBaseVehicleSettings(vehicle.VehicleType);
            if (baseVehicleSettings.WipeTime <= 0)
            {
                return true;
            }
            if (baseVehicleSettings.ExcludeCupboard && entity.GetBuildingPrivilege() != null)
            {
                return true;
            }
            return currentTimestamp - vehicle.LastDismount < baseVehicleSettings.WipeTime;
        }

        #endregion CheckVehicles

        #region Refund

        private void RefundVehicleItems(Vehicle vehicle, bool isCrash = false, bool isUnload = false)
        {
            var entity = vehicle.Entity;
            if (entity == null || vehicle.Entity.IsDestroyed)
            {
                return;
            }

            var baseVehicleSettings = GetBaseVehicleSettings(vehicle.VehicleType);
            baseVehicleSettings.RefundVehicleItems(vehicle, isCrash, isUnload);
        }

        private static void DropItemContainer(BaseEntity entity, ulong playerId, List<Item> collect)
        {
            var droppedItemContainer = GameManager.server.CreateEntity(PREFAB_ITEM_DROP, entity.GetDropPosition(), entity.transform.rotation) as DroppedItemContainer;
            if (droppedItemContainer != null)
            {
                droppedItemContainer.inventory = new ItemContainer();
                droppedItemContainer.inventory.ServerInitialize(null, Mathf.Min(collect.Count, droppedItemContainer.maxItemCount));
                droppedItemContainer.inventory.GiveUID();
                droppedItemContainer.inventory.entityOwner = droppedItemContainer;
                droppedItemContainer.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);
                for (var i = collect.Count - 1; i >= 0; i--)
                {
                    var item = collect[i];
                    if (!item.MoveToContainer(droppedItemContainer.inventory))
                    {
                        item.DropAndTossUpwards(droppedItemContainer.transform.position);
                    }
                }

                droppedItemContainer.OwnerID = playerId;
                droppedItemContainer.Spawn();
            }
        }

        #endregion Refund

        #region TryPay

        private bool TryPay(BasePlayer player, Dictionary<string, PriceInfo> prices, out string missingResources)
        {
            if (permission.UserHasPermission(player.UserIDString, PERMISSION_BYPASS_COST))
            {
                missingResources = null;
                return true;
            }

            if (!CanPay(player, prices, out missingResources))
            {
                return false;
            }

            var collect = Pool.GetList<Item>();
            foreach (var entry in prices)
            {
                if (entry.Value.amount <= 0)
                {
                    continue;
                }
                var itemDefinition = ItemManager.FindItemDefinition(entry.Key);
                if (itemDefinition != null)
                {
                    player.inventory.Take(collect, itemDefinition.itemid, entry.Value.amount);
                    player.Command("note.inv", itemDefinition.itemid, -entry.Value.amount);
                    continue;
                }
                switch (entry.Key.ToLower())
                {
                    case "economics":
                        Economics?.Call("Withdraw", player.userID, (double)entry.Value.amount);
                        continue;

                    case "serverrewards":
                        ServerRewards?.Call("TakePoints", player.userID, entry.Value.amount);
                        continue;
                }
            }

            foreach (var item in collect)
            {
                item.Remove();
            }
            Pool.FreeList(ref collect);
            missingResources = null;
            return true;
        }

        private readonly Hash<string, int> _entries = new Hash<string, int>();

        private bool CanPay(BasePlayer player, Dictionary<string, PriceInfo> prices, out string missingResources)
        {
            _entries.Clear();
            var language = RustTranslationAPI != null ? lang.GetLanguage(player.UserIDString) : null;
            foreach (var entry in prices)
            {
                if (entry.Value.amount <= 0) continue;
                int missingAmount;
                var itemDefinition = ItemManager.FindItemDefinition(entry.Key);
                if (itemDefinition != null)
                {
                    missingAmount = entry.Value.amount - player.inventory.GetAmount(itemDefinition.itemid);
                }
                else
                {
                    missingAmount = CheckBalance(entry.Key, entry.Value.amount, player.userID);
                }

                if (missingAmount <= 0)
                {
                    continue;
                }
                var displayName = GetItemDisplayName(language, entry.Key, entry.Value.displayName);
                _entries[displayName] += missingAmount;
            }
            if (_entries.Count > 0)
            {
                StringBuilder stringBuilder = Pool.Get<StringBuilder>();
                foreach (var entry in _entries)
                {
                    stringBuilder.AppendLine($"* {Lang("PriceFormat", player.UserIDString, entry.Key, entry.Value)}");
                }
                missingResources = stringBuilder.ToString();
                stringBuilder.Clear();
                Pool.Free(ref stringBuilder);
                return false;
            }
            missingResources = null;
            return true;
        }

        private int CheckBalance(string key, int price, ulong playerId)
        {
            switch (key.ToLower())
            {
                case "economics":
                    var balance = Economics?.Call("Balance", playerId);
                    if (balance is double)
                    {
                        var n = price - (double)balance;
                        return n <= 0 ? 0 : (int)Math.Ceiling(n);
                    }
                    return price;

                case "serverrewards":
                    var points = ServerRewards?.Call("CheckPoints", playerId);
                    if (points is int)
                    {
                        var n = price - (int)points;
                        return n <= 0 ? 0 : n;
                    }
                    return price;

                default:
                    PrintError($"Unknown Currency Type '{key}'");
                    return price;
            }
        }

        #endregion TryPay

        #region AreFriends

        private bool AreFriends(ulong playerId, ulong friendId)
        {
            if (playerId == friendId)
            {
                return true;
            }
            if (configData.globalSettings.useTeams && SameTeam(playerId, friendId))
            {
                return true;
            }

            if (configData.globalSettings.useFriends && HasFriend(playerId, friendId))
            {
                return true;
            }
            if (configData.globalSettings.useClans && SameClan(playerId, friendId))
            {
                return true;
            }
            return false;
        }

        private static bool SameTeam(ulong playerId, ulong friendId)
        {
            if (!RelationshipManager.TeamsEnabled())
            {
                return false;
            }
            var playerTeam = RelationshipManager.ServerInstance.FindPlayersTeam(playerId);
            if (playerTeam == null)
            {
                return false;
            }
            var friendTeam = RelationshipManager.ServerInstance.FindPlayersTeam(friendId);
            if (friendTeam == null)
            {
                return false;
            }
            return playerTeam == friendTeam;
        }

        private bool HasFriend(ulong playerId, ulong friendId)
        {
            if (Friends == null)
            {
                return false;
            }
            return (bool)Friends.Call("HasFriend", playerId, friendId);
        }

        private bool SameClan(ulong playerId, ulong friendId)
        {
            if (Clans == null)
            {
                return false;
            }
            //Clans
            var isMember = Clans.Call("IsClanMember", playerId.ToString(), friendId.ToString());
            if (isMember != null)
            {
                return (bool)isMember;
            }
            //Rust:IO Clans
            var playerClan = Clans.Call("GetClanOf", playerId);
            if (playerClan == null)
            {
                return false;
            }
            var friendClan = Clans.Call("GetClanOf", friendId);
            if (friendClan == null)
            {
                return false;
            }
            return playerClan == friendClan;
        }

        #endregion AreFriends

        #region IsPlayerBlocked

        private bool IsPlayerBlocked(BasePlayer player)
        {
            if (NoEscape == null)
            {
                return false;
            }
            if (configData.globalSettings.useRaidBlocker && IsRaidBlocked(player.UserIDString))
            {
                Print(player, Lang("RaidBlocked", player.UserIDString));
                return true;
            }
            if (configData.globalSettings.useCombatBlocker && IsCombatBlocked(player.UserIDString))
            {
                Print(player, Lang("CombatBlocked", player.UserIDString));
                return true;
            }
            return false;
        }

        private bool IsRaidBlocked(string playerId) => (bool)NoEscape.Call("IsRaidBlocked", playerId);

        private bool IsCombatBlocked(string playerId) => (bool)NoEscape.Call("IsCombatBlocked", playerId);

        #endregion IsPlayerBlocked

        #region GetSettings

        private BaseVehicleSettings GetBaseVehicleSettings(string vehicleType)
        {
            BaseVehicleSettings baseVehicleSettings;
            return allVehicleSettings.TryGetValue(vehicleType, out baseVehicleSettings) ? baseVehicleSettings : null;
        }

        private BaseVehicleSettings GetBaseVehicleSettings(NormalVehicleType normalVehicleType)
        {
            switch (normalVehicleType)
            {
                case NormalVehicleType.Rowboat: return configData.normalVehicles.rowboat;
                case NormalVehicleType.RHIB: return configData.normalVehicles.rhib;
                case NormalVehicleType.Sedan: return configData.normalVehicles.sedan;
                case NormalVehicleType.HotAirBalloon: return configData.normalVehicles.hotAirBalloon;
                case NormalVehicleType.MiniCopter: return configData.normalVehicles.miniCopter;
                case NormalVehicleType.TransportHelicopter: return configData.normalVehicles.transportHelicopter;
                case NormalVehicleType.Chinook: return configData.normalVehicles.chinook;
                case NormalVehicleType.RidableHorse: return configData.normalVehicles.ridableHorse;
                case NormalVehicleType.WorkCart: return configData.normalVehicles.workCart;
                case NormalVehicleType.MagnetCrane: return configData.normalVehicles.magnetCrane;
                case NormalVehicleType.SubmarineSolo: return configData.normalVehicles.submarineSolo;
                case NormalVehicleType.SubmarineDuo: return configData.normalVehicles.submarineDuo;
                case NormalVehicleType.Snowmobile: return configData.normalVehicles.snowmobile;
                case NormalVehicleType.TomahaSnowmobile: return configData.normalVehicles.tomahaSnowmobile;
                default: return null;
            }
        }

        #endregion GetSettings

        #region Permission

        private bool HasAdminPermission(BasePlayer player) => permission.UserHasPermission(player.UserIDString, PERMISSION_ADMIN);

        private bool CanViewVehicleInfo(BasePlayer player, string vehicleType, BaseVehicleSettings baseVehicleSettings)
        {
            if (baseVehicleSettings.Purchasable && baseVehicleSettings.Commands.Count > 0)
            {
                return HasVehiclePermission(player, vehicleType, baseVehicleSettings);
            }
            return false;
        }

        private bool HasVehiclePermission(BasePlayer player, string vehicleType, BaseVehicleSettings baseVehicleSettings = null)
        {
            if (baseVehicleSettings == null)
            {
                baseVehicleSettings = GetBaseVehicleSettings(vehicleType);
            }
            if (!baseVehicleSettings.UsePermission || string.IsNullOrEmpty(baseVehicleSettings.Permission))
            {
                return true;
            }
            return permission.UserHasPermission(player.UserIDString, PERMISSION_ALL) ||
                   permission.UserHasPermission(player.UserIDString, baseVehicleSettings.Permission);
        }

        #endregion Permission

        #region Cooldown

        private double GetSpawnCooldown(BasePlayer player, BaseVehicleSettings baseVehicleSettings)
        {
            double cooldown = baseVehicleSettings.SpawnCooldown;
            foreach (var entry in baseVehicleSettings.CooldownPermissions)
            {
                if (cooldown > entry.Value.spawnCooldown && permission.UserHasPermission(player.UserIDString, entry.Key))
                {
                    cooldown = entry.Value.spawnCooldown;
                }
            }
            return cooldown;
        }

        private double GetRecallCooldown(BasePlayer player, BaseVehicleSettings baseVehicleSettings)
        {
            double cooldown = baseVehicleSettings.RecallCooldown;
            foreach (var entry in baseVehicleSettings.CooldownPermissions)
            {
                if (cooldown > entry.Value.recallCooldown && permission.UserHasPermission(player.UserIDString, entry.Key))
                {
                    cooldown = entry.Value.recallCooldown;
                }
            }
            return cooldown;
        }

        #endregion Cooldown

        #region Claim

        private void TryClaimVehicle(BaseVehicle baseVehicle)
        {
            NextTick(() =>
            {
                if (baseVehicle == null)
                {
                    return;
                }
                var player = baseVehicle.creatorEntity as BasePlayer;
                if (player == null || !player.userID.IsSteamId() || !baseVehicle.OnlyOwnerAccessible())
                {
                    return;
                }
                var vehicleType = GetClaimableVehicleType(baseVehicle);
                if (vehicleType.HasValue)
                {
                    TryClaimVehicle(player, baseVehicle, vehicleType.Value.ToString());
                }
            });
        }

        private void TryClaimVehicle(BaseVehicle baseVehicle, BasePlayer player)
        {
            if (player == null || !player.userID.IsSteamId())
            {
                return;
            }
            var vehicleType = GetClaimableVehicleType(baseVehicle);
            if (vehicleType.HasValue)
            {
                TryClaimVehicle(player, baseVehicle, vehicleType.Value.ToString());
            }
        }

        private bool TryClaimVehicle(BasePlayer player, BaseEntity entity, string vehicleType)
        {
            Vehicle vehicle;
            if (!storedData.IsVehiclePurchased(player.userID, vehicleType, out vehicle))
            {
                if (!configData.globalSettings.autoUnlockFromVendor)
                {
                    return false;
                }
                storedData.AddVehicleLicense(player.userID, vehicleType);
                vehicle = storedData.GetVehicleLicense(player.userID, vehicleType);
            }
            if (vehicle.Entity == null || vehicle.Entity.IsDestroyed)
            {
                entity.OwnerID = player.userID;
                SetupVehicleEntity(entity, vehicle, player, false);
                return true;
            }
            return false;
        }

        #endregion Claim

        #region Helpers

        private static NormalVehicleType? GetClaimableVehicleType(BaseVehicle baseVehicle)
        {
            if (baseVehicle is BaseRidableAnimal)
            {
                return NormalVehicleType.RidableHorse;
            }
            if (baseVehicle is ScrapTransportHelicopter)
            {
                return NormalVehicleType.TransportHelicopter;
            }
            if (baseVehicle is MiniCopter)
            {
                return NormalVehicleType.MiniCopter;
            }
            if (baseVehicle is RHIB)
            {
                return NormalVehicleType.RHIB;
            }
            if (baseVehicle is MotorRowboat)
            {
                return NormalVehicleType.Rowboat;
            }
            if (baseVehicle is SubmarineDuo)
            {
                return NormalVehicleType.SubmarineDuo;
            }
            if (baseVehicle is BaseSubmarine)
            {
                return NormalVehicleType.SubmarineSolo;
            }
            return null;
        }

        private static bool NeedCheckWater(string vehicleType)
        {
            return vehicleType == nameof(NormalVehicleType.Rowboat) ||
                   vehicleType == nameof(NormalVehicleType.RHIB) ||
                   vehicleType == nameof(NormalVehicleType.SubmarineDuo) ||
                   vehicleType == nameof(NormalVehicleType.SubmarineSolo);
        }

        private static string GetVehiclePrefab(string vehicleType, BaseVehicleSettings baseVehicleSettings)
        {
            NormalVehicleType normalVehicleType;
            if (Enum.TryParse(vehicleType, out normalVehicleType))
            {
                switch (normalVehicleType)
                {
                    case NormalVehicleType.Rowboat: return PREFAB_ROWBOAT;
                    case NormalVehicleType.RHIB: return PREFAB_RHIB;
                    case NormalVehicleType.Sedan: return PREFAB_SEDAN;
                    case NormalVehicleType.HotAirBalloon: return PREFAB_HOTAIRBALLOON;
                    case NormalVehicleType.MiniCopter: return PREFAB_MINICOPTER;
                    case NormalVehicleType.TransportHelicopter: return PREFAB_TRANSPORTCOPTER;
                    case NormalVehicleType.Chinook: return PREFAB_CHINOOK;
                    case NormalVehicleType.RidableHorse: return PREFAB_RIDABLEHORSE;
                    case NormalVehicleType.WorkCart: return PREFAB_WORKCART;
                    case NormalVehicleType.MagnetCrane: return PREFAB_MAGNET_CRANE;
                    case NormalVehicleType.SubmarineSolo: return PREFAB_SUBMARINE_SOLO;
                    case NormalVehicleType.SubmarineDuo: return PREFAB_SUBMARINE_DUO;
                    case NormalVehicleType.Snowmobile: return PREFAB_SNOWMOBILE;
                    case NormalVehicleType.TomahaSnowmobile: return PREFAB_SNOWMOBILE_TOMAHA;
                    default: return null;
                }
            }

            var modularVehicleSettings = baseVehicleSettings as ModularVehicleSettings;
            if (modularVehicleSettings != null)
            {
                switch (modularVehicleSettings.ChassisType)
                {
                    case ChassisType.Small: return PREFAB_CHASSIS_SMALL;
                    case ChassisType.Medium: return PREFAB_CHASSIS_MEDIUM;
                    case ChassisType.Large: return PREFAB_CHASSIS_LARGE;
                    default: return null;
                }
            }
            return null;
        }

        private static string GetTrainVehiclePrefab(TrainComponentType componentType)
        {
            switch (componentType)
            {
                case TrainComponentType.Engine: return PREFAB_WORKCART_ABOVEGROUND;
                case TrainComponentType.A: return PREFAB_TRAINWAGON_A;
                case TrainComponentType.B: return PREFAB_TRAINWAGON_B;
                case TrainComponentType.C: return PREFAB_TRAINWAGON_C;
                case TrainComponentType.D: return PREFAB_TRAINWAGON_D;
                default: return null;
            }
        }

        private static bool GetDismountPosition(BaseVehicle baseVehicle, BasePlayer player, out Vector3 result)
        {
            var parentVehicle = baseVehicle.VehicleParent();
            if (parentVehicle != null)
            {
                return GetDismountPosition(parentVehicle, player, out result);
            }
            var list = Pool.GetList<Vector3>();
            foreach (var transform in baseVehicle.dismountPositions)
            {
                var visualCheckOrigin = transform.position + Vector3.up * 0.6f;
                if (baseVehicle.ValidDismountPosition(transform.position, visualCheckOrigin))
                {
                    list.Add(transform.position);
                }
            }
            if (list.Count == 0)
            {
                result = Vector3.zero;
                Pool.FreeList(ref list);
                return false;
            }
            Vector3 pos = player.transform.position;
            list.Sort((a, b) => Vector3.Distance(a, pos).CompareTo(Vector3.Distance(b, pos)));
            result = list[0];
            Pool.FreeList(ref list);
            return true;
        }

        private static bool VehicleAnyMounted(BaseEntity entity)
        {
            var baseVehicle = entity as BaseVehicle;
            if (baseVehicle != null && baseVehicle.AnyMounted())
            {
                return true;
            }
            return entity.GetComponentsInChildren<BasePlayer>()?.Length > 0;
        }

        private static void DismountAllPlayers(BaseEntity entity)
        {
            var baseVehicle = entity as BaseVehicle;
            if (baseVehicle != null)
            {
                //(vehicle as BaseVehicle).DismountAllPlayers();
                foreach (var mountPointInfo in baseVehicle.allMountPoints)
                {
                    if (mountPointInfo != null && mountPointInfo.mountable != null)
                    {
                        var mounted = mountPointInfo.mountable.GetMounted();
                        if (mounted != null)
                        {
                            mountPointInfo.mountable.DismountPlayer(mounted);
                        }
                    }
                }
            }
            var players = entity.GetComponentsInChildren<BasePlayer>();
            foreach (var player in players)
            {
                player.SetParent(null, true, true);
            }
        }

        private static Vector3 GetGroundPositionLookingAt(BasePlayer player, float distance, bool needUp = true)
        {
            RaycastHit hitInfo;
            var headRay = player.eyes.HeadRay();
            if (Physics.Raycast(headRay, out hitInfo, distance, LAYER_GROUND))
            {
                return hitInfo.point;
            }
            return GetGroundPosition(headRay.origin + headRay.direction * distance, needUp);
        }

        private static Vector3 GetGroundPosition(Vector3 position, bool needUp = true)
        {
            RaycastHit hitInfo;
            position.y = Physics.Raycast(needUp ? position + Vector3.up * 250 : position, Vector3.down, out hitInfo, needUp ? 400f : 50f, LAYER_GROUND)
                ? hitInfo.point.y
                : TerrainMeta.HeightMap.GetHeight(position);
            return position;
        }

        private static bool IsInWater(Vector3 position)
        {
            var colliders = Pool.GetList<Collider>();
            Vis.Colliders(position, 0.5f, colliders);
            if (colliders.Any(x => x.gameObject.layer == (int)Rust.Layer.Water))
            {
                Pool.FreeList(ref colliders);
                return true;
            }
            Pool.FreeList(ref colliders);
            return WaterLevel.Test(position);
        }

        private static void MoveToPosition(BasePlayer player, Vector3 position)
        {
            player.Teleport(position);
            player.ForceUpdateTriggers();
            //if (player.HasParent()) player.SetParent(null, true, true);
            player.SendNetworkUpdateImmediate();
        }

        #endregion Helpers

        #endregion Methods

        #region API

        [HookMethod(nameof(IsLicensedVehicle))]
        public bool IsLicensedVehicle(BaseEntity entity)
        {
            return vehiclesCache.ContainsKey(entity);
        }

        [HookMethod(nameof(GetLicensedVehicle))]
        public BaseEntity GetLicensedVehicle(ulong playerId, string license)
        {
            return storedData.GetVehicleLicense(playerId, license)?.Entity;
        }

        [HookMethod(nameof(HasVehicleLicense))]
        public bool HasVehicleLicense(ulong playerId, string license)
        {
            return storedData.HasVehicleLicense(playerId, license);
        }

        [HookMethod(nameof(RemoveVehicleLicense))]
        public bool RemoveVehicleLicense(ulong playerId, string license)
        {
            return storedData.RemoveVehicleLicense(playerId, license);
        }

        [HookMethod(nameof(AddVehicleLicense))]
        public bool AddVehicleLicense(ulong playerId, string license)
        {
            return storedData.AddVehicleLicense(playerId, license);
        }

        [HookMethod(nameof(GetVehicleLicenses))]
        public List<string> GetVehicleLicenses(ulong playerId)
        {
            return storedData.GetVehicleLicenseNames(playerId);
        }

        [HookMethod(nameof(PurchaseAllVehicles))]
        public void PurchaseAllVehicles(ulong playerId)
        {
            storedData.PurchaseAllVehicles(playerId);
        }

        #endregion API

        #region Commands

        #region Universal Command

        private void CmdUniversal(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
            {
                Print(player, Lang("NotAllowed", player.UserIDString));
                return;
            }

            string vehicleType;
            if (IsValidOption(player, command, out vehicleType))
            {
                var bypassCooldown = args.Length >= 1 && IsValidBypassCooldownOption(args[0]);
                HandleUniversalCmd(player, vehicleType, bypassCooldown, command);
            }
        }

        private void HandleUniversalCmd(BasePlayer player, string vehicleType, bool bypassCooldown, string command)
        {
            Vehicle vehicle;
            if (storedData.IsVehiclePurchased(player.userID, vehicleType, out vehicle))
            {
                string reason; Vector3 position = Vector3.zero; Quaternion rotation = Quaternion.identity;
                if (vehicle.Entity != null && !vehicle.Entity.IsDestroyed)
                {
                    //recall
                    if (CanRecall(player, vehicle, bypassCooldown, out reason, ref position, ref rotation, command))
                    {
                        RecallVehicle(player, vehicle, position, rotation);
                        return;
                    }
                }
                else
                {
                    //spawn
                    if (CanSpawn(player, vehicle, bypassCooldown, out reason, ref position, ref rotation, command))
                    {
                        SpawnVehicle(player, vehicle, position, rotation);
                        return;
                    }
                }
                Print(player, reason);
                return;
            }
            //buy
            BuyVehicle(player, vehicleType);
        }

        #endregion Universal Command

        #region Custom Kill Command

        private void CmdCustomKill(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
            {
                Print(player, Lang("NotAllowed", player.UserIDString));
                return;
            }
            command = command.Remove(0, configData.chatSettings.customKillCommandPrefix.Length);
            HandleKillCmd(player, command);
        }

        #endregion Custom Kill Command

        #region Help Command

        private void CmdLicenseHelp(BasePlayer player, string command, string[] args)
        {
            StringBuilder stringBuilder = Pool.Get<StringBuilder>();
            stringBuilder.AppendLine(Lang("Help", player.UserIDString));
            stringBuilder.AppendLine(Lang("HelpLicence1", player.UserIDString, configData.chatSettings.buyCommand));
            stringBuilder.AppendLine(Lang("HelpLicence2", player.UserIDString, configData.chatSettings.spawnCommand));
            stringBuilder.AppendLine(Lang("HelpLicence3", player.UserIDString, configData.chatSettings.recallCommand));
            stringBuilder.AppendLine(Lang("HelpLicence4", player.UserIDString, configData.chatSettings.killCommand));

            foreach (var entry in allVehicleSettings)
            {
                if (CanViewVehicleInfo(player, entry.Key, entry.Value))
                {
                    if (configData.chatSettings.useUniversalCommand)
                    {
                        var firstCmd = entry.Value.Commands[0];
                        stringBuilder.AppendLine(Lang("HelpLicence5", player.UserIDString, firstCmd, entry.Value.DisplayName));
                    }
                    //if (!string.IsNullOrEmpty(configData.chatS.customKillCommandPrefix))
                    //{
                    //    stringBuilder.AppendLine(Lang("HelpLicence6", player.UserIDString, configData.chatS.customKillCommandPrefix + firstCmd, entry.Value.displayName));
                    //}
                }
            }
            Print(player, stringBuilder.ToString());
            stringBuilder.Clear();
            Pool.Free(ref stringBuilder);
        }

        #endregion Help Command

        #region Remove Command

        [ConsoleCommand("vl.remove")]
        private void CCmdRemoveVehicle(ConsoleSystem.Arg arg)
        {
            if (arg.IsAdmin && arg.Args != null && arg.Args.Length == 2)
            {
                var option = arg.Args[0];
                string vehicleType;
                if (!IsValidVehicleType(option, out vehicleType))
                {
                    Print(arg, $"{option} is not a valid vehicle type");
                    return;
                }
                switch (arg.Args[1].ToLower())
                {
                    case "*":
                    case "all":
                        {
                            storedData.RemoveLicenseForAllPlayers(vehicleType);
                            var vehicleName = GetBaseVehicleSettings(vehicleType).DisplayName;
                            Print(arg, $"You successfully removed the vehicle({vehicleName}) of all players");
                        }
                        return;

                    default:
                        {
                            var target = RustCore.FindPlayer(arg.Args[1]);
                            if (target == null)
                            {
                                Print(arg, $"Player '{arg.Args[1]}' not found");
                                return;
                            }

                            var vehicleName = GetBaseVehicleSettings(vehicleType).DisplayName;
                            if (RemoveVehicleLicense(target.userID, vehicleType))
                            {
                                Print(arg, $"You successfully removed the vehicle({vehicleName}) of {target.displayName}");
                                return;
                            }

                            Print(arg, $"{target.displayName} has not purchased vehicle({vehicleName}) and cannot be removed");
                        }
                        return;
                }
            }
        }

        [ConsoleCommand("vl.cleardata")]
        private void CCmdClearVehicle(ConsoleSystem.Arg arg)
        {
            if (arg.IsAdmin)
            {
                foreach (var vehicle in vehiclesCache.Keys.ToArray())
                {
                    vehicle.Kill(BaseNetworkable.DestroyMode.Gib);
                }
                vehiclesCache.Clear();
                ClearData();
                Print(arg, "You successfully cleaned up all vehicle data");
            }
        }

        #endregion Remove Command

        #region Buy Command

        [ConsoleCommand("vl.buy")]
        private void CCmdBuyVehicle(ConsoleSystem.Arg arg)
        {
            if (arg.IsAdmin && arg.Args != null && arg.Args.Length == 2)
            {
                var option = arg.Args[0];
                string vehicleType;
                if (!IsValidVehicleType(option, out vehicleType))
                {
                    Print(arg, $"{option} is not a valid vehicle type");
                    return;
                }
                switch (arg.Args[1].ToLower())
                {
                    case "*":
                    case "all":
                        {
                            storedData.AddLicenseForAllPlayers(vehicleType);
                            var vehicleName = GetBaseVehicleSettings(vehicleType).DisplayName;
                            Print(arg, $"You successfully purchased the vehicle({vehicleName}) for all players");
                        }
                        return;

                    default:
                        {
                            var target = RustCore.FindPlayer(arg.Args[1]);
                            if (target == null)
                            {
                                Print(arg, $"Player '{arg.Args[1]}' not found");
                                return;
                            }

                            var vehicleName = GetBaseVehicleSettings(vehicleType).DisplayName;
                            if (AddVehicleLicense(target.userID, vehicleType))
                            {
                                Print(arg, $"You successfully purchased the vehicle({vehicleName}) for {target.displayName}");
                                return;
                            }

                            Print(arg, $"{target.displayName} has purchased vehicle({vehicleName})");
                        }
                        return;
                }
            }
            var player = arg.Player();
            if (player != null) CmdBuyVehicle(player, string.Empty, arg.Args);
            else Print(arg, $"The server console cannot use the '{arg.cmd.FullName}' command");
        }

        private void CmdBuyVehicle(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
            {
                Print(player, Lang("NotAllowed", player.UserIDString));
                return;
            }
            if (args == null || args.Length < 1)
            {
                StringBuilder stringBuilder = Pool.Get<StringBuilder>();
                stringBuilder.AppendLine(Lang("Help", player.UserIDString));
                foreach (var entry in allVehicleSettings)
                {
                    if (CanViewVehicleInfo(player, entry.Key, entry.Value))
                    {
                        var firstCmd = entry.Value.Commands[0];
                        if (entry.Value.PurchasePrices.Count > 0)
                        {
                            var prices = FormatPriceInfo(player, entry.Value.PurchasePrices);
                            stringBuilder.AppendLine(Lang("HelpBuyPrice", player.UserIDString, configData.chatSettings.buyCommand, firstCmd, entry.Value.DisplayName, prices));
                        }
                        else
                        {
                            stringBuilder.AppendLine(Lang("HelpBuy", player.UserIDString, configData.chatSettings.buyCommand, firstCmd, entry.Value.DisplayName));
                        }
                    }
                }
                Print(player, stringBuilder.ToString());
                stringBuilder.Clear();
                Pool.Free(ref stringBuilder);
                return;
            }
            string vehicleType;
            if (IsValidOption(player, args[0], out vehicleType))
            {
                BuyVehicle(player, vehicleType);
            }
        }

        private bool BuyVehicle(BasePlayer player, string vehicleType)
        {
            var baseVehicleSettings = GetBaseVehicleSettings(vehicleType);
            if (!baseVehicleSettings.Purchasable)
            {
                Print(player, Lang("VehicleCannotBeBought", player.UserIDString, baseVehicleSettings.DisplayName));
                return false;
            }
            var vehicles = storedData.GetPlayerVehicles(player.userID, false);
            if (vehicles.ContainsKey(vehicleType))
            {
                Print(player, Lang("VehicleAlreadyPurchased", player.UserIDString, baseVehicleSettings.DisplayName));
                return false;
            }
            string missingResources;
            if (baseVehicleSettings.PurchasePrices.Count > 0 && !TryPay(player, baseVehicleSettings.PurchasePrices, out missingResources))
            {
                Print(player, Lang("NoResourcesToPurchaseVehicle", player.UserIDString, baseVehicleSettings.DisplayName, missingResources));
                return false;
            }
            vehicles.Add(vehicleType, new Vehicle());
            SaveData();
            Print(player, Lang("VehiclePurchased", player.UserIDString, baseVehicleSettings.DisplayName, configData.chatSettings.spawnCommand));
            return true;
        }

        #endregion Buy Command

        #region Spawn Command

        [ConsoleCommand("vl.spawn")]
        private void CCmdSpawnVehicle(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) Print(arg, $"The server console cannot use the '{arg.cmd.FullName}' command");
            else CmdSpawnVehicle(player, string.Empty, arg.Args);
        }

        private void CmdSpawnVehicle(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
            {
                Print(player, Lang("NotAllowed", player.UserIDString));
                return;
            }
            if (args == null || args.Length < 1)
            {
                StringBuilder stringBuilder = Pool.Get<StringBuilder>();
                stringBuilder.AppendLine(Lang("Help", player.UserIDString));
                foreach (var entry in allVehicleSettings)
                {
                    if (CanViewVehicleInfo(player, entry.Key, entry.Value))
                    {
                        var firstCmd = entry.Value.Commands[0];
                        if (entry.Value.SpawnPrices.Count > 0)
                        {
                            var prices = FormatPriceInfo(player, entry.Value.SpawnPrices);
                            stringBuilder.AppendLine(Lang("HelpSpawnPrice", player.UserIDString, configData.chatSettings.spawnCommand, firstCmd, entry.Value.DisplayName, prices));
                        }
                        else
                        {
                            stringBuilder.AppendLine(Lang("HelpSpawn", player.UserIDString, configData.chatSettings.spawnCommand, firstCmd, entry.Value.DisplayName));
                        }
                    }
                }
                Print(player, stringBuilder.ToString());
                stringBuilder.Clear();
                Pool.Free(ref stringBuilder);
                return;
            }
            string vehicleType;
            if (IsValidOption(player, args[0], out vehicleType))
            {
                var bypassCooldown = args.Length > 1 && IsValidBypassCooldownOption(args[1]);
                SpawnVehicle(player, vehicleType, bypassCooldown, command + " " + args[0]);
            }
        }

        private bool SpawnVehicle(BasePlayer player, string vehicleType, bool bypassCooldown, string command)
        {
            var baseVehicleSettings = GetBaseVehicleSettings(vehicleType);
            Vehicle vehicle;
            if (!storedData.IsVehiclePurchased(player.userID, vehicleType, out vehicle))
            {
                Print(player, Lang("VehicleNotYetPurchased", player.UserIDString, baseVehicleSettings.DisplayName, configData.chatSettings.buyCommand));
                return false;
            }
            if (vehicle.Entity != null && !vehicle.Entity.IsDestroyed)
            {
                Print(player, Lang("AlreadyVehicleOut", player.UserIDString, baseVehicleSettings.DisplayName, configData.chatSettings.recallCommand));
                return false;
            }
            string reason; Vector3 position = Vector3.zero; Quaternion rotation = Quaternion.identity;
            if (CanSpawn(player, vehicle, bypassCooldown, out reason, ref position, ref rotation, command))
            {
                SpawnVehicle(player, vehicle, position, rotation);
                return false;
            }
            Print(player, reason);
            return true;
        }

        private bool CanSpawn(BasePlayer player, Vehicle vehicle, bool bypassCooldown, out string reason, ref Vector3 position, ref Quaternion rotation, string command = "")
        {
            var baseVehicleSettings = GetBaseVehicleSettings(vehicle.VehicleType);
            BaseEntity randomVehicle = null;
            if (configData.globalSettings.limitVehicles > 0)
            {
                var activeVehicles = storedData.ActiveVehicles(player.userID);
                int count = activeVehicles.Count();
                if (count >= configData.globalSettings.limitVehicles)
                {
                    if (configData.globalSettings.killVehicleLimited)
                    {
                        randomVehicle = activeVehicles.ElementAt(UnityEngine.Random.Range(0, count));
                    }
                    else
                    {
                        reason = Lang("VehiclesLimit", player.UserIDString, configData.globalSettings.limitVehicles);
                        return false;
                    }
                }
            }
            if (!CanPlayerAction(player, vehicle.VehicleType, baseVehicleSettings, out reason, ref position, ref rotation))
            {
                return false;
            }
            var obj = Interface.CallHook("CanLicensedVehicleSpawn", player, vehicle.VehicleType, position, rotation);
            if (obj != null)
            {
                var s = obj as string;
                reason = s ?? Lang("SpawnWasBlocked", player.UserIDString, baseVehicleSettings.DisplayName);
                return false;
            }

#if DEBUG
            if (player.IsAdmin)
            {
                reason = null;
                return true;
            }
#endif
            if (!CheckCooldown(player, vehicle, baseVehicleSettings, bypassCooldown, out reason, true, command))
            {
                return false;
            }

            string missingResources;
            if (baseVehicleSettings.SpawnPrices.Count > 0 && !TryPay(player, baseVehicleSettings.SpawnPrices, out missingResources))
            {
                reason = Lang("NoResourcesToSpawnVehicle", player.UserIDString, baseVehicleSettings.DisplayName, missingResources);
                return false;
            }

            if (randomVehicle != null)
            {
                randomVehicle.Kill(BaseNetworkable.DestroyMode.Gib);
            }
            reason = null;
            return true;
        }

        private void SpawnVehicle(BasePlayer player, Vehicle vehicle, Vector3 position, Quaternion rotation)
        {
            var baseVehicleSettings = GetBaseVehicleSettings(vehicle.VehicleType);
            var prefab = GetVehiclePrefab(vehicle.VehicleType, baseVehicleSettings);
            if (string.IsNullOrEmpty(prefab))
            {
                return;
            }
            var entity = GameManager.server.CreateEntity(prefab, position, rotation);
            if (entity == null)
            {
                return;
            }
            entity.enableSaving = configData.globalSettings.storeVehicle;
            entity.OwnerID = player.userID;
            entity.Spawn();
            if (!entity.IsDestroyed)
            {
                SetupVehicleEntity(entity, vehicle, player);
            }
            else
            {
                Print(player, Lang("NotSpawnedOrRecalled", player.UserIDString, baseVehicleSettings.DisplayName));
                return;
            }

            Interface.CallHook("OnLicensedVehicleSpawned", entity, player, vehicle.VehicleType);
            Print(player, Lang("VehicleSpawned", player.UserIDString, baseVehicleSettings.DisplayName));
        }

        private void SetupVehicleEntity(BaseEntity entity, Vehicle vehicle, BasePlayer player, bool giveFuel = true)
        {
            var baseVehicleSettings = GetBaseVehicleSettings(vehicle.VehicleType);
            baseVehicleSettings.SetupVehicle(entity, vehicle, player, giveFuel);

            vehicle.PlayerId = player.userID;
            vehicle.VehicleType = vehicle.VehicleType;
            vehicle.Entity = entity;
            vehicle.EntityId = entity.net.ID;
            vehicle.LastDismount = vehicle.LastRecall = TimeEx.currentTimestamp;
            vehiclesCache.Add(entity, vehicle);
        }

        #endregion Spawn Command

        #region Recall Command

        [ConsoleCommand("vl.recall")]
        private void CCmdRecallVehicle(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) Print(arg, $"The server console cannot use the '{arg.cmd.FullName}' command");
            else CmdRecallVehicle(player, string.Empty, arg.Args);
        }

        private void CmdRecallVehicle(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
            {
                Print(player, Lang("NotAllowed", player.UserIDString));
                return;
            }
            if (args == null || args.Length < 1)
            {
                StringBuilder stringBuilder = Pool.Get<StringBuilder>();
                stringBuilder.AppendLine(Lang("Help", player.UserIDString));
                foreach (var entry in allVehicleSettings)
                {
                    if (CanViewVehicleInfo(player, entry.Key, entry.Value))
                    {
                        var firstCmd = entry.Value.Commands[0];
                        if (entry.Value.RecallPrices.Count > 0)
                        {
                            var prices = FormatPriceInfo(player, entry.Value.RecallPrices);
                            stringBuilder.AppendLine(Lang("HelpRecallPrice", player.UserIDString, configData.chatSettings.recallCommand, firstCmd, entry.Value.DisplayName, prices));
                        }
                        else
                        {
                            stringBuilder.AppendLine(Lang("HelpRecall", player.UserIDString, configData.chatSettings.recallCommand, firstCmd, entry.Value.DisplayName));
                        }
                    }
                }
                Print(player, stringBuilder.ToString());
                stringBuilder.Clear();
                Pool.Free(ref stringBuilder);
                return;
            }
            string vehicleType;
            if (IsValidOption(player, args[0], out vehicleType))
            {
                var bypassCooldown = args.Length > 1 && IsValidBypassCooldownOption(args[1]);
                RecallVehicle(player, vehicleType, bypassCooldown, command + " " + args[0]);
            }
        }

        private bool RecallVehicle(BasePlayer player, string vehicleType, bool bypassCooldown, string command)
        {
            var baseVehicleSettings = GetBaseVehicleSettings(vehicleType);
            Vehicle vehicle;
            if (!storedData.IsVehiclePurchased(player.userID, vehicleType, out vehicle))
            {
                Print(player, Lang("VehicleNotYetPurchased", player.UserIDString, baseVehicleSettings.DisplayName, configData.chatSettings.buyCommand));
                return false;
            }
            if (vehicle.Entity != null && !vehicle.Entity.IsDestroyed)
            {
                string reason; Vector3 position = Vector3.zero; Quaternion rotation = Quaternion.identity;
                if (CanRecall(player, vehicle, bypassCooldown, out reason, ref position, ref rotation, command))
                {
                    RecallVehicle(player, vehicle, position, rotation);
                    return true;
                }
                Print(player, reason);
                return false;
            }
            Print(player, Lang("VehicleNotOut", player.UserIDString, baseVehicleSettings.DisplayName, configData.chatSettings.spawnCommand));
            return false;
        }

        private bool CanRecall(BasePlayer player, Vehicle vehicle, bool bypassCooldown, out string reason, ref Vector3 position, ref Quaternion rotation, string command = "")
        {
            var baseVehicleSettings = GetBaseVehicleSettings(vehicle.VehicleType);
            if (baseVehicleSettings.RecallMaxDistance > 0 && Vector3.Distance(player.transform.position, vehicle.Entity.transform.position) > baseVehicleSettings.RecallMaxDistance)
            {
                reason = Lang("RecallTooFar", player.UserIDString, baseVehicleSettings.RecallMaxDistance, baseVehicleSettings.DisplayName);
                return false;
            }
            if (configData.globalSettings.anyMountedRecall && VehicleAnyMounted(vehicle.Entity))
            {
                reason = Lang("PlayerMountedOnVehicle", player.UserIDString, baseVehicleSettings.DisplayName);
                return false;
            }
            if (!CanPlayerAction(player, vehicle.VehicleType, baseVehicleSettings, out reason, ref position, ref rotation))
            {
                return false;
            }

            var obj = Interface.CallHook("CanLicensedVehicleRecall", vehicle.Entity, player, vehicle.VehicleType, position, rotation);
            if (obj != null)
            {
                var s = obj as string;
                reason = s ?? Lang("RecallWasBlocked", player.UserIDString, baseVehicleSettings.DisplayName);
                return false;
            }
#if DEBUG
            if (player.IsAdmin)
            {
                reason = null;
                return true;
            }
#endif
            if (!CheckCooldown(player, vehicle, baseVehicleSettings, bypassCooldown, out reason, false, command))
            {
                return false;
            }
            string missingResources;
            if (baseVehicleSettings.RecallPrices.Count > 0 && !TryPay(player, baseVehicleSettings.RecallPrices, out missingResources))
            {
                reason = Lang("NoResourcesToRecallVehicle", player.UserIDString, baseVehicleSettings.DisplayName, missingResources);
                return false;
            }
            reason = null;
            return true;
        }

        private void RecallVehicle(BasePlayer player, Vehicle vehicle, Vector3 position, Quaternion rotation)
        {
            var baseVehicleSettings = GetBaseVehicleSettings(vehicle.VehicleType);

            baseVehicleSettings.PreRecallVehicle(player, vehicle, position, rotation);

            vehicle.OnRecall();
            vehicle.Entity.transform.SetPositionAndRotation(position, rotation);
            vehicle.Entity.transform.hasChanged = true;

            baseVehicleSettings.PostRecallVehicle(player, vehicle, position, rotation);

            if (vehicle.Entity == null || vehicle.Entity.IsDestroyed)
            {
                Print(player, Lang("NotSpawnedOrRecalled", player.UserIDString, baseVehicleSettings.DisplayName));
                return;
            }

            Interface.CallHook("OnLicensedVehicleRecalled", vehicle.Entity, player, vehicle.VehicleType);
            Print(player, Lang("VehicleRecalled", player.UserIDString, baseVehicleSettings.DisplayName));
        }

        #endregion Recall Command

        #region Kill Command

        [ConsoleCommand("vl.kill")]
        private void CCmdKillVehicle(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) Print(arg, $"The server console cannot use the '{arg.cmd.FullName}' command");
            else CmdKillVehicle(player, string.Empty, arg.Args);
        }

        private void CmdKillVehicle(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
            {
                Print(player, Lang("NotAllowed", player.UserIDString));
                return;
            }
            if (args == null || args.Length < 1)
            {
                StringBuilder stringBuilder = Pool.Get<StringBuilder>();
                stringBuilder.AppendLine(Lang("Help", player.UserIDString));
                foreach (var entry in allVehicleSettings)
                {
                    if (CanViewVehicleInfo(player, entry.Key, entry.Value))
                    {
                        var firstCmd = entry.Value.Commands[0];
                        if (!string.IsNullOrEmpty(configData.chatSettings.customKillCommandPrefix))
                        {
                            stringBuilder.AppendLine(Lang("HelpKillCustom", player.UserIDString, configData.chatSettings.killCommand, firstCmd, configData.chatSettings.customKillCommandPrefix + firstCmd, entry.Value.DisplayName));
                        }
                        else
                        {
                            stringBuilder.AppendLine(Lang("HelpKill", player.UserIDString, configData.chatSettings.killCommand, firstCmd, entry.Value.DisplayName));
                        }
                    }
                }
                Print(player, stringBuilder.ToString());
                stringBuilder.Clear();
                Pool.Free(ref stringBuilder);
                return;
            }

            HandleKillCmd(player, args[0]);
        }

        private void HandleKillCmd(BasePlayer player, string option)
        {
            string vehicleType;
            if (IsValidOption(player, option, out vehicleType))
            {
                KillVehicle(player, vehicleType);
            }
        }

        private bool KillVehicle(BasePlayer player, string vehicleType)
        {
            var baseVehicleSettings = GetBaseVehicleSettings(vehicleType);
            Vehicle vehicle;
            if (!storedData.IsVehiclePurchased(player.userID, vehicleType, out vehicle))
            {
                Print(player, Lang("VehicleNotYetPurchased", player.UserIDString, baseVehicleSettings.DisplayName, configData.chatSettings.buyCommand));
                return false;
            }
            if (vehicle.Entity != null && !vehicle.Entity.IsDestroyed)
            {
                if (!CanKill(player, vehicle, baseVehicleSettings))
                {
                    return false;
                }
                vehicle.Entity.Kill(BaseNetworkable.DestroyMode.Gib);
                Print(player, Lang("VehicleKilled", player.UserIDString, baseVehicleSettings.DisplayName));
                return true;
            }
            Print(player, Lang("VehicleNotOut", player.UserIDString, baseVehicleSettings.DisplayName, configData.chatSettings.spawnCommand));
            return false;
        }

        private bool CanKill(BasePlayer player, Vehicle vehicle, BaseVehicleSettings baseVehicleSettings)
        {
            if (configData.globalSettings.anyMountedKill && VehicleAnyMounted(vehicle.Entity))
            {
                Print(player, Lang("PlayerMountedOnVehicle", player.UserIDString, baseVehicleSettings.DisplayName));
                return false;
            }
            if (baseVehicleSettings.KillMaxDistance > 0 && Vector3.Distance(player.transform.position, vehicle.Entity.transform.position) > baseVehicleSettings.KillMaxDistance)
            {
                Print(player, Lang("KillTooFar", player.UserIDString, baseVehicleSettings.KillMaxDistance, baseVehicleSettings.DisplayName));
                return false;
            }

            return true;
        }

        #endregion Kill Command

        #region Command Helpers

        private bool IsValidBypassCooldownOption(string option)
        {
            return !string.IsNullOrEmpty(configData.chatSettings.bypassCooldownCommand) && string.Equals(option, configData.chatSettings.bypassCooldownCommand, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsValidOption(BasePlayer player, string option, out string vehicleType)
        {
            if (!commandToVehicleType.TryGetValue(option, out vehicleType))
            {
                Print(player, Lang("OptionNotFound", player.UserIDString, option));
                return false;
            }
            if (!HasVehiclePermission(player, vehicleType))
            {
                Print(player, Lang("NotAllowed", player.UserIDString));
                vehicleType = null;
                return false;
            }
            if (IsPlayerBlocked(player))
            {
                vehicleType = null;
                return false;
            }
            return true;
        }

        private bool IsValidVehicleType(string option, out string vehicleType)
        {
            foreach (var entry in allVehicleSettings)
            {
                if (string.Equals(entry.Key, option, StringComparison.OrdinalIgnoreCase))
                {
                    vehicleType = entry.Key;
                    return true;
                }
            }

            vehicleType = null;
            return false;
        }

        private string FormatPriceInfo(BasePlayer player, Dictionary<string, PriceInfo> prices)
        {
            var language = RustTranslationAPI != null ? lang.GetLanguage(player.UserIDString) : null;
            return string.Join(", ",
                from p in prices
                select Lang("PriceFormat", player.UserIDString, GetItemDisplayName(language, p.Key, p.Value.displayName), p.Value.amount));
        }

        private bool CanPlayerAction(BasePlayer player, string vehicleType, BaseVehicleSettings baseVehicleSettings, out string reason, ref Vector3 position, ref Quaternion rotation, BaseEntity entity = null)
        {
            if (configData.globalSettings.preventBuildingBlocked && player.IsBuildingBlocked())
            {
                reason = Lang("BuildingBlocked", player.UserIDString, baseVehicleSettings.DisplayName);
                return false;
            }
            if (configData.globalSettings.preventSafeZone && player.InSafeZone())
            {
                reason = Lang("PlayerInSafeZone", player.UserIDString, baseVehicleSettings.DisplayName);
                return false;
            }
            if (configData.globalSettings.preventMountedOrParented && HasMountedOrParented(player, vehicleType))
            {
                reason = Lang("MountedOrParented", player.UserIDString, baseVehicleSettings.DisplayName);
                return false;
            }
            Vector3 lookingAt = Vector3.zero;
            var isWorkCart = vehicleType == nameof(NormalVehicleType.WorkCart);
            var checkWater = NeedCheckWater(vehicleType);
            if (!CheckPosition(player, baseVehicleSettings, checkWater, isWorkCart, out reason, ref lookingAt, entity))
            {
                return false;
            }
            FindVehicleSpawnPositionAndRotation(player, baseVehicleSettings, checkWater, isWorkCart, vehicleType, lookingAt, out position, out rotation);
            reason = null;
            return true;
        }

        private bool HasMountedOrParented(BasePlayer player, string vehicleType)
        {
            if (player.GetMountedVehicle() != null) return true;
            var parentEntity = player.GetParentEntity();
            if (parentEntity != null)
            {
                if (configData.globalSettings.spawnLookingAt && LandOnCargoShip != null && parentEntity is CargoShip &&
                    (vehicleType == nameof(NormalVehicleType.MiniCopter) ||
                     vehicleType == nameof(NormalVehicleType.TransportHelicopter)))
                {
                    return false;
                }
                return true;
            }
            return false;
        }

        private bool CheckCooldown(BasePlayer player, Vehicle vehicle, BaseVehicleSettings baseVehicleSettings, bool bypassCooldown, out string reason, bool isSpawnCooldown, string command = "")
        {
            var cooldown = isSpawnCooldown ? GetSpawnCooldown(player, baseVehicleSettings) : GetRecallCooldown(player, baseVehicleSettings);
            if (cooldown > 0)
            {
                var timeLeft = Math.Ceiling(cooldown - (TimeEx.currentTimestamp - (isSpawnCooldown ? vehicle.LastDeath : vehicle.LastRecall)));
                if (timeLeft > 0)
                {
                    var bypassPrices = isSpawnCooldown
                        ? baseVehicleSettings.BypassSpawnCooldownPrices
                        : baseVehicleSettings.BypassRecallCooldownPrices;
                    if (bypassCooldown && bypassPrices.Count > 0)
                    {
                        string missingResources;
                        if (!TryPay(player, bypassPrices, out missingResources))
                        {
                            reason = Lang(isSpawnCooldown ? "NoResourcesToSpawnVehicleBypass" : "NoResourcesToRecallVehicleBypass", player.UserIDString, baseVehicleSettings.DisplayName, missingResources);
                            return false;
                        }

                        if (isSpawnCooldown) vehicle.LastDeath = 0;
                        else vehicle.LastRecall = 0;
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(configData.chatSettings.bypassCooldownCommand) || bypassPrices.Count <= 0)
                        {
                            reason = Lang(isSpawnCooldown ? "VehicleOnSpawnCooldown" : "VehicleOnRecallCooldown", player.UserIDString, timeLeft, baseVehicleSettings.DisplayName);
                        }
                        else
                        {
                            reason = Lang(isSpawnCooldown ? "VehicleOnSpawnCooldownPay" : "VehicleOnRecallCooldownPay",
                                player.UserIDString, timeLeft, baseVehicleSettings.DisplayName,
                                command + " " + configData.chatSettings.bypassCooldownCommand,
                                FormatPriceInfo(player,
                                    isSpawnCooldown
                                        ? baseVehicleSettings.BypassSpawnCooldownPrices
                                        : baseVehicleSettings.BypassRecallCooldownPrices));
                        }
                        return false;
                    }
                }
            }
            reason = null;
            return true;
        }

        private bool CheckPosition(BasePlayer player, BaseVehicleSettings baseVehicleSettings, bool checkWater, bool isWorkCart, out string reason, ref Vector3 lookingAt, BaseEntity entity = null)
        {
            if (checkWater || configData.globalSettings.spawnLookingAt || isWorkCart)
            {
                lookingAt = GetGroundPositionLookingAt(player, baseVehicleSettings.Distance, !isWorkCart);
                if (checkWater && !IsInWater(lookingAt))
                {
                    reason = Lang("NotLookingAtWater", player.UserIDString, baseVehicleSettings.DisplayName);
                    return false;
                }
                if (configData.globalSettings.spawnLookingAt && baseVehicleSettings.MinDistanceForPlayers > 0)
                {
                    var nearbyPlayers = Pool.GetList<BasePlayer>();
                    Vis.Entities(lookingAt, baseVehicleSettings.MinDistanceForPlayers, nearbyPlayers, Rust.Layers.Mask.Player_Server);
                    bool flag = nearbyPlayers.Any(x => x.userID.IsSteamId() && x != player);
                    Pool.FreeList(ref nearbyPlayers);
                    if (flag)
                    {
                        reason = Lang("PlayersOnNearby", player.UserIDString, baseVehicleSettings.DisplayName);
                        return false;
                    }
                }

                if (isWorkCart)
                {
                    float distResult;
                    TrainTrackSpline splineResult;
                    if (!TrainTrackSpline.TryFindTrackNearby(lookingAt, baseVehicleSettings.Distance, out splineResult, out distResult))
                    {
                        reason = Lang("TooFarTrainTrack", player.UserIDString);
                        return false;
                    }
                    //splineResult.HasClearTrackSpaceNear(entity as TrainEngine)
                    var position = splineResult.GetPosition(distResult);
                    if (!HasClearTrackSpaceNear(splineResult, position, entity as TrainTrackSpline.ITrainTrackUser))
                    {
                        reason = Lang("TooCloseTrainBarricadeOrWorkCart", player.UserIDString);
                        return false;
                    }
                    lookingAt = position;
                    reason = null;
                    return true;
                }
            }
            reason = null;
            return true;
        }

        private void FindVehicleSpawnPositionAndRotation(BasePlayer player, BaseVehicleSettings baseVehicleSettings, bool checkWater, bool isWorkCart, string vehicleType, Vector3 lookingAt, out Vector3 spawnPos, out Quaternion spawnRot)
        {
            if (isWorkCart)
            {
                spawnPos = lookingAt;
                var rotation = player.eyes.HeadForward().WithY(0);
                spawnRot = rotation != Vector3.zero ? Quaternion.LookRotation(rotation) : Quaternion.identity;
                return;
            }
            if (configData.globalSettings.spawnLookingAt)
            {
                bool needGetGround = true;
                spawnPos = lookingAt == Vector3.zero ? GetGroundPositionLookingAt(player, baseVehicleSettings.Distance) : lookingAt;
                if (checkWater)
                {
                    RaycastHit hit;
                    if (Physics.Raycast(spawnPos, Vector3.up, out hit, 100, LAYER_GROUND) && hit.GetEntity() is StabilityEntity)
                    {
                        //At the dock
                        needGetGround = false;
                    }
                }
                else
                {
                    var buildingBlocks = Pool.GetList<BuildingBlock>();
                    Vis.Entities(spawnPos, 2f, buildingBlocks, Rust.Layers.Mask.Construction);
                    if (buildingBlocks.Count > 0)
                    {
                        var pos = spawnPos;
                        var closestBuildingBlock = buildingBlocks
                            .Where(x => !x.ShortPrefabName.Contains("wall"))
                            .OrderBy(x => (x.transform.position - pos).magnitude).FirstOrDefault();
                        if (closestBuildingBlock != null)
                        {
                            var worldSpaceBounds = closestBuildingBlock.WorldSpaceBounds();
                            spawnPos = worldSpaceBounds.position;
                            spawnPos.y += worldSpaceBounds.extents.y;
                            needGetGround = false;
                        }
                    }
                    Pool.FreeList(ref buildingBlocks);
                }
                if (needGetGround)
                {
                    spawnPos = GetGroundPosition(spawnPos);
                }
            }
            else
            {
                var minDistance = Mathf.Min(baseVehicleSettings.MinDistanceForPlayers, 2.5f);
                var distance = Mathf.Max(baseVehicleSettings.Distance, minDistance);
                spawnPos = player.transform.position;
                var nearbyPlayers = Pool.GetList<BasePlayer>();
                var sourcePos = checkWater ? (lookingAt == Vector3.zero ? GetGroundPositionLookingAt(player, baseVehicleSettings.Distance) : lookingAt) : spawnPos;
                for (int i = 0; i < 100; i++)
                {
                    spawnPos.x = sourcePos.x + UnityEngine.Random.Range(minDistance, distance) * (UnityEngine.Random.value >= 0.5f ? 1 : -1);
                    spawnPos.z = sourcePos.z + UnityEngine.Random.Range(minDistance, distance) * (UnityEngine.Random.value >= 0.5f ? 1 : -1);
                    spawnPos = GetGroundPosition(spawnPos);
                    nearbyPlayers.Clear();
                    Vis.Entities(spawnPos, minDistance, nearbyPlayers, Rust.Layers.Mask.Player_Server);
                    if (!nearbyPlayers.Any(x => x.userID.IsSteamId()))
                    {
                        break;
                    }
                }
                Pool.FreeList(ref nearbyPlayers);
            }

            var normalized = (spawnPos - player.transform.position).normalized;
            var angle = normalized != Vector3.zero ? Quaternion.LookRotation(normalized).eulerAngles.y : UnityEngine.Random.Range(0f, 360f);
            var rot = vehicleType == nameof(NormalVehicleType.HotAirBalloon) ? 180f : 90f;
            spawnRot = Quaternion.Euler(Vector3.up * (angle + rot));
            if (vehicleType != nameof(NormalVehicleType.RidableHorse)) spawnPos += Vector3.up * 0.3f;
        }

        #region HasClearTrackSpace

        public bool HasClearTrackSpaceNear(TrainTrackSpline trainTrackSpline, Vector3 position, TrainTrackSpline.ITrainTrackUser asker)
        {
            if (!HasClearTrackSpace(trainTrackSpline, position, asker))
            {
                return false;
            }
            if (trainTrackSpline.HasNextTrack)
            {
                foreach (var nextTrack in trainTrackSpline.nextTracks)
                {
                    if (!HasClearTrackSpace(nextTrack.track, position, asker))
                    {
                        return false;
                    }
                }
            }
            if (trainTrackSpline.HasPrevTrack)
            {
                foreach (var prevTrack in trainTrackSpline.prevTracks)
                {
                    if (!HasClearTrackSpace(prevTrack.track, position, asker))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private bool HasClearTrackSpace(TrainTrackSpline trainTrackSpline, Vector3 position, TrainTrackSpline.ITrainTrackUser asker)
        {
            foreach (var trackUser in trainTrackSpline.trackUsers)
            {
                if (trackUser != asker && Vector3.SqrMagnitude(trackUser.Position - position) < 144f)
                {
                    return false;
                }
            }
            return true;
        }

        #endregion HasClearTrackSpace

        #endregion Command Helpers

        #endregion Commands

        #region RustTranslationAPI

        private string GetItemTranslationByShortName(string language, string itemShortName) => (string)RustTranslationAPI.Call("GetItemTranslationByShortName", language, itemShortName);

        private string GetItemDisplayName(string language, string itemShortName, string displayName)
        {
            if (RustTranslationAPI != null)
            {
                var displayName1 = GetItemTranslationByShortName(language, itemShortName);
                if (!string.IsNullOrEmpty(displayName1))
                {
                    return displayName1;
                }
            }
            return displayName;
        }

        #endregion RustTranslationAPI

        #region ConfigurationFile

        public ConfigData configData { get; private set; }

        public class ConfigData
        {
            [JsonProperty(PropertyName = "Settings")]
            public GlobalSettings globalSettings = new GlobalSettings();

            [JsonProperty(PropertyName = "Chat Settings")]
            public ChatSettings chatSettings = new ChatSettings();

            [JsonProperty(PropertyName = "Normal Vehicle Settings")]
            public NormalVehicleSettings normalVehicles = new NormalVehicleSettings();

            [JsonProperty(PropertyName = "Modular Vehicle Settings", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, ModularVehicleSettings> modularVehicles = new Dictionary<string, ModularVehicleSettings>
            {
                ["SmallCar"] = new ModularVehicleSettings
                {
                    Purchasable = true,
                    DisplayName = "Small Modular Car",
                    Distance = 5,
                    MinDistanceForPlayers = 3,
                    UsePermission = true,
                    Permission = "vehiclelicence.smallmodularcar",
                    Commands = new List<string> { "small", "smallcar" },
                    PurchasePrices = new Dictionary<string, PriceInfo>
                    {
                        ["scrap"] = new PriceInfo { amount = 1600, displayName = "Scrap" }
                    },
                    SpawnPrices = new Dictionary<string, PriceInfo>
                    {
                        ["metal.refined"] = new PriceInfo { amount = 10, displayName = "High Quality Metal" }
                    },
                    RecallPrices = new Dictionary<string, PriceInfo>
                    {
                        ["scrap"] = new PriceInfo { amount = 5, displayName = "Scrap" }
                    },
                    SpawnCooldown = 7200,
                    RecallCooldown = 30,
                    CooldownPermissions = new Dictionary<string, CooldownPermission>
                    {
                        ["vehiclelicence.vip"] = new CooldownPermission
                        {
                            spawnCooldown = 3600,
                            recallCooldown = 10,
                        }
                    },
                    ChassisType = ChassisType.Small,
                    ModuleItems = new List<ModuleItem>
                    {
                        new ModuleItem
                        {
                            shortName = "vehicle.1mod.cockpit.with.engine" ,healthPercentage = 50f
                        },
                        new ModuleItem
                        {
                            shortName = "vehicle.1mod.storage" ,healthPercentage = 50f
                        },
                    },
                    EngineItems = new List<EngineItem>
                    {
                        new EngineItem
                        {
                            shortName = "carburetor1",conditionPercentage = 20f
                        },
                        new EngineItem
                        {
                            shortName = "crankshaft1",conditionPercentage = 20f
                        },
                        new EngineItem
                        {
                            shortName = "piston1",conditionPercentage = 20f
                        },
                        new EngineItem
                        {
                            shortName = "sparkplug1",conditionPercentage = 20f
                        },
                        new EngineItem
                        {
                            shortName = "valve1",conditionPercentage = 20f
                        }
                    }
                },
                ["MediumCar"] = new ModularVehicleSettings
                {
                    Purchasable = true,
                    DisplayName = "Medium Modular Car",
                    Distance = 5,
                    MinDistanceForPlayers = 3,
                    UsePermission = true,
                    Permission = "vehiclelicence.mediumodularcar",
                    Commands = new List<string> { "medium", "mediumcar" },
                    PurchasePrices = new Dictionary<string, PriceInfo>
                    {
                        ["scrap"] = new PriceInfo { amount = 2400, displayName = "Scrap" }
                    },
                    SpawnPrices = new Dictionary<string, PriceInfo>
                    {
                        ["metal.refined"] = new PriceInfo { amount = 50, displayName = "High Quality Metal" }
                    },
                    RecallPrices = new Dictionary<string, PriceInfo>
                    {
                        ["scrap"] = new PriceInfo { amount = 8, displayName = "Scrap" }
                    },
                    SpawnCooldown = 9000,
                    RecallCooldown = 30,
                    CooldownPermissions = new Dictionary<string, CooldownPermission>
                    {
                        ["vehiclelicence.vip"] = new CooldownPermission
                        {
                            spawnCooldown = 4500,
                            recallCooldown = 10,
                        }
                    },
                    ChassisType = ChassisType.Medium,
                    ModuleItems = new List<ModuleItem>
                    {
                        new ModuleItem
                        {
                            shortName = "vehicle.1mod.cockpit.with.engine" ,healthPercentage = 50f
                        },
                        new ModuleItem
                        {
                            shortName = "vehicle.1mod.rear.seats" ,healthPercentage = 50f
                        },
                        new ModuleItem
                        {
                            shortName = "vehicle.1mod.flatbed" ,healthPercentage = 50f
                        },
                    },
                    EngineItems = new List<EngineItem>
                    {
                        new EngineItem
                        {
                            shortName = "carburetor2",conditionPercentage = 20f
                        },
                        new EngineItem
                        {
                            shortName = "crankshaft2",conditionPercentage = 20f
                        },
                        new EngineItem
                        {
                            shortName = "piston2",conditionPercentage = 20f
                        },
                        new EngineItem
                        {
                            shortName = "sparkplug2",conditionPercentage = 20f
                        },
                        new EngineItem
                        {
                            shortName = "valve2",conditionPercentage = 20f
                        }
                    }
                },
                ["LargeCar"] = new ModularVehicleSettings
                {
                    Purchasable = true,
                    DisplayName = "Large Modular Car",
                    Distance = 6,
                    MinDistanceForPlayers = 3,
                    UsePermission = true,
                    Permission = "vehiclelicence.largemodularcar",
                    Commands = new List<string> { "large", "largecar" },
                    PurchasePrices = new Dictionary<string, PriceInfo>
                    {
                        ["scrap"] = new PriceInfo { amount = 3000, displayName = "Scrap" }
                    },
                    SpawnPrices = new Dictionary<string, PriceInfo>
                    {
                        ["metal.refined"] = new PriceInfo { amount = 100, displayName = "High Quality Metal" }
                    },
                    RecallPrices = new Dictionary<string, PriceInfo>
                    {
                        ["scrap"] = new PriceInfo { amount = 10, displayName = "Scrap" }
                    },
                    SpawnCooldown = 10800,
                    RecallCooldown = 30,
                    CooldownPermissions = new Dictionary<string, CooldownPermission>
                    {
                        ["vehiclelicence.vip"] = new CooldownPermission
                        {
                            spawnCooldown = 5400,
                            recallCooldown = 10,
                        }
                    },
                    ChassisType = ChassisType.Large,
                    ModuleItems = new List<ModuleItem>
                    {
                        new ModuleItem
                        {
                            shortName = "vehicle.1mod.engine",healthPercentage = 50f
                        },
                        new ModuleItem
                        {
                            shortName = "vehicle.1mod.cockpit.armored",healthPercentage = 50f
                        },
                        new ModuleItem
                        {
                            shortName = "vehicle.1mod.passengers.armored",healthPercentage = 50f
                        },
                        new ModuleItem
                        {
                            shortName = "vehicle.1mod.storage",healthPercentage = 50f
                        },
                    },
                    EngineItems = new List<EngineItem>
                    {
                        new EngineItem
                        {
                            shortName = "carburetor3",conditionPercentage = 10f
                        },
                        new EngineItem
                        {
                            shortName = "crankshaft3",conditionPercentage = 10f
                        },
                        new EngineItem
                        {
                            shortName = "piston3",conditionPercentage = 10f
                        },
                        new EngineItem
                        {
                            shortName = "piston3",conditionPercentage = 10f
                        },
                        new EngineItem
                        {
                            shortName = "sparkplug3",conditionPercentage = 10f
                        },
                        new EngineItem
                        {
                            shortName = "sparkplug3",conditionPercentage = 10f
                        },
                        new EngineItem
                        {
                            shortName = "valve3",conditionPercentage = 10f
                        },
                        new EngineItem
                        {
                            shortName = "valve3",conditionPercentage = 10f
                        }
                    }
                },
            };

            [JsonProperty(PropertyName = "Train Vehicle Settings", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, TrainVehicleSettings> trainVehicles = new Dictionary<string, TrainVehicleSettings>
            {
                ["TrainEngine"] = new TrainVehicleSettings
                {
                    Purchasable = true,
                    DisplayName = "Train Engine",
                    Distance = 12,
                    MinDistanceForPlayers = 6,
                    UsePermission = true,
                    Permission = "vehiclelicence.trainengine",
                    Commands = new List<string>
                    {
                        "train", "trainengine"
                    },
                    PurchasePrices = new Dictionary<string, PriceInfo>
                    {
                        ["scrap"] = new PriceInfo { amount = 2000, displayName = "Scrap" }
                    },
                    SpawnCooldown = 1800,
                    RecallCooldown = 30,
                    CooldownPermissions = new Dictionary<string, CooldownPermission>
                    {
                        ["vehiclelicence.vip"] = new CooldownPermission
                        {
                            spawnCooldown = 900,
                            recallCooldown = 10,
                        }
                    },
                    TrainComponents = new List<TrainComponent>
                    {
                        new TrainComponent(){ type = TrainComponentType.Engine },
                    }
                },
                ["CompleteTrain"] = new TrainVehicleSettings
                {
                    Purchasable = true,
                    DisplayName = "Complete Train",
                    Distance = 12,
                    MinDistanceForPlayers = 6,
                    UsePermission = true,
                    Permission = "vehiclelicence.completetrain",
                    Commands = new List<string>
                    {
                        "completetrain"
                    },
                    PurchasePrices = new Dictionary<string, PriceInfo>
                    {
                        ["scrap"] = new PriceInfo { amount = 6000, displayName = "Scrap" }
                    },
                    SpawnCooldown = 3600,
                    RecallCooldown = 60,
                    CooldownPermissions = new Dictionary<string, CooldownPermission>
                    {
                        ["vehiclelicence.vip"] = new CooldownPermission
                        {
                            spawnCooldown = 900,
                            recallCooldown = 10,
                        }
                    },
                    TrainComponents = new List<TrainComponent>
                    {
                        new TrainComponent
                        {
                            type = TrainComponentType.Engine
                        },
                        new TrainComponent
                        {
                            type = TrainComponentType.A
                        },
                        new TrainComponent
                        {
                            type = TrainComponentType.B
                        },
                        new TrainComponent
                        {
                            type = TrainComponentType.C
                        },
                        new TrainComponent
                        {
                            type = TrainComponentType.D
                        },
                    }
                },
            };

            [JsonProperty(PropertyName = "Version")]
            public VersionNumber version;
        }

        public class ChatSettings
        {
            [JsonProperty(PropertyName = "Use Universal Chat Command")]
            public bool useUniversalCommand = true;

            [JsonProperty(PropertyName = "Help Chat Command")]
            public string helpCommand = "license";

            [JsonProperty(PropertyName = "Buy Chat Command")]
            public string buyCommand = "buy";

            [JsonProperty(PropertyName = "Spawn Chat Command")]
            public string spawnCommand = "spawn";

            [JsonProperty(PropertyName = "Recall Chat Command")]
            public string recallCommand = "recall";

            [JsonProperty(PropertyName = "Kill Chat Command")]
            public string killCommand = "kill";

            [JsonProperty(PropertyName = "Custom Kill Chat Command Prefix")]
            public string customKillCommandPrefix = "no";

            [JsonProperty(PropertyName = "Bypass Cooldown Command")]
            public string bypassCooldownCommand = "pay";

            [JsonProperty(PropertyName = "Chat Prefix")]
            public string prefix = "<color=#00FFFF>[VehicleLicense]</color>: ";

            [JsonProperty(PropertyName = "Chat SteamID Icon")]
            public ulong steamIDIcon = 76561198924840872;
        }

        public class GlobalSettings
        {
            [JsonProperty(PropertyName = "Store Vehicle On Plugin Unloaded / Server Restart")]
            public bool storeVehicle = true;

            [JsonProperty(PropertyName = "Clear Vehicle Data On Map Wipe")]
            public bool clearVehicleOnWipe;

            [JsonProperty(PropertyName = "Interval to check vehicle for wipe (Seconds)")]
            public float checkVehiclesInterval = 300;

            [JsonProperty(PropertyName = "Spawn vehicle in the direction you are looking at")]
            public bool spawnLookingAt = true;

            [JsonProperty(PropertyName = "Automatically claim vehicles purchased from vehicle vendors")]
            public bool autoClaimFromVendor;

            [JsonProperty(PropertyName = "Vehicle vendor purchases will unlock the license for the player")]
            public bool autoUnlockFromVendor;

            [JsonProperty(PropertyName = "Limit the number of vehicles at a time")]
            public int limitVehicles;

            [JsonProperty(PropertyName = "Kill a random vehicle when the number of vehicles is limited")]
            public bool killVehicleLimited;

            [JsonProperty(PropertyName = "Prevent vehicles from damaging players")]
            public bool preventDamagePlayer = true;

            [JsonProperty(PropertyName = "Prevent vehicles from shattering")]
            public bool preventShattering = true;

            [JsonProperty(PropertyName = "Prevent vehicles from spawning or recalling in safe zone")]
            public bool preventSafeZone = true;

            [JsonProperty(PropertyName = "Prevent vehicles from spawning or recalling when the player are building blocked")]
            public bool preventBuildingBlocked = true;

            [JsonProperty(PropertyName = "Prevent vehicles from spawning or recalling when the player is mounted or parented")]
            public bool preventMountedOrParented = true;

            [JsonProperty(PropertyName = "Check if any player mounted when recalling a vehicle")]
            public bool anyMountedRecall = true;

            [JsonProperty(PropertyName = "Check if any player mounted when killing a vehicle")]
            public bool anyMountedKill;

            [JsonProperty(PropertyName = "Dismount all players when a vehicle is recalled")]
            public bool dismountAllPlayersRecall = true;

            [JsonProperty(PropertyName = "Prevent other players from mounting vehicle")]
            public bool preventMounting = true;

            [JsonProperty(PropertyName = "Prevent mounting on driver's seat only")]
            public bool preventDriverSeat = true;

            [JsonProperty(PropertyName = "Prevent other players from looting fuel container and inventory")]
            public bool preventLooting = true;

            [JsonProperty(PropertyName = "Use Teams")]
            public bool useTeams;

            [JsonProperty(PropertyName = "Use Clans")]
            public bool useClans = true;

            [JsonProperty(PropertyName = "Use Friends")]
            public bool useFriends = true;

            [JsonProperty(PropertyName = "Vehicle No Decay")]
            public bool noDecay;

            [JsonProperty(PropertyName = "Vehicle No Fire Ball")]
            public bool noFireBall = true;

            [JsonProperty(PropertyName = "Vehicle No Server Gibs")]
            public bool noServerGibs = true;

            [JsonProperty(PropertyName = "Chinook No Map Marker")]
            public bool noMapMarker = true;

            [JsonProperty(PropertyName = "Use Raid Blocker (Need NoEscape Plugin)")]
            public bool useRaidBlocker;

            [JsonProperty(PropertyName = "Use Combat Blocker (Need NoEscape Plugin)")]
            public bool useCombatBlocker;
        }

        public class NormalVehicleSettings
        {
            [JsonProperty(PropertyName = "Sedan Vehicle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public SedanSettings sedan = new SedanSettings
            {
                Purchasable = true,
                DisplayName = "Sedan",
                Distance = 5,
                MinDistanceForPlayers = 3,
                UsePermission = true,
                Permission = "vehiclelicence.sedan",
                Commands = new List<string> { "car", "sedan" },
                PurchasePrices = new Dictionary<string, PriceInfo>
                {
                    ["scrap"] = new PriceInfo { amount = 300, displayName = "Scrap" }
                },
                SpawnCooldown = 300,
                RecallCooldown = 30,
                CooldownPermissions = new Dictionary<string, CooldownPermission>
                {
                    ["vehiclelicence.vip"] = new CooldownPermission
                    {
                        spawnCooldown = 150,
                        recallCooldown = 10,
                    }
                },
            };

            [JsonProperty(PropertyName = "Chinook Vehicle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public ChinookSettings chinook = new ChinookSettings
            {
                Purchasable = true,
                DisplayName = "Chinook",
                Distance = 15,
                MinDistanceForPlayers = 6,
                UsePermission = true,
                Permission = "vehiclelicence.chinook",
                Commands = new List<string> { "ch47", "chinook" },
                PurchasePrices = new Dictionary<string, PriceInfo>
                {
                    ["scrap"] = new PriceInfo { amount = 3000, displayName = "Scrap" }
                },
                SpawnCooldown = 3000,
                RecallCooldown = 30,
                CooldownPermissions = new Dictionary<string, CooldownPermission>
                {
                    ["vehiclelicence.vip"] = new CooldownPermission
                    {
                        spawnCooldown = 1500,
                        recallCooldown = 10,
                    }
                },
            };

            [JsonProperty(PropertyName = "Rowboat Vehicle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public RowboatSettings rowboat = new RowboatSettings
            {
                Purchasable = true,
                DisplayName = "Row Boat",
                Distance = 5,
                MinDistanceForPlayers = 2,
                UsePermission = true,
                Permission = "vehiclelicence.rowboat",
                Commands = new List<string> { "row", "rowboat" },
                PurchasePrices = new Dictionary<string, PriceInfo>
                {
                    ["scrap"] = new PriceInfo { amount = 500, displayName = "Scrap" }
                },
                SpawnCooldown = 300,
                RecallCooldown = 30,
                CooldownPermissions = new Dictionary<string, CooldownPermission>
                {
                    ["vehiclelicence.vip"] = new CooldownPermission
                    {
                        spawnCooldown = 150,
                        recallCooldown = 10,
                    }
                },
            };

            [JsonProperty(PropertyName = "RHIB Vehicle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public RhibSettings rhib = new RhibSettings
            {
                Purchasable = true,
                DisplayName = "Rigid Hulled Inflatable Boat",
                Distance = 10,
                MinDistanceForPlayers = 3,
                UsePermission = true,
                Permission = "vehiclelicence.rhib",
                Commands = new List<string> { "rhib" },
                PurchasePrices = new Dictionary<string, PriceInfo>
                {
                    ["scrap"] = new PriceInfo { amount = 1000, displayName = "Scrap" }
                },
                SpawnCooldown = 450,
                RecallCooldown = 30,
                CooldownPermissions = new Dictionary<string, CooldownPermission>
                {
                    ["vehiclelicence.vip"] = new CooldownPermission
                    {
                        spawnCooldown = 225,
                        recallCooldown = 10,
                    }
                },
            };

            [JsonProperty(PropertyName = "Hot Air Balloon Vehicle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public HotAirBalloonSettings hotAirBalloon = new HotAirBalloonSettings
            {
                Purchasable = true,
                DisplayName = "Hot Air Balloon",
                Distance = 20,
                MinDistanceForPlayers = 5,
                UsePermission = true,
                Permission = "vehiclelicence.hotairballoon",
                Commands = new List<string> { "hab", "hotairballoon" },
                PurchasePrices = new Dictionary<string, PriceInfo>
                {
                    ["scrap"] = new PriceInfo { amount = 500, displayName = "Scrap" }
                },
                SpawnCooldown = 900,
                RecallCooldown = 30,
                CooldownPermissions = new Dictionary<string, CooldownPermission>
                {
                    ["vehiclelicence.vip"] = new CooldownPermission
                    {
                        spawnCooldown = 450,
                        recallCooldown = 10,
                    }
                },
            };

            [JsonProperty(PropertyName = "Ridable Horse Vehicle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public RidableHorseSettings ridableHorse = new RidableHorseSettings
            {
                Purchasable = true,
                DisplayName = "Ridable Horse",
                Distance = 6,
                MinDistanceForPlayers = 1,
                UsePermission = true,
                Permission = "vehiclelicence.ridablehorse",
                Commands = new List<string> { "horse", "ridablehorse" },
                PurchasePrices = new Dictionary<string, PriceInfo>
                {
                    ["scrap"] = new PriceInfo { amount = 700, displayName = "Scrap" }
                },
                SpawnCooldown = 3000,
                RecallCooldown = 30,
                CooldownPermissions = new Dictionary<string, CooldownPermission>
                {
                    ["vehiclelicence.vip"] = new CooldownPermission
                    {
                        spawnCooldown = 1500,
                        recallCooldown = 10,
                    }
                },
            };

            [JsonProperty(PropertyName = "Mini Copter Vehicle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public MiniCopterSettings miniCopter = new MiniCopterSettings
            {
                Purchasable = true,
                DisplayName = "Mini Copter",
                Distance = 8,
                MinDistanceForPlayers = 2,
                UsePermission = true,
                Permission = "vehiclelicence.minicopter",
                Commands = new List<string> { "mini", "minicopter" },
                PurchasePrices = new Dictionary<string, PriceInfo>
                {
                    ["scrap"] = new PriceInfo { amount = 4000, displayName = "Scrap" }
                },
                SpawnCooldown = 1800,
                RecallCooldown = 30,
                CooldownPermissions = new Dictionary<string, CooldownPermission>
                {
                    ["vehiclelicence.vip"] = new CooldownPermission
                    {
                        spawnCooldown = 900,
                        recallCooldown = 10,
                    }
                },
            };

            [JsonProperty(PropertyName = "Transport Helicopter Vehicle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public TransportHelicopterSettings transportHelicopter = new TransportHelicopterSettings
            {
                Purchasable = true,
                DisplayName = "Transport Copter",
                Distance = 10,
                MinDistanceForPlayers = 4,
                UsePermission = true,
                Permission = "vehiclelicence.transportcopter",
                Commands = new List<string>
                {
                    "tcop", "transportcopter"
                },
                PurchasePrices = new Dictionary<string, PriceInfo>
                {
                    ["scrap"] = new PriceInfo { amount = 5000, displayName = "Scrap" }
                },
                SpawnCooldown = 2400,
                RecallCooldown = 30,
                CooldownPermissions = new Dictionary<string, CooldownPermission>
                {
                    ["vehiclelicence.vip"] = new CooldownPermission
                    {
                        spawnCooldown = 1200,
                        recallCooldown = 10,
                    }
                },
            };

            [JsonProperty(PropertyName = "Work Cart Vehicle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public WorkCartSettings workCart = new WorkCartSettings
            {
                Purchasable = true,
                DisplayName = "Work Cart",
                Distance = 12,
                MinDistanceForPlayers = 6,
                UsePermission = true,
                Permission = "vehiclelicence.workcart",
                Commands = new List<string>
                {
                    "cart", "workcart"
                },
                PurchasePrices = new Dictionary<string, PriceInfo>
                {
                    ["scrap"] = new PriceInfo { amount = 2000, displayName = "Scrap" }
                },
                SpawnCooldown = 1800,
                RecallCooldown = 30,
                CooldownPermissions = new Dictionary<string, CooldownPermission>
                {
                    ["vehiclelicence.vip"] = new CooldownPermission
                    {
                        spawnCooldown = 900,
                        recallCooldown = 10,
                    }
                },
            };

            [JsonProperty(PropertyName = "Magnet Crane Vehicle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public MagnetCraneSettings magnetCrane = new MagnetCraneSettings
            {
                Purchasable = true,
                DisplayName = "Magnet Crane",
                Distance = 16,
                MinDistanceForPlayers = 8,
                UsePermission = true,
                Permission = "vehiclelicence.magnetcrane",
                Commands = new List<string>
                {
                    "crane", "magnetcrane"
                },
                PurchasePrices = new Dictionary<string, PriceInfo>
                {
                    ["scrap"] = new PriceInfo { amount = 2000, displayName = "Scrap" }
                },
                SpawnCooldown = 600,
                RecallCooldown = 30,
                CooldownPermissions = new Dictionary<string, CooldownPermission>
                {
                    ["vehiclelicence.vip"] = new CooldownPermission
                    {
                        spawnCooldown = 300,
                        recallCooldown = 10,
                    }
                },
            };

            [JsonProperty(PropertyName = "Submarine Solo Vehicle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public SubmarineSettings submarineSolo = new SubmarineSettings
            {
                Purchasable = true,
                DisplayName = "Submarine Solo",
                Distance = 5,
                MinDistanceForPlayers = 2,
                UsePermission = true,
                Permission = "vehiclelicence.submarinesolo",
                Commands = new List<string> { "subsolo", "solo" },
                PurchasePrices = new Dictionary<string, PriceInfo>
                {
                    ["scrap"] = new PriceInfo { amount = 600, displayName = "Scrap" }
                },
                SpawnCooldown = 300,
                RecallCooldown = 30,
                CooldownPermissions = new Dictionary<string, CooldownPermission>
                {
                    ["vehiclelicence.vip"] = new CooldownPermission
                    {
                        spawnCooldown = 150,
                        recallCooldown = 10,
                    }
                },
            };

            [JsonProperty(PropertyName = "Submarine Duo Vehicle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public SubmarineSettings submarineDuo = new SubmarineSettings
            {
                Purchasable = true,
                DisplayName = "Submarine Duo",
                Distance = 5,
                MinDistanceForPlayers = 2,
                UsePermission = true,
                Permission = "vehiclelicence.submarineduo",
                Commands = new List<string> { "subduo", "duo" },
                PurchasePrices = new Dictionary<string, PriceInfo>
                {
                    ["scrap"] = new PriceInfo { amount = 1000, displayName = "Scrap" }
                },
                SpawnCooldown = 300,
                RecallCooldown = 30,
                CooldownPermissions = new Dictionary<string, CooldownPermission>
                {
                    ["vehiclelicence.vip"] = new CooldownPermission
                    {
                        spawnCooldown = 150,
                        recallCooldown = 10,
                    }
                },
            };

            [JsonProperty(PropertyName = "Snowmobile Vehicle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public SnowmobileSettings snowmobile = new SnowmobileSettings
            {
                Purchasable = true,
                DisplayName = "Snowmobile",
                Distance = 5,
                MinDistanceForPlayers = 2,
                UsePermission = true,
                Permission = "vehiclelicence.snowmobile",
                Commands = new List<string> { "snow", "snowmobile" },
                PurchasePrices = new Dictionary<string, PriceInfo>
                {
                    ["scrap"] = new PriceInfo { amount = 1000, displayName = "Scrap" }
                },
                SpawnCooldown = 300,
                RecallCooldown = 30,
                CooldownPermissions = new Dictionary<string, CooldownPermission>
                {
                    ["vehiclelicence.vip"] = new CooldownPermission
                    {
                        spawnCooldown = 150,
                        recallCooldown = 10,
                    }
                },
            };

            [JsonProperty(PropertyName = "Tomaha Snowmobile Vehicle", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public SnowmobileSettings tomahaSnowmobile = new SnowmobileSettings
            {
                Purchasable = true,
                DisplayName = "Tomaha Snowmobile",
                Distance = 5,
                MinDistanceForPlayers = 2,
                UsePermission = true,
                Permission = "vehiclelicence.tomahasnowmobile",
                Commands = new List<string> { "tsnow", "tsnowmobile" },
                PurchasePrices = new Dictionary<string, PriceInfo>
                {
                    ["scrap"] = new PriceInfo { amount = 1000, displayName = "Scrap" }
                },
                SpawnCooldown = 300,
                RecallCooldown = 30,
                CooldownPermissions = new Dictionary<string, CooldownPermission>
                {
                    ["vehiclelicence.vip"] = new CooldownPermission
                    {
                        spawnCooldown = 150,
                        recallCooldown = 10,
                    }
                },
            };
        }

        #region BaseSettings

        public abstract class BaseVehicleSettings
        {
            #region Properties

            [JsonProperty(PropertyName = "Purchasable")]
            public bool Purchasable { get; set; }

            [JsonProperty(PropertyName = "Display Name")]
            public string DisplayName { get; set; }

            [JsonProperty(PropertyName = "Use Permission")]
            public bool UsePermission { get; set; }

            [JsonProperty(PropertyName = "Permission")]
            public string Permission { get; set; }

            [JsonProperty(PropertyName = "Distance To Spawn")]
            public float Distance { get; set; }

            [JsonProperty(PropertyName = "Time Before Vehicle Wipe (Seconds)")]
            public double WipeTime { get; set; }

            [JsonProperty(PropertyName = "Exclude cupboard zones when wiping")]
            public bool ExcludeCupboard { get; set; }

            [JsonProperty(PropertyName = "Maximum Health")]
            public float MaxHealth { get; set; }

            [JsonProperty(PropertyName = "Can Recall Maximum Distance")]
            public float RecallMaxDistance { get; set; }

            [JsonProperty(PropertyName = "Can Kill Maximum Distance")]
            public float KillMaxDistance { get; set; }

            [JsonProperty(PropertyName = "Minimum distance from player to recall or spawn")]
            public float MinDistanceForPlayers { get; set; } = 3f;

            [JsonProperty(PropertyName = "Remove License Once Crashed")]
            public bool RemoveLicenseOnceCrash { get; set; }

            [JsonProperty(PropertyName = "Commands")]
            public List<string> Commands { get; set; } = new List<string>();

            [JsonProperty(PropertyName = "Purchase Prices")]
            public Dictionary<string, PriceInfo> PurchasePrices { get; set; } = new Dictionary<string, PriceInfo>();

            [JsonProperty(PropertyName = "Spawn Prices")]
            public Dictionary<string, PriceInfo> SpawnPrices { get; set; } = new Dictionary<string, PriceInfo>();

            [JsonProperty(PropertyName = "Recall Prices")]
            public Dictionary<string, PriceInfo> RecallPrices { get; set; } = new Dictionary<string, PriceInfo>();

            [JsonProperty(PropertyName = "Recall Cooldown Bypass Prices")]
            public Dictionary<string, PriceInfo> BypassRecallCooldownPrices { get; set; } = new Dictionary<string, PriceInfo>();

            [JsonProperty(PropertyName = "Spawn Cooldown Bypass Prices")]
            public Dictionary<string, PriceInfo> BypassSpawnCooldownPrices { get; set; } = new Dictionary<string, PriceInfo>();

            [JsonProperty(PropertyName = "Spawn Cooldown (Seconds)")]
            public double SpawnCooldown { get; set; }

            [JsonProperty(PropertyName = "Recall Cooldown (Seconds)")]
            public double RecallCooldown { get; set; }

            [JsonProperty(PropertyName = "Cooldown Permissions")]
            public Dictionary<string, CooldownPermission> CooldownPermissions { get; set; } = new Dictionary<string, CooldownPermission>();

            #endregion Properties

            [JsonIgnore]
            private ConfigData configData => Instance.configData;

            protected virtual bool IsNormalVehicle => true;

            protected virtual EntityFuelSystem GetFuelSystem(BaseEntity entity)
            {
                return null;
            }

            protected virtual ItemContainer GetInventory(BaseEntity entity)
            {
                return null;
            }

            #region Setup

            public virtual void SetupVehicle(BaseEntity entity, Vehicle vehicle, BasePlayer player, bool giveFuel = true)
            {
                if (MaxHealth > 0 && Math.Abs(MaxHealth - entity.MaxHealth()) > 0f)
                {
                    (entity as BaseCombatEntity)?.InitializeHealth(MaxHealth, MaxHealth);
                }
                var helicopterVehicle = entity as BaseHelicopterVehicle;
                if (helicopterVehicle != null)
                {
                    if (configData.globalSettings.noServerGibs)
                    {
                        helicopterVehicle.serverGibs.guid = string.Empty;
                    }
                    if (configData.globalSettings.noFireBall)
                    {
                        helicopterVehicle.fireBall.guid = string.Empty;
                    }
                    if (configData.globalSettings.noMapMarker)
                    {
                        var ch47Helicopter = entity as CH47Helicopter;
                        if (ch47Helicopter != null)
                        {
                            ch47Helicopter.mapMarkerInstance?.Kill();
                            ch47Helicopter.mapMarkerEntityPrefab.guid = string.Empty;
                        }
                    }
                }

                if (configData.globalSettings.preventShattering)
                {
                    var magnetLiftable = entity.GetComponent<MagnetLiftable>();
                    if (magnetLiftable != null)
                    {
                        UnityEngine.Object.Destroy(magnetLiftable);
                    }
                }
            }

            #endregion Setup

            #region Recall

            public virtual void PreRecallVehicle(BasePlayer player, Vehicle vehicle, Vector3 position, Quaternion rotation)
            {
                if (configData.globalSettings.dismountAllPlayersRecall)
                {
                    DismountAllPlayers(vehicle.Entity);
                }

                if (CanDropInventory())
                {
                    TryDropVehicleInventory(player, vehicle);
                }

                if (vehicle.Entity.HasParent())
                {
                    vehicle.Entity.SetParent(null, true, true);
                }
            }

            public virtual void PostRecallVehicle(BasePlayer player, Vehicle vehicle, Vector3 position, Quaternion rotation)
            {
            }

            #region DropInventory

            protected virtual bool CanDropInventory()
            {
                return false;
            }

            private void TryDropVehicleInventory(BasePlayer player, Vehicle vehicle)
            {
                var droppedItemContainer = DropVehicleInventory(player, vehicle);
                if (droppedItemContainer != null)
                {
                    Instance.Print(player, Instance.Lang("VehicleInventoryDropped", player.UserIDString, DisplayName));
                }
            }

            protected virtual DroppedItemContainer DropVehicleInventory(BasePlayer player, Vehicle vehicle)
            {
                if (IsNormalVehicle)
                {
                    var inventory = GetInventory(vehicle.Entity);
                    if (inventory != null)
                    {
                        return inventory.Drop(PREFAB_ITEM_DROP, vehicle.Entity.GetDropPosition(), vehicle.Entity.transform.rotation);
                    }
                }
                return null;
            }

            #endregion DropInventory

            #endregion Recall

            #region Refund

            protected virtual bool CanRefundFuel(bool isCrash, bool isUnload)
            {
                return isUnload;
            }

            protected virtual bool CanRefundInventory(bool isCrash, bool isUnload)
            {
                return isUnload;
            }

            protected virtual void CollectVehicleItems(List<Item> items, Vehicle vehicle, bool isCrash, bool isUnload)
            {
                if (IsNormalVehicle)
                {
                    if (CanRefundFuel(isCrash, isUnload))
                    {
                        var fuelSystem = GetFuelSystem(vehicle.Entity);
                        if (fuelSystem != null)
                        {
                            var fuelContainer = fuelSystem.GetFuelContainer();
                            if (fuelContainer != null && fuelContainer.inventory != null)
                            {
                                items.AddRange(fuelContainer.inventory.itemList);
                            }
                        }
                    }
                    if (CanRefundInventory(isCrash, isUnload))
                    {
                        var inventory = GetInventory(vehicle.Entity);
                        if (inventory != null)
                        {
                            items.AddRange(inventory.itemList);
                        }
                    }
                }
            }

            public void RefundVehicleItems(Vehicle vehicle, bool isCrash, bool isUnload)
            {
                var collect = Pool.GetList<Item>();

                CollectVehicleItems(collect, vehicle, isCrash, isUnload);

                if (collect.Count > 0)
                {
                    var player = RustCore.FindPlayerById(vehicle.PlayerId);
                    if (player == null)
                    {
                        DropItemContainer(vehicle.Entity, vehicle.PlayerId, collect);
                    }
                    else
                    {
                        for (var i = collect.Count - 1; i >= 0; i--)
                        {
                            var item = collect[i];
                            player.GiveItem(item);
                        }

                        if (player.IsConnected)
                        {
                            Instance.Print(player, Instance.Lang("RefundedVehicleItems", player.UserIDString, DisplayName));
                        }
                    }
                }
                Pool.FreeList(ref collect);
            }

            #endregion Refund

            #region GiveFuel

            protected void TryGiveFuel(BaseEntity entity, IFuelVehicle iFuelVehicle)
            {
                if (iFuelVehicle == null || iFuelVehicle.SpawnFuelAmount <= 0)
                {
                    return;
                }
                var fuelSystem = GetFuelSystem(entity);
                if (fuelSystem != null)
                {
                    var fuelContainer = fuelSystem.GetFuelContainer();
                    if (fuelContainer != null && fuelContainer.inventory != null)
                    {
                        var fuelItem = ItemManager.CreateByItemID(ITEMID_FUEL, iFuelVehicle.SpawnFuelAmount);
                        if (!fuelItem.MoveToContainer(fuelContainer.inventory))
                        {
                            fuelItem.Remove();
                        }
                    }
                }
            }

            #endregion GiveFuel
        }

        public abstract class FuelVehicleSettings : BaseVehicleSettings, IFuelVehicle
        {
            public int SpawnFuelAmount { get; set; }
            public bool RefundFuelOnKill { get; set; } = true;
            public bool RefundFuelOnCrash { get; set; } = true;

            public override void SetupVehicle(BaseEntity entity, Vehicle vehicle, BasePlayer player, bool giveFuel = true)
            {
                if (giveFuel)
                {
                    TryGiveFuel(entity, this);
                }
                base.SetupVehicle(entity, vehicle, player, giveFuel);
            }

            protected override bool CanRefundFuel(bool isCrash, bool isUnload)
            {
                if (base.CanRefundInventory(isCrash, isUnload))
                {
                    return true;
                }
                return isCrash ? RefundFuelOnCrash : RefundFuelOnKill;
            }
        }

        public abstract class InventoryVehicleSettings : BaseVehicleSettings, IInventoryVehicle
        {
            public bool RefundInventoryOnKill { get; set; } = true;
            public bool RefundInventoryOnCrash { get; set; } = true;
            public bool DropInventoryOnRecall { get; set; }

            protected override bool CanDropInventory()
            {
                return DropInventoryOnRecall;
            }

            protected override bool CanRefundInventory(bool isCrash, bool isUnload)
            {
                if (base.CanRefundInventory(isCrash, isUnload))
                {
                    return true;
                }
                return isCrash ? RefundInventoryOnCrash : RefundInventoryOnKill;
            }
        }

        public abstract class InvFuelVehicleSettings : BaseVehicleSettings, IFuelVehicle, IInventoryVehicle
        {
            public int SpawnFuelAmount { get; set; }
            public bool RefundFuelOnKill { get; set; } = true;
            public bool RefundFuelOnCrash { get; set; } = true;
            public bool RefundInventoryOnKill { get; set; } = true;
            public bool RefundInventoryOnCrash { get; set; } = true;
            public bool DropInventoryOnRecall { get; set; }

            public override void SetupVehicle(BaseEntity entity, Vehicle vehicle, BasePlayer player, bool giveFuel = true)
            {
                if (giveFuel)
                {
                    TryGiveFuel(entity, this);
                }
                base.SetupVehicle(entity, vehicle, player, giveFuel);
            }

            protected override bool CanDropInventory()
            {
                return DropInventoryOnRecall;
            }

            protected override bool CanRefundInventory(bool isCrash, bool isUnload)
            {
                if (base.CanRefundInventory(isCrash, isUnload))
                {
                    return true;
                }
                return isCrash ? RefundInventoryOnCrash : RefundInventoryOnKill;
            }

            protected override bool CanRefundFuel(bool isCrash, bool isUnload)
            {
                if (base.CanRefundInventory(isCrash, isUnload))
                {
                    return true;
                }
                return isCrash ? RefundFuelOnCrash : RefundFuelOnKill;
            }
        }

        #endregion BaseSettings

        #region Interface

        public interface IFuelVehicle
        {
            [JsonProperty(PropertyName = "Amount Of Fuel To Spawn", Order = 20)]
            int SpawnFuelAmount { get; set; }

            [JsonProperty(PropertyName = "Refund Fuel On Kill", Order = 21)]
            bool RefundFuelOnKill { get; set; }

            [JsonProperty(PropertyName = "Refund Fuel On Crash", Order = 22)]
            bool RefundFuelOnCrash { get; set; }
        }

        public interface IInventoryVehicle
        {
            [JsonProperty(PropertyName = "Refund Inventory On Kill", Order = 30)]
            bool RefundInventoryOnKill { get; set; }

            [JsonProperty(PropertyName = "Refund Inventory On Crash", Order = 31)]
            bool RefundInventoryOnCrash { get; set; }

            [JsonProperty(PropertyName = "Drop Inventory Items When Vehicle Recall", Order = 32)]
            bool DropInventoryOnRecall { get; set; }
        }

        public interface IModularVehicle
        {
            [JsonProperty(PropertyName = "Refund Engine Items On Kill", Order = 40)]
            bool RefundEngineOnKill { get; set; }

            [JsonProperty(PropertyName = "Refund Engine Items On Crash", Order = 41)]
            bool RefundEngineOnCrash { get; set; }

            [JsonProperty(PropertyName = "Refund Module Items On Kill", Order = 42)]
            bool RefundModuleOnKill { get; set; }

            [JsonProperty(PropertyName = "Refund Module Items On Crash", Order = 43)]
            bool RefundModuleOnCrash { get; set; }
        }

        public interface ITrainVehicle
        {
        }

        #endregion Interface

        #region Struct

        public struct CooldownPermission
        {
            public double spawnCooldown;
            public double recallCooldown;
        }

        public struct ModuleItem
        {
            public string shortName;
            public float healthPercentage;
        }

        public struct EngineItem
        {
            public string shortName;
            public float conditionPercentage;
        }

        public struct PriceInfo
        {
            public int amount;
            public string displayName;
        }

        public struct TrainComponent
        {
            public TrainComponentType type;
        }

        #endregion Struct

        #region VehicleSettings

        public class SedanSettings : BaseVehicleSettings
        {
        }

        public class ChinookSettings : BaseVehicleSettings
        {
        }

        public class RowboatSettings : InvFuelVehicleSettings
        {
            protected override EntityFuelSystem GetFuelSystem(BaseEntity entity)
            {
                return (entity as RHIB)?.GetFuelSystem();
            }

            protected override ItemContainer GetInventory(BaseEntity entity)
            {
                return (entity as RHIB)?.storageUnitInstance.Get(true)?.inventory;
            }
        }

        public class RhibSettings : InvFuelVehicleSettings
        {
            protected override EntityFuelSystem GetFuelSystem(BaseEntity entity)
            {
                return (entity as RHIB)?.GetFuelSystem();
            }

            protected override ItemContainer GetInventory(BaseEntity entity)
            {
                return (entity as RHIB)?.storageUnitInstance.Get(true)?.inventory;
            }
        }

        public class HotAirBalloonSettings : InvFuelVehicleSettings
        {
            protected override EntityFuelSystem GetFuelSystem(BaseEntity entity)
            {
                return (entity as HotAirBalloon)?.fuelSystem;
            }

            protected override ItemContainer GetInventory(BaseEntity entity)
            {
                return (entity as HotAirBalloon)?.storageUnitInstance.Get(true)?.inventory;
            }
        }

        public class MiniCopterSettings : FuelVehicleSettings
        {
            protected override EntityFuelSystem GetFuelSystem(BaseEntity entity)
            {
                return (entity as MiniCopter)?.GetFuelSystem();
            }
        }

        public class TransportHelicopterSettings : FuelVehicleSettings
        {
            protected override EntityFuelSystem GetFuelSystem(BaseEntity entity)
            {
                return (entity as ScrapTransportHelicopter)?.GetFuelSystem();
            }
        }

        public class RidableHorseSettings : InventoryVehicleSettings
        {
            protected override ItemContainer GetInventory(BaseEntity entity)
            {
                return (entity as RidableHorse)?.inventory;
            }

            public override void PostRecallVehicle(BasePlayer player, Vehicle vehicle, Vector3 position, Quaternion rotation)
            {
                base.PostRecallVehicle(player, vehicle, position, rotation);

                var ridableHorse = vehicle.Entity as RidableHorse;
                if (ridableHorse != null)
                {
                    ridableHorse.TryLeaveHitch();
                    ridableHorse.DropToGround(ridableHorse.transform.position, true);//ridableHorse.UpdateDropToGroundForDuration(2f);
                }
            }
        }

        public class WorkCartSettings : FuelVehicleSettings
        {
            protected override EntityFuelSystem GetFuelSystem(BaseEntity entity)
            {
                return (entity as TrainEngine)?.GetFuelSystem();
            }

            public override void PostRecallVehicle(BasePlayer player, Vehicle vehicle, Vector3 position, Quaternion rotation)
            {
                base.PostRecallVehicle(player, vehicle, position, rotation);

                var trainEngine = vehicle.Entity as TrainEngine;
                if (trainEngine != null)
                {
                    trainEngine.initialSpawnTime = Time.time - 1f;

                    float distResult; TrainTrackSpline splineResult;
                    if (TrainTrackSpline.TryFindTrackNearby(trainEngine.GetFrontWheelPos(), 2f, out splineResult, out distResult) && splineResult.HasClearTrackSpaceNear(trainEngine))
                    {
                        trainEngine.FrontWheelSplineDist = distResult;
                        Vector3 tangent;
                        Vector3 positionAndTangent = splineResult.GetPositionAndTangent(trainEngine.FrontWheelSplineDist, trainEngine.transform.forward, out tangent);
                        trainEngine.SetTheRestFromFrontWheelData(ref splineResult, positionAndTangent, tangent, trainEngine.localTrackSelection);
                        trainEngine.FrontTrackSection = splineResult;

                        trainEngine.Invoke(() =>
                        {
                            trainEngine.completeTrain.UpdateTick(Time.fixedDeltaTime);
                        }, 0.25f);
                    }
                    else
                    {
                        trainEngine.Kill();
                    }
                }
            }
        }

        public class MagnetCraneSettings : FuelVehicleSettings
        {
            protected override EntityFuelSystem GetFuelSystem(BaseEntity entity)
            {
                return (entity as MagnetCrane)?.GetFuelSystem();
            }
        }

        public class SubmarineSettings : InvFuelVehicleSettings
        {
            protected override EntityFuelSystem GetFuelSystem(BaseEntity entity)
            {
                return (entity as BaseSubmarine)?.GetFuelSystem();
            }

            protected override ItemContainer GetInventory(BaseEntity entity)
            {
                return (entity as BaseSubmarine)?.GetTorpedoContainer()?.inventory;
            }
        }

        public class SnowmobileSettings : InvFuelVehicleSettings
        {
            protected override EntityFuelSystem GetFuelSystem(BaseEntity entity)
            {
                return (entity as Snowmobile)?.GetFuelSystem();
            }

            protected override ItemContainer GetInventory(BaseEntity entity)
            {
                return (entity as Snowmobile)?.GetItemContainer()?.inventory;
            }
        }

        public class ModularVehicleSettings : InvFuelVehicleSettings, IModularVehicle
        {
            #region Properties

            public bool RefundEngineOnKill { get; set; } = true;
            public bool RefundEngineOnCrash { get; set; } = true;
            public bool RefundModuleOnKill { get; set; } = true;
            public bool RefundModuleOnCrash { get; set; } = true;

            [JsonConverter(typeof(StringEnumConverter))]
            [JsonProperty(PropertyName = "Chassis Type (Small, Medium, Large)", Order = 50)]
            public ChassisType ChassisType { get; set; } = ChassisType.Small;

            [JsonProperty(PropertyName = "Vehicle Module Items", Order = 51)]
            public List<ModuleItem> ModuleItems { get; set; } = new List<ModuleItem>();

            [JsonProperty(PropertyName = "Vehicle Engine Items", Order = 52)]
            public List<EngineItem> EngineItems { get; set; } = new List<EngineItem>();

            #endregion Properties

            #region ModuleItems

            [JsonIgnore]
            private List<ModuleItem> _validModuleItems;

            [JsonIgnore]
            public IEnumerable<ModuleItem> ValidModuleItems
            {
                get
                {
                    if (_validModuleItems == null)
                    {
                        _validModuleItems = new List<ModuleItem>();
                        foreach (var modularItem in ModuleItems)
                        {
                            var itemDefinition = ItemManager.FindItemDefinition(modularItem.shortName);
                            if (itemDefinition != null)
                            {
                                var itemModVehicleModule = itemDefinition.GetComponent<ItemModVehicleModule>();
                                if (itemModVehicleModule == null || !itemModVehicleModule.entityPrefab.isValid)
                                {
                                    Instance.PrintError($"'{modularItem}' is not a valid vehicle module");
                                    continue;
                                }
                                _validModuleItems.Add(modularItem);
                            }
                        }
                    }
                    return _validModuleItems;
                }
            }

            public IEnumerable<Item> CreateModuleItems()
            {
                foreach (var moduleItem in ValidModuleItems)
                {
                    var item = ItemManager.CreateByName(moduleItem.shortName);
                    if (item != null)
                    {
                        item.condition = item.maxCondition * (moduleItem.healthPercentage / 100f);
                        item.MarkDirty();
                        yield return item;
                    }
                }
            }

            #endregion ModuleItems

            #region EngineItems

            [JsonIgnore]
            private List<EngineItem> _validEngineItems;

            [JsonIgnore]
            public IEnumerable<EngineItem> ValidEngineItems
            {
                get
                {
                    if (_validEngineItems == null)
                    {
                        _validEngineItems = new List<EngineItem>();
                        foreach (var modularItem in EngineItems)
                        {
                            var itemDefinition = ItemManager.FindItemDefinition(modularItem.shortName);
                            if (itemDefinition != null)
                            {
                                var itemModEngineItem = itemDefinition.GetComponent<ItemModEngineItem>();
                                if (itemModEngineItem == null)
                                {
                                    Instance.PrintError($"'{modularItem}' is not a valid engine item");
                                    continue;
                                }
                                _validEngineItems.Add(modularItem);
                            }
                        }
                    }
                    return _validEngineItems;
                }
            }

            public IEnumerable<Item> CreateEngineItems()
            {
                foreach (var engineItem in ValidEngineItems)
                {
                    var item = ItemManager.CreateByName(engineItem.shortName);
                    if (item != null)
                    {
                        item.condition = item.maxCondition * (engineItem.conditionPercentage / 100f);
                        item.MarkDirty();
                        yield return item;
                    }
                }
            }

            #endregion EngineItems

            protected override bool IsNormalVehicle => false;

            protected override EntityFuelSystem GetFuelSystem(BaseEntity entity)
            {
                return (entity as ModularCar)?.GetFuelSystem();
            }

            #region Setup

            public override void SetupVehicle(BaseEntity entity, Vehicle vehicle, BasePlayer player, bool giveFuel = true)
            {
                var modularCar = entity as ModularCar;
                if (modularCar != null)
                {
                    if (ValidModuleItems.Any())
                    {
                        AttacheVehicleModules(modularCar, vehicle);
                    }
                    if (ValidEngineItems.Any())
                    {
                        Instance.NextTick(() =>
                        {
                            AddItemsToVehicleEngine(modularCar, vehicle);
                        });
                    }
                }
                base.SetupVehicle(entity, vehicle, player, giveFuel);
            }

            #endregion Setup

            #region Recall

            public override void PreRecallVehicle(BasePlayer player, Vehicle vehicle, Vector3 position, Quaternion rotation)
            {
                base.PreRecallVehicle(player, vehicle, position, rotation);

                if (vehicle.Entity is ModularCar)
                {
                    var modularCarGarages = Pool.GetList<ModularCarGarage>();
                    Vis.Entities(vehicle.Entity.transform.position, 3f, modularCarGarages, Rust.Layers.Mask.Deployed | Rust.Layers.Mask.Default);
                    var modularCarGarage = modularCarGarages.FirstOrDefault(x => x.carOccupant == vehicle.Entity);
                    Pool.FreeList(ref modularCarGarages);
                    if (modularCarGarage != null)
                    {
                        modularCarGarage.enabled = false;
                        modularCarGarage.ReleaseOccupant();
                        modularCarGarage.Invoke(() => modularCarGarage.enabled = true, 0.25f);
                    }
                }
            }

            #region DropInventory

            protected override DroppedItemContainer DropVehicleInventory(BasePlayer player, Vehicle vehicle)
            {
                var modularCar = vehicle.Entity as ModularCar;
                if (modularCar != null)
                {
                    foreach (var moduleEntity in modularCar.AttachedModuleEntities)
                    {
                        if (moduleEntity is VehicleModuleEngine)
                        {
                            continue;
                        }
                        var moduleStorage = moduleEntity as VehicleModuleStorage;
                        if (moduleStorage != null)
                        {
                            return moduleStorage.GetContainer()?.inventory?.Drop(PREFAB_ITEM_DROP, vehicle.Entity.GetDropPosition(), vehicle.Entity.transform.rotation);
                        }
                    }
                }
                return null;
            }

            #endregion DropInventory

            #endregion Recall

            #region Refund

            private void GetRefundStatus(bool isCrash, bool isUnload, out bool refundFuel, out bool refundInventory, out bool refundEngine, out bool refundModule)
            {
                if (isUnload)
                {
                    refundFuel = refundInventory = refundEngine = refundModule = true;
                    return;
                }
                refundFuel = isCrash ? RefundFuelOnCrash : RefundFuelOnKill;
                refundInventory = isCrash ? RefundInventoryOnCrash : RefundInventoryOnKill;
                refundEngine = isCrash ? RefundEngineOnCrash : RefundEngineOnKill;
                refundModule = isCrash ? RefundModuleOnCrash : RefundModuleOnKill;
            }

            protected override void CollectVehicleItems(List<Item> items, Vehicle vehicle, bool isCrash, bool isUnload)
            {
                var modularCar = vehicle.Entity as ModularCar;
                if (modularCar != null)
                {
                    bool refundFuel, refundInventory, refundEngine, refundModule;
                    GetRefundStatus(isCrash, isUnload, out refundFuel, out refundInventory, out refundEngine, out refundModule);

                    foreach (var moduleEntity in modularCar.AttachedModuleEntities)
                    {
                        if (refundEngine)
                        {
                            var moduleEngine = moduleEntity as VehicleModuleEngine;
                            if (moduleEngine != null)
                            {
                                var engineContainer = moduleEngine.GetContainer()?.inventory;
                                if (engineContainer != null)
                                {
                                    items.AddRange(engineContainer.itemList);
                                }
                                continue;
                            }
                        }
                        if (refundInventory)
                        {
                            var moduleStorage = moduleEntity as VehicleModuleStorage;
                            if (moduleStorage != null && !(moduleEntity is VehicleModuleEngine))
                            {
                                var storageContainer = moduleStorage.GetContainer()?.inventory;
                                if (storageContainer != null)
                                {
                                    items.AddRange(storageContainer.itemList);
                                }
                            }
                        }
                    }
                    if (refundFuel)
                    {
                        var fuelContainer = modularCar.GetFuelSystem()?.GetFuelContainer()?.inventory;
                        if (fuelContainer != null)
                        {
                            items.AddRange(fuelContainer.itemList);
                        }
                    }
                    if (refundModule)
                    {
                        var moduleContainer = modularCar.Inventory?.ModuleContainer;
                        if (moduleContainer != null)
                        {
                            items.AddRange(moduleContainer.itemList);
                        }
                    }
                    //var chassisContainer = modularCar.Inventory?.ChassisContainer;
                    //if (chassisContainer != null)
                    //{
                    //    collect.AddRange(chassisContainer.itemList);
                    //}
                }
            }

            #endregion Refund

            #region VehicleModules

            private void AttacheVehicleModules(ModularCar modularCar, Vehicle vehicle)
            {
                foreach (var moduleItem in CreateModuleItems())
                {
                    if (!modularCar.TryAddModule(moduleItem))
                    {
                        Instance?.PrintError($"Module item '{moduleItem.info.shortname}' in '{vehicle.VehicleType}' cannot be attached to the vehicle");
                        moduleItem.Remove();
                    }
                }
            }

            private void AddItemsToVehicleEngine(ModularCar modularCar, Vehicle vehicle)
            {
                if (modularCar == null || modularCar.IsDestroyed)
                {
                    return;
                }
                foreach (var moduleEntity in modularCar.AttachedModuleEntities)
                {
                    var vehicleModuleEngine = moduleEntity as VehicleModuleEngine;
                    if (vehicleModuleEngine != null)
                    {
                        var engineInventory = vehicleModuleEngine.GetContainer()?.inventory;
                        if (engineInventory != null)
                        {
                            foreach (var engineItem in CreateEngineItems())
                            {
                                bool moved = false;
                                for (int i = 0; i < engineInventory.capacity; i++)
                                {
                                    if (engineItem.MoveToContainer(engineInventory, i, false))
                                    {
                                        moved = true;
                                        break;
                                    }
                                }
                                if (!moved)
                                {
                                    Instance?.PrintError($"Engine item '{engineItem.info.shortname}' in '{vehicle.VehicleType}' cannot be move to the vehicle engine inventory");
                                    engineItem.Remove();
                                    engineItem.DoRemove();
                                }
                            }
                        }
                    }
                }
            }

            #endregion VehicleModules
        }

        public class TrainVehicleSettings : FuelVehicleSettings, ITrainVehicle
        {
            #region Properties

            [JsonProperty(PropertyName = "Train Components", Order = 50)]
            public List<TrainComponent> TrainComponents { get; set; } = new List<TrainComponent>();

            #endregion Properties

            #region Prefabs

            [JsonIgnore]
            private List<string> _prefabs;

            [JsonIgnore]
            public IEnumerable<string> Prefabs
            {
                get
                {
                    if (_prefabs == null)
                    {
                        _prefabs = new List<string>();
                        foreach (var component in TrainComponents)
                        {
                            var prefab = GetTrainVehiclePrefab(component.type);
                            if (!string.IsNullOrEmpty(prefab))
                            {
                                _prefabs.Add(prefab);
                            }
                        }
                    }
                    return _prefabs;
                }
            }

            #endregion Prefabs

            protected override bool IsNormalVehicle => false;

            protected override EntityFuelSystem GetFuelSystem(BaseEntity entity)
            {
                return (entity as TrainCar)?.GetFuelSystem();
            }

            public override void PostRecallVehicle(BasePlayer player, Vehicle vehicle, Vector3 position, Quaternion rotation)
            {
                base.PostRecallVehicle(player, vehicle, position, rotation);

                var trainEngine = vehicle.Entity as TrainEngine;
                if (trainEngine != null)
                {
                    trainEngine.initialSpawnTime = Time.time - 1f;

                    float distResult; TrainTrackSpline splineResult;
                    if (TrainTrackSpline.TryFindTrackNearby(trainEngine.GetFrontWheelPos(), 2f, out splineResult, out distResult) && splineResult.HasClearTrackSpaceNear(trainEngine))
                    {
                        trainEngine.FrontWheelSplineDist = distResult;
                        Vector3 tangent;
                        Vector3 positionAndTangent = splineResult.GetPositionAndTangent(trainEngine.FrontWheelSplineDist, trainEngine.transform.forward, out tangent);
                        trainEngine.SetTheRestFromFrontWheelData(ref splineResult, positionAndTangent, tangent, trainEngine.localTrackSelection);
                        trainEngine.FrontTrackSection = splineResult;

                        trainEngine.Invoke(() =>
                        {
                            trainEngine.completeTrain.UpdateTick(Time.fixedDeltaTime);
                        }, 0.25f);
                    }
                    else
                    {
                        trainEngine.Kill();
                    }
                }
            }

            protected override void CollectVehicleItems(List<Item> items, Vehicle vehicle, bool isCrash, bool isUnload)
            {
                var trainCar = vehicle.Entity as TrainCar;
                if (trainCar != null)
                {
                    if (CanRefundFuel(isCrash, isUnload))
                    {
                        var fuelContainer = trainCar.GetFuelSystem()?.GetFuelContainer()?.inventory;
                        if (fuelContainer != null)
                        {
                            items.AddRange(fuelContainer.itemList);
                        }
                    }
                }
            }
        }

        #endregion VehicleSettings

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
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Creating a new configuration file");
            configData = new ConfigData();
            configData.version = Version;
        }

        protected override void SaveConfig() => Config.WriteObject(configData);

        private void UpdateConfigValues()
        {
            if (configData.version < Version)
            {
                if (configData.version <= default(VersionNumber))
                {
                    string prefix, prefixColor;
                    if (GetConfigValue(out prefix, "Chat Settings", "Chat Prefix") && GetConfigValue(out prefixColor, "Chat Settings", "Chat Prefix Color"))
                    {
                        configData.chatSettings.prefix = $"<color={prefixColor}>{prefix}</color>: ";
                    }
                }
                if (configData.version <= new VersionNumber(1, 7, 3))
                {
                    configData.normalVehicles.sedan.MinDistanceForPlayers = 3f;
                    configData.normalVehicles.chinook.MinDistanceForPlayers = 5f;
                    configData.normalVehicles.rowboat.MinDistanceForPlayers = 2f;
                    configData.normalVehicles.rhib.MinDistanceForPlayers = 3f;
                    configData.normalVehicles.hotAirBalloon.MinDistanceForPlayers = 4f;
                    configData.normalVehicles.ridableHorse.MinDistanceForPlayers = 1f;
                    configData.normalVehicles.miniCopter.MinDistanceForPlayers = 2f;
                    configData.normalVehicles.transportHelicopter.MinDistanceForPlayers = 4f;
                    foreach (var entry in configData.modularVehicles)
                    {
                        switch (entry.Value.ChassisType)
                        {
                            case ChassisType.Small:
                                entry.Value.MinDistanceForPlayers = 2f;
                                break;

                            case ChassisType.Medium:
                                entry.Value.MinDistanceForPlayers = 2.5f;
                                break;

                            case ChassisType.Large:
                                entry.Value.MinDistanceForPlayers = 3f;
                                break;

                            default: continue;
                        }
                    }
                }
                if (configData.version >= new VersionNumber(1, 7, 17) && configData.version <= new VersionNumber(1, 7, 18))
                {
                    LoadData();
                    foreach (var data in storedData.playerData)
                    {
                        Vehicle vehicle;
                        if (data.Value.TryGetValue("SubmarineDouble", out vehicle))
                        {
                            data.Value.Remove("SubmarineDouble");
                            data.Value.Add(nameof(NormalVehicleType.SubmarineDuo), vehicle);
                        }
                    }
                    SaveData();
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

        #endregion ConfigurationFile

        #region DataFile

        public StoredData storedData { get; private set; }

        public class StoredData
        {
            public readonly Dictionary<ulong, Dictionary<string, Vehicle>> playerData = new Dictionary<ulong, Dictionary<string, Vehicle>>();

            public IEnumerable<BaseEntity> ActiveVehicles(ulong playerId)
            {
                Dictionary<string, Vehicle> vehicles;
                if (!playerData.TryGetValue(playerId, out vehicles))
                {
                    yield break;
                }

                foreach (var vehicle in vehicles.Values)
                {
                    if (vehicle.Entity != null && !vehicle.Entity.IsDestroyed)
                    {
                        yield return vehicle.Entity;
                    }
                }
            }

            public Dictionary<string, Vehicle> GetPlayerVehicles(ulong playerId, bool readOnly = true)
            {
                Dictionary<string, Vehicle> vehicles;
                if (!playerData.TryGetValue(playerId, out vehicles))
                {
                    if (!readOnly)
                    {
                        vehicles = new Dictionary<string, Vehicle>();
                        playerData.Add(playerId, vehicles);
                        return vehicles;
                    }
                    return null;
                }
                return vehicles;
            }

            public bool IsVehiclePurchased(ulong playerId, string vehicleType, out Vehicle vehicle)
            {
                vehicle = GetVehicleLicense(playerId, vehicleType);
                if (vehicle == null)
                {
                    return false;
                }
                return true;
            }

            public Vehicle GetVehicleLicense(ulong playerId, string vehicleType)
            {
                Dictionary<string, Vehicle> vehicles;
                if (!playerData.TryGetValue(playerId, out vehicles))
                {
                    return null;
                }
                Vehicle vehicle;
                if (!vehicles.TryGetValue(vehicleType, out vehicle))
                {
                    return null;
                }
                return vehicle;
            }

            public bool HasVehicleLicense(ulong playerId, string vehicleType)
            {
                Dictionary<string, Vehicle> vehicles;
                if (!playerData.TryGetValue(playerId, out vehicles))
                {
                    return false;
                }
                return vehicles.ContainsKey(vehicleType);
            }

            public bool AddVehicleLicense(ulong playerId, string vehicleType)
            {
                Dictionary<string, Vehicle> vehicles;
                if (!playerData.TryGetValue(playerId, out vehicles))
                {
                    vehicles = new Dictionary<string, Vehicle>();
                    playerData.Add(playerId, vehicles);
                }
                if (vehicles.ContainsKey(vehicleType))
                {
                    return false;
                }
                vehicles.Add(vehicleType, new Vehicle());
                Instance.SaveData();
                return true;
            }

            public bool RemoveVehicleLicense(ulong playerId, string vehicleType)
            {
                Dictionary<string, Vehicle> vehicles;
                if (!playerData.TryGetValue(playerId, out vehicles))
                {
                    return false;
                }

                if (!vehicles.Remove(vehicleType))
                {
                    return false;
                }
                Instance.SaveData();
                return true;
            }

            public List<string> GetVehicleLicenseNames(ulong playerId)
            {
                Dictionary<string, Vehicle> vehicles;
                if (!playerData.TryGetValue(playerId, out vehicles))
                {
                    return new List<string>();
                }
                return vehicles.Keys.ToList();
            }

            public void PurchaseAllVehicles(ulong playerId)
            {
                bool changed = false;
                Dictionary<string, Vehicle> vehicles;
                if (!playerData.TryGetValue(playerId, out vehicles))
                {
                    vehicles = new Dictionary<string, Vehicle>();
                    playerData.Add(playerId, vehicles);
                }
                foreach (var vehicleType in Instance.allVehicleSettings.Keys)
                {
                    if (!vehicles.ContainsKey(vehicleType))
                    {
                        vehicles.Add(vehicleType, new Vehicle());
                        changed = true;
                    }
                }
                if (changed) Instance.SaveData();
            }

            public void AddLicenseForAllPlayers(string vehicleType)
            {
                foreach (var entry in playerData)
                {
                    if (!entry.Value.ContainsKey(vehicleType))
                    {
                        entry.Value.Add(vehicleType, new Vehicle());
                    }
                }
            }

            public void RemoveLicenseForAllPlayers(string vehicleType)
            {
                foreach (var entry in playerData)
                {
                    entry.Value.Remove(vehicleType);
                }
            }

            public void ResetPlayerData()
            {
                foreach (var vehicleEntries in playerData)
                {
                    foreach (var vehicleEntry in vehicleEntries.Value)
                    {
                        vehicleEntry.Value.Reset();
                    }
                }
            }
        }

        public class Vehicle
        {
            [JsonProperty("entityID")]
            public uint EntityId { get; set; }

            [JsonProperty("lastDeath")]
            public double LastDeath { get; set; }

            [JsonIgnore]
            public ulong PlayerId { get; set; }

            [JsonIgnore]
            public BaseEntity Entity { get; set; }

            [JsonIgnore]
            public string VehicleType { get; set; }

            [JsonIgnore]
            public double LastRecall { get; set; }

            [JsonIgnore]
            public double LastDismount { get; set; }

            public void OnDismount()
            {
                LastDismount = TimeEx.currentTimestamp;
            }

            public void OnRecall()
            {
                LastRecall = TimeEx.currentTimestamp;
            }

            public void OnDeath()
            {
                Entity = null;
                EntityId = 0;
                LastDeath = TimeEx.currentTimestamp;
            }

            public void Reset()
            {
                EntityId = 0;
                LastDeath = 0;
            }
        }

        private void LoadData()
        {
            try
            {
                storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            }
            catch
            {
                storedData = null;
            }
            if (storedData == null)
            {
                ClearData();
            }
        }

        private void ClearData()
        {
            storedData = new StoredData();
            SaveData();
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);

        private void OnNewSave()
        {
            if (configData.globalSettings.clearVehicleOnWipe)
            {
                ClearData();
            }
            else
            {
                storedData.ResetPlayerData();
                SaveData();
            }
        }

        #endregion DataFile

        #region LanguageFile

        private void Print(BasePlayer player, string message)
        {
            Player.Message(player, message, configData.chatSettings.prefix, configData.chatSettings.steamIDIcon);
        }

        private void Print(ConsoleSystem.Arg arg, string message)
        {
            var player = arg.Player();
            if (player == null) Puts(message);
            else PrintToConsole(player, message);
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
                ["Help"] = "These are the available commands:",
                ["HelpLicence1"] = "<color=#4DFF4D>/{0}</color> -- To buy a vehicle",
                ["HelpLicence2"] = "<color=#4DFF4D>/{0}</color> -- To spawn a vehicle",
                ["HelpLicence3"] = "<color=#4DFF4D>/{0}</color> -- To recall a vehicle",
                ["HelpLicence4"] = "<color=#4DFF4D>/{0}</color> -- To kill a vehicle",
                ["HelpLicence5"] = "<color=#4DFF4D>/{0}</color> -- To buy, spawn or recall a <color=#009EFF>{1}</color>",
                //["HelpLicence6"] = "<color=#4DFF4D>/{0}</color> -- To kill a <color=#009EFF>{1}</color>",

                ["PriceFormat"] = "<color=#FF1919>{0}</color> x{1}",
                ["HelpBuy"] = "<color=#4DFF4D>/{0} {1}</color> -- To buy a <color=#009EFF>{2}</color>",
                ["HelpBuyPrice"] = "<color=#4DFF4D>/{0} {1}</color> -- To buy a <color=#009EFF>{2}</color>. Price: {3}",
                ["HelpSpawn"] = "<color=#4DFF4D>/{0} {1}</color> -- To spawn a <color=#009EFF>{2}</color>",
                ["HelpSpawnPrice"] = "<color=#4DFF4D>/{0} {1}</color> -- To spawn a <color=#009EFF>{2}</color>. Price: {3}",
                ["HelpRecall"] = "<color=#4DFF4D>/{0} {1}</color> -- To recall a <color=#009EFF>{2}</color>",
                ["HelpRecallPrice"] = "<color=#4DFF4D>/{0} {1}</color> -- To recall a <color=#009EFF>{2}</color>. Price: {3}",
                ["HelpKill"] = "<color=#4DFF4D>/{0} {1}</color> -- To kill a <color=#009EFF>{2}</color>",
                ["HelpKillCustom"] = "<color=#4DFF4D>/{0} {1}</color> or <color=#4DFF4D>/{2}</color>  -- To kill a <color=#009EFF>{3}</color>",

                ["NotAllowed"] = "You do not have permission to use this command.",
                ["RaidBlocked"] = "<color=#FF1919>You may not do that while raid blocked</color>.",
                ["CombatBlocked"] = "<color=#FF1919>You may not do that while combat blocked</color>.",
                ["OptionNotFound"] = "This <color=#009EFF>{0}</color> option doesn't exist.",
                ["VehiclePurchased"] = "You have purchased a <color=#009EFF>{0}</color>, type <color=#4DFF4D>/{1}</color> for more information.",
                ["VehicleAlreadyPurchased"] = "You have already purchased <color=#009EFF>{0}</color>.",
                ["VehicleCannotBeBought"] = "<color=#009EFF>{0}</color> is unpurchasable",
                ["VehicleNotOut"] = "<color=#009EFF>{0}</color> is not out, type <color=#4DFF4D>/{1}</color> for more information.",
                ["AlreadyVehicleOut"] = "You already have a <color=#009EFF>{0}</color> outside, type <color=#4DFF4D>/{1}</color> for more information.",
                ["VehicleNotYetPurchased"] = "You have not yet purchased a <color=#009EFF>{0}</color>, type <color=#4DFF4D>/{1}</color> for more information.",
                ["VehicleSpawned"] = "You spawned your <color=#009EFF>{0}</color>.",
                ["VehicleRecalled"] = "You recalled your <color=#009EFF>{0}</color>.",
                ["VehicleKilled"] = "You killed your <color=#009EFF>{0}</color>.",
                ["VehicleOnSpawnCooldown"] = "You must wait <color=#FF1919>{0}</color> seconds before you can spawn your <color=#009EFF>{1}</color>.",
                ["VehicleOnRecallCooldown"] = "You must wait <color=#FF1919>{0}</color> seconds before you can recall your <color=#009EFF>{1}</color>.",
                ["VehicleOnSpawnCooldownPay"] = "You must wait <color=#FF1919>{0}</color> seconds before you can spawn your <color=#009EFF>{1}</color>. You can bypass this cooldown by using the <color=#FF1919>/{2}</color> command to pay <color=#009EFF>{3}</color>",
                ["VehicleOnRecallCooldownPay"] = "You must wait <color=#FF1919>{0}</color> seconds before you can recall your <color=#009EFF>{1}</color>. You can bypass this cooldown by using the <color=#FF1919>/{2}</color> command to pay <color=#009EFF>{3}</color>",
                ["NotLookingAtWater"] = "You must be looking at water to spawn or recall a <color=#009EFF>{0}</color>.",
                ["BuildingBlocked"] = "You can't spawn a <color=#009EFF>{0}</color> if you don't have the building privileges.",
                ["RefundedVehicleItems"] = "Your <color=#009EFF>{0}</color> vehicle items was refunded to your inventory.",
                ["PlayerMountedOnVehicle"] = "It cannot be recalled or killed when players mounted on your <color=#009EFF>{0}</color>.",
                ["PlayerInSafeZone"] = "You cannot spawn or recall your <color=#009EFF>{0}</color> in the safe zone.",
                ["VehicleInventoryDropped"] = "Your <color=#009EFF>{0}</color> vehicle inventory cannot be recalled, it have dropped to the ground.",
                ["NoResourcesToPurchaseVehicle"] = "You don't have enough resources to buy a <color=#009EFF>{0}</color>. You are missing: \n{1}",
                ["NoResourcesToSpawnVehicle"] = "You don't have enough resources to spawn a <color=#009EFF>{0}</color>. You are missing: \n{1}",
                ["NoResourcesToSpawnVehicleBypass"] = "You don't have enough resources to bypass the cooldown to spawn a <color=#009EFF>{0}</color>. You are missing: \n{1}",
                ["NoResourcesToRecallVehicle"] = "You don't have enough resources to recall a <color=#009EFF>{0}</color>. You are missing: \n{1}",
                ["NoResourcesToRecallVehicleBypass"] = "You don't have enough resources to bypass the cooldown to recall a <color=#009EFF>{0}</color>. You are missing: \n{1}",
                ["MountedOrParented"] = "You cannot spawn or recall a <color=#009EFF>{0}</color> when mounted or parented.",
                ["RecallTooFar"] = "You must be within <color=#FF1919>{0}</color> meters of <color=#009EFF>{1}</color> to recall.",
                ["KillTooFar"] = "You must be within <color=#FF1919>{0}</color> meters of <color=#009EFF>{1}</color> to kill.",
                ["PlayersOnNearby"] = "You cannot spawn or recall a <color=#009EFF>{0}</color> when there are players near the position you are looking at.",
                ["RecallWasBlocked"] = "An external plugin blocked you from recalling a <color=#009EFF>{0}</color>.",
                ["SpawnWasBlocked"] = "An external plugin blocked you from spawning a <color=#009EFF>{0}</color>.",
                ["VehiclesLimit"] = "You can have up to <color=#009EFF>{0}</color> vehicles at a time.",
                ["TooFarTrainTrack"] = "You are too far from the train track.",
                ["TooCloseTrainBarricadeOrWorkCart"] = "You are too close to the train barricade or work cart.",
                ["NotSpawnedOrRecalled"] = "For some reason, your <color=#009EFF>{0}</color> vehicle was not spawned/recalled",

                ["CantUse"] = "Sorry! This {0} belongs to {1}.You cannot use it."
            }, this);
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Help"] = "可用命令列表:",
                ["HelpLicence1"] = "<color=#4DFF4D>/{0}</color> -- 购买一辆载具",
                ["HelpLicence2"] = "<color=#4DFF4D>/{0}</color> -- 生成一辆载具",
                ["HelpLicence3"] = "<color=#4DFF4D>/{0}</color> -- 召回一辆载具",
                ["HelpLicence4"] = "<color=#4DFF4D>/{0}</color> -- 摧毁一辆载具",
                ["HelpLicence5"] = "<color=#4DFF4D>/{0}</color> -- 购买，生成，召回一辆 <color=#009EFF>{1}</color>",
                //["HelpLicence6"] = "<color=#4DFF4D>/{0}</color> -- 摧毁一辆 <color=#009EFF>{1}</color>",

                ["PriceFormat"] = "<color=#FF1919>{0}</color> x{1}",
                ["HelpBuy"] = "<color=#4DFF4D>/{0} {1}</color> -- 购买一辆 <color=#009EFF>{2}</color>",
                ["HelpBuyPrice"] = "<color=#4DFF4D>/{0} {1}</color> -- 购买一辆 <color=#009EFF>{2}</color>，价格: {3}",
                ["HelpSpawn"] = "<color=#4DFF4D>/{0} {1}</color> -- 生成一辆 <color=#009EFF>{2}</color>",
                ["HelpSpawnPrice"] = "<color=#4DFF4D>/{0} {1}</color> -- 生成一辆 <color=#009EFF>{2}</color>，价格: {3}",
                ["HelpRecall"] = "<color=#4DFF4D>/{0} {1}</color> -- 召回一辆 <color=#009EFF>{2}</color>",
                ["HelpRecallPrice"] = "<color=#4DFF4D>/{0} {1}</color> -- 召回一辆 <color=#009EFF>{2}</color>，价格: {3}",
                ["HelpKill"] = "<color=#4DFF4D>/{0} {1}</color> -- 摧毁一辆 <color=#009EFF>{2}</color>",
                ["HelpKillCustom"] = "<color=#4DFF4D>/{0} {1}</color> 或者 <color=#4DFF4D>/{2}</color>  -- 摧毁一辆 <color=#009EFF>{3}</color>",

                ["NotAllowed"] = "您没有权限使用该命令",
                ["RaidBlocked"] = "<color=#FF1919>您被突袭阻止了，不能使用该命令</color>",
                ["CombatBlocked"] = "<color=#FF1919>您被战斗阻止了，不能使用该命令</color>",
                ["OptionNotFound"] = "选项 <color=#009EFF>{0}</color> 不存在",
                ["VehiclePurchased"] = "您购买了 <color=#009EFF>{0}</color>, 输入 <color=#4DFF4D>/{1}</color> 了解更多信息",
                ["VehicleAlreadyPurchased"] = "您已经购买了 <color=#009EFF>{0}</color>",
                ["VehicleCannotBeBought"] = "<color=#009EFF>{0}</color> 是不可购买的",
                ["VehicleNotOut"] = "您还没有生成您的 <color=#009EFF>{0}</color>, 输入 <color=#4DFF4D>/{1}</color> 了解更多信息",
                ["AlreadyVehicleOut"] = "您已经生成了您的 <color=#009EFF>{0}</color>, 输入 <color=#4DFF4D>/{1}</color> 了解更多信息",
                ["VehicleNotYetPurchased"] = "您还没有购买 <color=#009EFF>{0}</color>, 输入 <color=#4DFF4D>/{1}</color> 了解更多信息",
                ["VehicleSpawned"] = "您生成了您的 <color=#009EFF>{0}</color>",
                ["VehicleRecalled"] = "您召回了您的 <color=#009EFF>{0}</color>",
                ["VehicleKilled"] = "您摧毁了您的 <color=#009EFF>{0}</color>",
                ["VehicleOnSpawnCooldown"] = "您必须等待 <color=#FF1919>{0}</color> 秒，才能生成您的 <color=#009EFF>{1}</color>",
                ["VehicleOnRecallCooldown"] = "您必须等待 <color=#FF1919>{0}</color> 秒，才能召回您的 <color=#009EFF>{1}</color>",
                ["VehicleOnSpawnCooldownPay"] = "您必须等待 <color=#FF1919>{0}</color> 秒，才能生成您的 <color=#009EFF>{1}</color>。你可以使用 <color=#FF1919>/{2}</color> 命令支付 <color=#009EFF>{3}</color> 来绕过这个冷却时间",
                ["VehicleOnRecallCooldownPay"] = "您必须等待 <color=#FF1919>{0}</color> 秒，才能召回您的 <color=#009EFF>{1}</color>。你可以使用 <color=#FF1919>/{2}</color> 命令支付 <color=#009EFF>{3}</color> 来绕过这个冷却时间",
                ["NotLookingAtWater"] = "您必须看着水面才能生成您的 <color=#009EFF>{0}</color>",
                ["BuildingBlocked"] = "您没有领地柜权限，无法生成您的 <color=#009EFF>{0}</color>",
                ["RefundedVehicleItems"] = "您的 <color=#009EFF>{0}</color> 载具物品已经归还回您的库存",
                ["PlayerMountedOnVehicle"] = "您的 <color=#009EFF>{0}</color> 上坐着玩家，无法被召回或摧毁",
                ["PlayerInSafeZone"] = "您不能在安全区域内生成或召回您的 <color=#009EFF>{0}</color>",
                ["VehicleInventoryDropped"] = "您的 <color=#009EFF>{0}</color> 载具物品不能召回，它已经掉落在地上了",
                ["NoResourcesToPurchaseVehicle"] = "您没有足够的资源购买 <color=#009EFF>{0}</color>，还需要: \n{1}",
                ["NoResourcesToSpawnVehicle"] = "您没有足够的资源生成 <color=#009EFF>{0}</color>，还需要: \n{1}",
                ["NoResourcesToSpawnVehicleBypass"] = "您没有足够的资源绕过冷却时间来生成 <color=#009EFF>{0}</color>，还需要: \n{1}",
                ["NoResourcesToRecallVehicle"] = "您没有足够的资源召回 <color=#009EFF>{0}</color>，还需要: \n{1}",
                ["NoResourcesToRecallVehicleBypass"] = "您没有足够的资源绕过冷却时间来召回 <color=#009EFF>{0}</color>，还需要: \n{1}",
                ["MountedOrParented"] = "当您坐着或者在附着在实体上时无法生成或召回 <color=#009EFF>{0}</color>",
                ["RecallTooFar"] = "您必须在 <color=#FF1919>{0}</color> 米内才能召回您的 <color=#009EFF>{1}</color>",
                ["KillTooFar"] = "您必须在 <color=#FF1919>{0}</color> 米内才能摧毁您的 <color=#009EFF>{1}</color>",
                ["PlayersOnNearby"] = "您正在看着的位置附近有玩家时无法生成或召回 <color=#009EFF>{0}</color>",
                ["RecallWasBlocked"] = "有其他插件阻止您召回 <color=#009EFF>{0}</color>.",
                ["SpawnWasBlocked"] = "有其他插件阻止您生成 <color=#009EFF>{0}</color>.",
                ["VehiclesLimit"] = "您在同一时间内最多可以拥有 <color=#009EFF>{0}</color> 辆载具",
                ["TooFarTrainTrack"] = "您距离地铁轨道太远了",
                ["TooCloseTrainBarricadeOrWorkCart"] = "您距离地轨障碍物或其它地铁太近了",
                ["NotSpawnedOrRecalled"] = "由于某些原因，您的 <color=#009EFF>{0}</color> 载具无法生成或召回",

                ["CantUse"] = "您不能使用它，这个 {0} 属于 {1}"
            }, this, "zh-CN");
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Help"] = "Список доступных команд:",
                ["HelpLicence1"] = "<color=#4DFF4D>/{0}</color> -- Купить транспорт",
                ["HelpLicence2"] = "<color=#4DFF4D>/{0}</color> -- Создать транспорт",
                ["HelpLicence3"] = "<color=#4DFF4D>/{0}</color> -- Вызвать транспорт",
                ["HelpLicence4"] = "<color=#4DFF4D>/{0}</color> -- Уничтожить транспорт",
                ["HelpLicence5"] = "<color=#4DFF4D>/{0}</color> -- Купить, создать, или вызвать <color=#009EFF>{1}</color>",
                //["HelpLicence6"] = "<color=#4DFF4D>/{0}</color> -- Уничтожить <color=#009EFF>{1}</color>",

                ["PriceFormat"] = "<color=#FF1919>{0}</color> x{1}",
                ["HelpBuy"] = "<color=#4DFF4D>/{0} {1}</color> -- Купить <color=#009EFF>{2}</color>.",
                ["HelpBuyPrice"] = "<color=#4DFF4D>/{0} {1}</color> -- Купить <color=#009EFF>{2}</color>. Цена: {3}",
                ["HelpSpawn"] = "<color=#4DFF4D>/{0} {1}</color> -- Создать <color=#009EFF>{2}</color>",
                ["HelpSpawnPrice"] = "<color=#4DFF4D>/{0} {1}</color> -- Вызывать <color=#009EFF>{2}</color>. Цена: {3}",
                ["HelpRecall"] = "<color=#4DFF4D>/{0} {1}</color> -- Вызвать <color=#009EFF>{2}</color>",
                ["HelpRecallPrice"] = "<color=#4DFF4D>/{0} {1}</color> -- Вызвать <color=#009EFF>{2}</color>. Цена: {3}",
                ["HelpKill"] = "<color=#4DFF4D>/{0} {1}</color> -- Уничтожить <color=#009EFF>{2}</color>",
                ["HelpKillCustom"] = "<color=#4DFF4D>/{0} {1}</color> или же <color=#4DFF4D>/{2}</color>  -- Уничтожить <color=#009EFF>{3}</color>",

                ["NotAllowed"] = "У вас нет разрешения для использования данной команды.",
                ["RaidBlocked"] = "<color=#FF1919>Вы не можете это сделать из-за блокировки (рейд)</color>.",
                ["CombatBlocked"] = "<color=#FF1919>Вы не можете это сделать из-за блокировки (бой)</color>.",
                ["OptionNotFound"] = "Опция <color=#009EFF>{0}</color> не существует.",
                ["VehiclePurchased"] = "Вы приобрели <color=#009EFF>{0}</color>, напишите <color=#4DFF4D>/{1}</color> для получения дополнительной информации.",
                ["VehicleAlreadyPurchased"] = "Вы уже приобрели <color=#009EFF>{0}</color>.",
                ["VehicleCannotBeBought"] = "<color=#009EFF>{0}</color> приобрести невозможно",
                ["VehicleNotOut"] = "<color=#009EFF>{0}</color> отсутствует. Напишите <color=#4DFF4D>/{1}</color> для получения дополнительной информации.",
                ["AlreadyVehicleOut"] = "У вас уже есть <color=#009EFF>{0}</color>, напишите <color=#4DFF4D>/{1}</color>  для получения дополнительной информации.",
                ["VehicleNotYetPurchased"] = "Вы ещё не приобрели <color=#009EFF>{0}</color>. Напишите <color=#4DFF4D>/{1}</color> для получения дополнительной информации.",
                ["VehicleSpawned"] = "Вы создали ваш <color=#009EFF>{0}</color>.",
                ["VehicleRecalled"] = "Вы вызвали ваш <color=#009EFF>{0}</color>.",
                ["VehicleKilled"] = "Вы уничтожили ваш <color=#009EFF>{0}</color>.",
                ["VehicleOnSpawnCooldown"] = "Вам необходимо подождать <color=#FF1919>{0}</color> секунд прежде, чем создать свой <color=#009EFF>{1}</color>.",
                ["VehicleOnRecallCooldown"] = "Вам необходимо подождать <color=#FF1919>{0}</color> секунд прежде, чем вызвать свой <color=#009EFF>{1}</color>.",
                ["VehicleOnSpawnCooldownPay"] = "Вам необходимо подождать <color=#FF1919>{0}</color> секунд прежде, чем создать свой <color=#009EFF>{1}</color>. Вы можете обойти это время восстановления, используя команду <color=#FF1919>/{2}</color>, чтобы заплатить <color=#009EFF>{3}</color>",
                ["VehicleOnRecallCooldownPay"] = "Вам необходимо подождать <color=#FF1919>{0}</color> секунд прежде, чем вызвать свой <color=#009EFF>{1}</color>. Вы можете обойти это время восстановления, используя команду <color=#FF1919>/{2}</color>, чтобы заплатить <color=#009EFF>{3}</color>",
                ["NotLookingAtWater"] = "Вы должны смотреть на воду, чтобы создать или вызвать <color=#009EFF>{0}</color>.",
                ["BuildingBlocked"] = "Вы не можете создать <color=#009EFF>{0}</color> если отсутствует право строительства.",
                ["RefundedVehicleItems"] = "Запчасти от вашего <color=#009EFF>{0}</color> были возвращены в ваш инвентарь.",
                ["PlayerMountedOnVehicle"] = "Нельзя вызвать, когда игрок находится в вашем <color=#009EFF>{0}</color>.",
                ["PlayerInSafeZone"] = "Вы не можете создать, или вызвать ваш <color=#009EFF>{0}</color> в безопасной зоне.",
                ["VehicleInventoryDropped"] = "Инвентарь из вашего <color=#009EFF>{0}</color> не может быть вызван, он выброшен на землю.",
                ["NoResourcesToPurchaseVehicle"] = "У вас недостаточно ресурсов для покупки <color=#009EFF>{0}</color>. Вам не хватает: \n{1}",
                ["NoResourcesToSpawnVehicle"] = "У вас недостаточно ресурсов для покупки <color=#009EFF>{0}</color>. Вам не хватает: \n{1}",
                ["NoResourcesToSpawnVehicleBypass"] = "У вас недостаточно ресурсов для покупки <color=#009EFF>{0}</color>. Вам не хватает: \n{1}",
                ["NoResourcesToRecallVehicle"] = "У вас недостаточно ресурсов для покупки <color=#009EFF>{0}</color>. Вам не хватает: \n{1}",
                ["NoResourcesToRecallVehicleBypass"] = "У вас недостаточно ресурсов для покупки <color=#009EFF>{0}</color>. Вам не хватает: \n{1}",
                ["MountedOrParented"] = "Вы не можете создать <color=#009EFF>{0}</color> когда сидите или привязаны к объекту.",
                ["RecallTooFar"] = "Вы должны быть в пределах <color=#FF1919>{0}</color> метров от <color=#009EFF>{1}</color>, чтобы вызывать.",
                ["KillTooFar"] = "Вы должны быть в пределах <color=#FF1919>{0}</color> метров от <color=#009EFF>{1}</color>, уничтожить.",
                ["PlayersOnNearby"] = "Вы не можете создать <color=#009EFF>{0}</color> когда рядом с той позицией, на которую вы смотрите, есть игроки.",
                ["RecallWasBlocked"] = "Внешний плагин заблокировал вам вызвать <color=#009EFF>{0}</color>.",
                ["SpawnWasBlocked"] = "Внешний плагин заблокировал вам создать <color=#009EFF>{0}</color>.",
                ["VehiclesLimit"] = "У вас может быть до <color=#009EFF>{0}</color> автомобилей одновременно",
                ["TooFarTrainTrack"] = "Вы слишком далеко от железнодорожных путей",
                ["TooCloseTrainBarricadeOrWorkCart"] = "Вы слишком близко к железнодорожной баррикаде или рабочей тележке",
                ["NotSpawnedOrRecalled"] = "По какой-то причине ваш <color=#009EFF>{0}</color>  автомобилей не был вызван / отозван",

                ["CantUse"] = "Простите! Этот {0} принадлежит {1}. Вы не можете его использовать."
            }, this, "ru");
        }

        #endregion LanguageFile
    }
}