using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Automated Events", "k1lly0u/mspeedie/Arainrr", "1.0.11")]
    internal class AutomatedEvents : RustPlugin
    {
        #region Fields

        [PluginReference] private Plugin GUIAnnouncements;

        private const string PERMISSION_USE = "automatedevents.allowed";
        private const string PERMISSION_NEXT = "automatedevents.next";

        private const string PREFAB_APC = "assets/prefabs/npc/m2bradley/bradleyapc.prefab";
        private const string PREFAB_PLANE = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        private const string PREFAB_CHINOOK = "assets/prefabs/npc/ch47/ch47scientists.entity.prefab";
        private const string PREFAB_HELI = "assets/prefabs/npc/patrol helicopter/patrolhelicopter.prefab";
        private const string PREFAB_SHIP = "assets/content/vehicles/boats/cargoship/cargoshiptest.prefab";
        private const string PREFAB_SLEIGH = "assets/prefabs/misc/xmas/sleigh/santasleigh.prefab";
        private const string PREFAB_EASTER = "assets/prefabs/misc/easter/egghunt.prefab";
        private const string PREFAB_HALLOWEEN = "assets/prefabs/misc/halloween/halloweenhunt.prefab";
        private const string PREFAB_CHRISTMAS = "assets/prefabs/misc/xmas/xmasrefill.prefab";

        private static AutomatedEvents instance;
        private Dictionary<BaseEntity, EventType> _eventEntities;
        private readonly Dictionary<EventType, Timer> _eventTimers = new Dictionary<EventType, Timer>();
        private readonly Dictionary<EventSchedule, EventType> _disabledVanillaEvents = new Dictionary<EventSchedule, EventType>();

        private readonly Dictionary<string, EventType> _eventSchedulePrefabShortNames = new Dictionary<string, EventType>
        {
            ["event_airdrop"] = EventType.CargoPlane,
            ["event_cargoship"] = EventType.CargoShip,
            ["event_cargoheli"] = EventType.Chinook,
            ["event_helicopter"] = EventType.Helicopter,
            ["event_xmas"] = EventType.Christmas,
            ["event_easter"] = EventType.Easter,
            ["event_halloween"] = EventType.Halloween,
        };

        private enum EventType
        {
            None,
            Bradley,
            CargoPlane,
            CargoShip,
            Chinook,
            Helicopter,
            SantaSleigh,
            Christmas,
            Easter,
            Halloween
        }

        #endregion Fields

        #region Oxide Hooks

        private void Init()
        {
            instance = this;
            permission.RegisterPermission(PERMISSION_USE, this);
            permission.RegisterPermission(PERMISSION_NEXT, this);

            AddCovalenceCommand(configData.Chat.NextEventCommand, nameof(CmdNextEvent));
            AddCovalenceCommand(configData.Chat.RunEventCommand, nameof(CmdRunEvent));
            AddCovalenceCommand(configData.Chat.KillEventCommand, nameof(CmdKillEvent));

            var eventTypes = new List<EventType>(Enum.GetValues(typeof(EventType)).Cast<EventType>());
            if (!eventTypes.Any(x =>
            {
                if (x == EventType.None) return false;
                var baseEvent = GetBaseEvent(x);
                return baseEvent.Enabled && baseEvent.RestartTimerOnKill;
            }))
            {
                Unsubscribe(nameof(OnEntityKill));
            }
            else
            {
                _eventEntities = new Dictionary<BaseEntity, EventType>();
            }
        }

        private void OnServerInitialized(bool initial)
        {
            if (initial)
            {
                timer.Once(30f, InitializeEvents);
            }
            else
            {
                InitializeEvents();
            }
        }

        private void InitializeEvents()
        {
            ClearExistingEvents();
            foreach (EventType eventType in Enum.GetValues(typeof(EventType)))
            {
                if (eventType == EventType.None) continue;
                var baseEvent = GetBaseEvent(eventType);
                switch (eventType)
                {
                    case EventType.Bradley:
                        {
                            var bradleySpawner = BradleySpawner.singleton;
                            if (bradleySpawner != null)
                            {
                                if (baseEvent.DisableVanillaEvent)
                                {
                                    ConVar.Bradley.enabled = false;
                                    bradleySpawner.enabled = false;
                                    bradleySpawner.CancelInvoke(nameof(bradleySpawner.DelayedStart));
                                    bradleySpawner.CancelInvoke(nameof(bradleySpawner.CheckIfRespawnNeeded));
                                    PrintDebug($"The vanilla {eventType} event is disabled");
                                }
                            }
                            else if (baseEvent.Enabled)
                            {
                                PrintError("There is no Bradley Spawner on your server, so the Bradley event is disabled");
                                continue;
                            }
                        }
                        break;
                }
                if (baseEvent.Enabled)
                {
                    _eventTimers[eventType] = timer.Once(5f, () => StartEventTimer(eventType, configData.Global.AnnounceOnLoaded));
                }
            }
        }

        private void Unload()
        {
            foreach (EventType eventType in Enum.GetValues(typeof(EventType)))
            {
                switch (eventType)
                {
                    case EventType.Bradley:
                        {
                            var baseEvent = GetBaseEvent(eventType);
                            var bradleySpawner = BradleySpawner.singleton;
                            if (bradleySpawner != null && baseEvent.DisableVanillaEvent)
                            {
                                ConVar.Bradley.enabled = true;
                                bradleySpawner.enabled = true;
                                bradleySpawner.InvokeRepeating(nameof(bradleySpawner.CheckIfRespawnNeeded), 0f, 5f);
                                PrintDebug($"The vanilla {eventType} event is enabled");
                            }
                        }
                        continue;
                }
            }

            foreach (var entry in _disabledVanillaEvents)
            {
                entry.Key.enabled = true;
                PrintDebug($"The vanilla {entry.Value} event is enabled");
            }
            foreach (var value in _eventTimers.Values)
            {
                value?.Destroy();
            }
            instance = null;
        }

        private void OnEntityKill(BaseEntity entity)
        {
            if (entity == null) return;
            EventType eventType;
            if (!_eventEntities.TryGetValue(entity, out eventType)) return;
            StartEventTimer(eventType, onKill: true);
        }

        private object OnEventTrigger(TriggeredEventPrefab eventPrefab)
        {
            if (eventPrefab == null) return null;
            var prefabShortName = GetPrefabShortName(eventPrefab.name);
            if (string.IsNullOrEmpty(prefabShortName))
            {
                PrintError($"Failed to get prefab short name ({eventPrefab.name}). Please notify the plugin developer");
                return null;
            }
            EventType eventType;
            if (_eventSchedulePrefabShortNames.TryGetValue(prefabShortName, out eventType))
            {
                var baseEvent = GetBaseEvent(eventType);
                if (baseEvent.DisableVanillaEvent)
                {
                    var eventSchedule = eventPrefab.GetComponent<EventSchedule>();
                    if (eventSchedule == null)
                    {
                        PrintError($"{eventPrefab.name} has no EventSchedule component. Please notify the plugin developer");
                        return null;
                    }
                    eventSchedule.enabled = false;
                    _disabledVanillaEvents.Add(eventSchedule, eventType);
                    PrintDebug($"The vanilla {eventType} event is disabled", true);
                    return false;
                }
                if (!baseEvent.Enabled) return null;
                switch (eventType)
                {
                    case EventType.CargoPlane:
                        if (!CanRunEvent<CargoPlane>(eventType, baseEvent))
                        {
                            return false;
                        }
                        break;

                    case EventType.CargoShip:
                        if (!CanRunEvent<CargoShip>(eventType, baseEvent))
                        {
                            return false;
                        }
                        break;

                    case EventType.Chinook:
                        if (!CanRunEvent<CH47HelicopterAIController>(eventType, baseEvent))
                        {
                            return false;
                        }
                        break;

                    case EventType.Helicopter:
                        if (!CanRunEvent<BaseHelicopter>(eventType, baseEvent))
                        {
                            return false;
                        }
                        break;

                    case EventType.Christmas:
                        if (!CanRunEvent<XMasRefill>(eventType, baseEvent))
                        {
                            return false;
                        }
                        break;

                    case EventType.Easter:
                        if (!CanRunHuntEvent<EggHuntEvent>(eventType, baseEvent))
                        {
                            return false;
                        }
                        break;

                    case EventType.Halloween:
                        if (!CanRunHuntEvent<HalloweenHunt>(eventType, baseEvent))
                        {
                            return false;
                        }
                        break;

                    default:
                        PrintError($"The vanilla {eventType} event was triggered, but not handled. Please notify the plugin developer");
                        return null;
                }
                if (configData.Global.AnnounceEventTriggered)
                {
                    SendEventTriggeredMessage(eventType.ToString());
                }
                return null;
            }
            PrintError($"Unknown Vanilla Event Schedule: {eventPrefab.name} ({prefabShortName})");
            return null;
        }

        #endregion Oxide Hooks

        #region Methods

        private void ClearExistingEvents()
        {
            var eventTypes = new Dictionary<EventType, bool>();
            foreach (EventType eventType in Enum.GetValues(typeof(EventType)))
            {
                if (eventType == EventType.None) continue;
                var baseEvent = GetBaseEvent(eventType);
                if (baseEvent.Enabled && baseEvent.KillEventOnLoaded)
                {
                    var excludePlayerEntity = (baseEvent as CoexistEvent)?.ExcludePlayerEntity ?? false;
                    eventTypes.Add(eventType, excludePlayerEntity);
                }
            }

            if (eventTypes.Count <= 0) return;
            foreach (var baseEntity in BaseNetworkable.serverEntities.OfType<BaseEntity>().ToArray())
            {
                var eventType = GetEventTypeFromEntity(baseEntity);
                if (eventType == EventType.None) continue;
                bool excludePlayerEntity;
                if (eventTypes.TryGetValue(eventType, out excludePlayerEntity))
                {
                    if (excludePlayerEntity && baseEntity.OwnerID.IsSteamId()) continue;
                    PrintDebug($"Killing a {eventType}");
                    baseEntity.Kill();
                }
            }
        }

        private void StartEventTimer(EventType eventType, bool announce = true, float timeOverride = 0f, bool onKill = false)
        {
            if (eventType == EventType.None) return;
            var baseEvent = GetBaseEvent(eventType);
            if (!baseEvent.Enabled)
            {
                PrintDebug($"Unable to running {eventType} event, because the event is disabled");
                return;
            }
            var randomTime = timeOverride <= 0f
                ? baseEvent.MinimumTimeBetween <= baseEvent.MaximumTimeBetween
                    ? UnityEngine.Random.Range(baseEvent.MinimumTimeBetween, baseEvent.MaximumTimeBetween)
                    : UnityEngine.Random.Range(baseEvent.MaximumTimeBetween, baseEvent.MinimumTimeBetween)
                : timeOverride;
            randomTime += baseEvent.StartOffset;
            var nextDateTime = DateTime.UtcNow.AddMinutes(randomTime);
            baseEvent.NextRunTime = Facepunch.Math.Epoch.FromDateTime(nextDateTime);

            Timer value;
            if (_eventTimers.TryGetValue(eventType, out value))
            {
                value?.Destroy();
            }
            _eventTimers[eventType] = timer.Once(randomTime * 60f, () => RunEvent(eventType));

            if (onKill || !baseEvent.RestartTimerOnKill)
            {
                var timeLeft = TimeSpan.FromSeconds(baseEvent.NextRunTime - Facepunch.Math.Epoch.Current).ToShortString();
                PrintDebug($"Next {eventType} event will be ran after {timeLeft}");
                if (announce && baseEvent.AnnounceNext)
                {
                    SendEventNextRunMessage(eventType, timeLeft);
                }
            }
        }

        private void RunEvent(EventType eventType, bool runOnce = false, bool bypass = false)
        {
            if (eventType == EventType.None) return;
            BaseEntity eventEntity = null;
            string eventTypeStr = null;
            var baseEvent = GetBaseEvent(eventType);
            switch (eventType)
            {
                case EventType.Bradley:
                    {
                        if (bypass || CanRunEvent<BradleyAPC>(eventType, baseEvent, false))
                        {
                            var eventWeight = baseEvent.GetRandomEventWeight();
                            if (eventWeight == null || eventWeight.IsNormalEvent)
                            {
                                var bradleySpawner = BradleySpawner.singleton;
                                if (bradleySpawner == null || bradleySpawner.path?.interestZones == null)
                                {
                                    PrintError("There is no Bradley Spawner on your server, so you cannot spawn a Bradley");
                                    return;
                                }
                                PrintDebug("Spawning Bradley");
                                var bradley = GameManager.server.CreateEntity(PREFAB_APC) as BradleyAPC;
                                if (bradley == null)
                                {
                                    goto NotifyDeveloper;
                                }
                                bradley.Spawn();
                                eventEntity = bradley;
                                eventTypeStr = eventType.ToString();

                                var position = bradleySpawner.path.interestZones[UnityEngine.Random.Range(0, bradleySpawner.path.interestZones.Count)].transform.position;
                                bradley.transform.position = position;
                                bradley.DoAI = true;
                                bradley.InstallPatrolPath(bradleySpawner.path);
                            }
                            else
                            {
                                PrintDebug($"Spawning {eventWeight.Name}");
                                eventWeight.RunCustomEvent();
                                eventTypeStr = eventWeight.Name;
                            }
                        }
                    }
                    break;

                case EventType.CargoPlane:
                    {
                        if (bypass || CanRunEvent<CargoPlane>(eventType, baseEvent, false))
                        {
                            var eventWeight = baseEvent.GetRandomEventWeight();
                            if (eventWeight == null || eventWeight.IsNormalEvent)
                            {
                                PrintDebug("Spawning Cargo Plane");
                                var plane = GameManager.server.CreateEntity(PREFAB_PLANE) as CargoPlane;
                                if (plane == null)
                                {
                                    goto NotifyDeveloper;
                                }
                                plane.Spawn();
                                eventEntity = plane;
                                eventTypeStr = eventType.ToString();
                            }
                            else
                            {
                                PrintDebug($"Spawning {eventWeight.Name}");
                                eventWeight.RunCustomEvent();
                                eventTypeStr = eventWeight.Name;
                            }
                        }
                    }
                    break;

                case EventType.CargoShip:
                    {
                        if (bypass || CanRunEvent<CargoShip>(eventType, baseEvent, false))
                        {
                            var eventWeight = baseEvent.GetRandomEventWeight();
                            if (eventWeight == null || eventWeight.IsNormalEvent)
                            {
                                PrintDebug("Spawning Cargo Ship");
                                var ship = GameManager.server.CreateEntity(PREFAB_SHIP) as CargoShip;
                                if (ship == null)
                                {
                                    goto NotifyDeveloper;
                                }
                                ship.TriggeredEventSpawn();
                                ship.Spawn();
                                eventEntity = ship;
                                eventTypeStr = eventType.ToString();
                            }
                            else
                            {
                                PrintDebug($"Spawning {eventWeight.Name}");
                                eventWeight.RunCustomEvent();
                                eventTypeStr = eventWeight.Name;
                            }
                        }
                    }
                    break;

                case EventType.Chinook:
                    {
                        if (bypass || CanRunEvent<CH47HelicopterAIController>(eventType, baseEvent, false, entity => entity.landingTarget == Vector3.zero))
                        {
                            var eventWeight = baseEvent.GetRandomEventWeight();
                            if (eventWeight == null || eventWeight.IsNormalEvent)
                            {
                                PrintDebug("Spawning Chinook");
                                var chinook = GameManager.server.CreateEntity(PREFAB_CHINOOK) as CH47HelicopterAIController;
                                if (chinook == null)
                                {
                                    goto NotifyDeveloper;
                                }

                                chinook.TriggeredEventSpawn();
                                chinook.Spawn();
                                eventEntity = chinook;
                                eventTypeStr = eventType.ToString();
                            }
                            else
                            {
                                PrintDebug($"Spawning {eventWeight.Name}");
                                eventWeight.RunCustomEvent();
                                eventTypeStr = eventWeight.Name;
                            }
                        }
                    }
                    break;

                case EventType.Helicopter:
                    {
                        if (bypass || CanRunEvent<BaseHelicopter>(eventType, baseEvent, false))
                        {
                            var eventWeight = baseEvent.GetRandomEventWeight();
                            if (eventWeight == null || eventWeight.IsNormalEvent)
                            {
                                PrintDebug("Spawning Helicopter");
                                var helicopter = GameManager.server.CreateEntity(PREFAB_HELI) as BaseHelicopter;
                                if (helicopter == null)
                                {
                                    goto NotifyDeveloper;
                                }
                                helicopter.Spawn();
                                eventEntity = helicopter;
                                eventTypeStr = eventType.ToString();
                            }
                            else
                            {
                                PrintDebug($"Spawning {eventWeight.Name}");
                                eventWeight.RunCustomEvent();
                                eventTypeStr = eventWeight.Name;
                            }
                        }
                    }
                    break;

                case EventType.SantaSleigh:
                    {
                        if (bypass || CanRunEvent<SantaSleigh>(eventType, baseEvent, false))
                        {
                            var eventWeight = baseEvent.GetRandomEventWeight();
                            if (eventWeight == null || eventWeight.IsNormalEvent)
                            {
                                PrintDebug("Santa Sleigh is coming, have you been good?");
                                var santaSleigh = GameManager.server.CreateEntity(PREFAB_SLEIGH) as SantaSleigh;
                                if (santaSleigh == null)
                                {
                                    goto NotifyDeveloper;
                                }

                                santaSleigh.Spawn();
                                eventEntity = santaSleigh;
                                eventTypeStr = eventType.ToString();
                            }
                            else
                            {
                                PrintDebug($"Spawning {eventWeight.Name}");
                                eventWeight.RunCustomEvent();
                                eventTypeStr = eventWeight.Name;
                            }
                        }
                    }
                    break;

                case EventType.Christmas:
                    {
                        if (bypass || CanRunEvent<XMasRefill>(eventType, baseEvent, false))
                        {
                            var eventWeight = baseEvent.GetRandomEventWeight();
                            if (eventWeight == null || eventWeight.IsNormalEvent)
                            {
                                PrintDebug("Christmas Refill is occurring");
                                var xMasRefill = GameManager.server.CreateEntity(PREFAB_CHRISTMAS) as XMasRefill;
                                if (xMasRefill == null)
                                {
                                    goto NotifyDeveloper;
                                }

                                bool temp = ConVar.XMas.enabled;
                                ConVar.XMas.enabled = true;
                                xMasRefill.Spawn();
                                ConVar.XMas.enabled = temp;
                                eventEntity = xMasRefill;
                                eventTypeStr = eventType.ToString();
                            }
                            else
                            {
                                PrintDebug($"Spawning {eventWeight.Name}");
                                eventWeight.RunCustomEvent();
                                eventTypeStr = eventWeight.Name;
                            }
                        }
                    }
                    break;

                case EventType.Easter:
                    {
                        if (bypass || CanRunHuntEvent<EggHuntEvent>(eventType, baseEvent, false))
                        {
                            if (EggHuntEvent.serverEvent != null) //EggHuntEvent.serverEvent.IsEventActive()
                            {
                                var timeLeft = EggHuntEvent.durationSeconds - EggHuntEvent.serverEvent.timeAlive + EggHuntEvent.serverEvent.warmupTime + 60f;
                                PrintDebug($"There is an {(EggHuntEvent.serverEvent.ShortPrefabName == "egghunt" ? eventType : EventType.Halloween)} event running, so the {eventType} event will be delayed until {Mathf.RoundToInt(timeLeft)} seconds later", true);
                                if (!runOnce)
                                {
                                    StartEventTimer(eventType, timeOverride: timeLeft / 60f);
                                }

                                return;
                            }

                            PrintDebug("Happy Easter Egg Hunt is occurring");
                            var eggHuntEvent = GameManager.server.CreateEntity(PREFAB_EASTER) as EggHuntEvent;
                            if (eggHuntEvent == null)
                            {
                                goto NotifyDeveloper;
                            }

                            eggHuntEvent.Spawn();
                            eventEntity = eggHuntEvent;
                            eventTypeStr = eventType.ToString();
                        }
                    }
                    break;

                case EventType.Halloween:
                    {
                        if (bypass || CanRunHuntEvent<HalloweenHunt>(eventType, baseEvent, false))
                        {
                            if (EggHuntEvent.serverEvent != null) //EggHuntEvent.serverEvent.IsEventActive()
                            {
                                var timeLeft = EggHuntEvent.durationSeconds - EggHuntEvent.serverEvent.timeAlive + EggHuntEvent.serverEvent.warmupTime + 60f;
                                PrintDebug($"There is an {(EggHuntEvent.serverEvent.ShortPrefabName == "egghunt" ? EventType.Easter : eventType)} event running, so the {eventType} event will be delayed until {Mathf.RoundToInt(timeLeft)} seconds later", true);
                                if (!runOnce)
                                {
                                    StartEventTimer(eventType, timeOverride: timeLeft / 60f);
                                }

                                return;
                            }

                            PrintDebug("Spooky Halloween Hunt is occurring");
                            var halloweenHunt = GameManager.server.CreateEntity(PREFAB_HALLOWEEN) as HalloweenHunt;
                            if (halloweenHunt == null)
                            {
                                goto NotifyDeveloper;
                            }

                            halloweenHunt.Spawn();
                            eventEntity = halloweenHunt;
                            eventTypeStr = eventType.ToString();
                        }
                    }
                    break;

                default:
                    PrintError($"RunEvent: Unknown EventType: {eventType}");
                    return;
            }

            if (eventEntity != null && baseEvent.Enabled && baseEvent.RestartTimerOnKill)
            {
                foreach (var entry in _eventEntities.ToArray())
                {
                    if (entry.Value == eventType)
                    {
                        _eventEntities.Remove(entry.Key);
                    }
                }
                _eventEntities.Add(eventEntity, eventType);
            }
            if (!string.IsNullOrEmpty(eventTypeStr))
            {
                if (configData.Global.AnnounceEventTriggered)
                {
                    SendEventTriggeredMessage(eventTypeStr);
                }
                Interface.CallHook("OnAutoEventTriggered", eventTypeStr, eventEntity, runOnce);
            }
            if (!runOnce)
            {
                StartEventTimer(eventType);
            }
            return;
        NotifyDeveloper:
            {
                PrintError($"{eventType} prefab does not exist. Please notify the plugin developer");
            }
        }

        private void KillEvent(EventType eventType)
        {
            var baseEvent = GetBaseEvent(eventType);
            switch (eventType)
            {
                case EventType.Bradley:
                    foreach (var bradley in GetEventEntities<BradleyAPC>(baseEvent).ToArray())
                    {
                        PrintDebug("Killing a Bradley");
                        bradley.Kill();
                    }
                    return;

                case EventType.CargoPlane:
                    foreach (var cargoPlane in GetEventEntities<CargoPlane>(baseEvent).ToArray())
                    {
                        PrintDebug("Killing a Cargo Plane");
                        cargoPlane.Kill();
                    }
                    return;

                case EventType.CargoShip:
                    foreach (var cargoShip in GetEventEntities<CargoShip>(baseEvent).ToArray())
                    {
                        PrintDebug("Killing a Cargo Ship");
                        cargoShip.Kill();
                    }
                    return;

                case EventType.Chinook:
                    foreach (var ch47Helicopter in GetEventEntities<CH47HelicopterAIController>(baseEvent, entity => entity.landingTarget == Vector3.zero).ToArray())
                    {
                        PrintDebug("Killing a Chinook (CH47)");
                        ch47Helicopter.Kill();
                    }
                    return;

                case EventType.Helicopter:
                    foreach (var helicopter in GetEventEntities<BaseHelicopter>(baseEvent).ToArray())
                    {
                        PrintDebug("Killing a Helicopter");
                        helicopter.Kill();
                    }
                    return;

                case EventType.SantaSleigh:
                    foreach (var santaSleigh in GetEventEntities<SantaSleigh>(baseEvent).ToArray())
                    {
                        PrintDebug("Killing a Santa Sleigh");
                        santaSleigh.Kill();
                    }
                    return;

                case EventType.Christmas:
                    foreach (var christmas in GetEventEntities<XMasRefill>(baseEvent).ToArray())
                    {
                        PrintDebug("Killing a Christmas");
                        christmas.Kill();
                    }
                    return;

                case EventType.Easter:
                    foreach (var easter in GetEventEntities<EggHuntEvent>(baseEvent, entity => entity.ShortPrefabName == "egghunt").ToArray())
                    {
                        PrintDebug("Killing a Easter");
                        easter.Kill();
                    }
                    return;

                case EventType.Halloween:
                    foreach (var halloween in GetEventEntities<HalloweenHunt>(baseEvent).ToArray())
                    {
                        PrintDebug("Killing a Halloween");
                        halloween.Kill();
                    }
                    return;

                default:
                    PrintError($"KillEvent: Unknown EventType: {eventType}");
                    return;
            }
        }

        private bool GetNextEventRunTime(IPlayer iPlayer, EventType eventType, out string nextTime)
        {
            var baseEvent = GetBaseEvent(eventType);
            if (!baseEvent.Enabled || baseEvent.NextRunTime <= 0)
            {
                nextTime = Lang("NotSet", iPlayer.Id, baseEvent.DisplayName);
                return false;
            }
            var timeLeft = TimeSpan.FromSeconds(baseEvent.NextRunTime - Facepunch.Math.Epoch.Current).ToShortString();
            nextTime = Lang("NextRunTime", iPlayer.Id, baseEvent.DisplayName, timeLeft);
            return true;
        }

        private BaseEvent GetBaseEvent(EventType eventType)
        {
            switch (eventType)
            {
                case EventType.Bradley: return configData.Events.Bradley;
                case EventType.CargoPlane: return configData.Events.Plane;
                case EventType.CargoShip: return configData.Events.Ship;
                case EventType.Chinook: return configData.Events.Chinook;
                case EventType.Helicopter: return configData.Events.Helicopter;
                case EventType.SantaSleigh: return configData.Events.SantaSleigh;
                case EventType.Christmas: return configData.Events.Christmas;
                case EventType.Easter: return configData.Events.Easter;
                case EventType.Halloween: return configData.Events.Halloween;
                default: PrintError($"GetBaseEventS: Unknown EventType: {eventType}"); return null;
            }
        }

        private string GetEventTypeDisplayName(EventType eventType)
        {
            if (eventType == EventType.None) return "None";
            var baseEvent = GetBaseEvent(eventType);
            return baseEvent.DisplayName;
        }

        private void SendEventNextRunMessage(EventType eventType, string timeLeft)
        {
            if (configData.Global.UseGuiAnnouncements && GUIAnnouncements != null)
            {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    GUIAnnouncements.Call("CreateAnnouncement", Lang("NextRunTime", player.UserIDString, GetEventTypeDisplayName(eventType), timeLeft), "Purple", "White", player);
                }
            }
            else
            {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    Print(player, Lang("NextRunTime", player.UserIDString, GetEventTypeDisplayName(eventType), timeLeft));
                }
            }
        }

        private void SendEventTriggeredMessage(string eventTypeStr)
        {
            if (configData.Global.UseGuiAnnouncements && GUIAnnouncements != null)
            {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    var message = Lang(eventTypeStr, player.UserIDString);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        GUIAnnouncements.Call("CreateAnnouncement", message, "Purple", "White", player);
                    }
                }
            }
            else
            {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    var message = Lang(eventTypeStr, player.UserIDString);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        Print(player, message);
                    }
                }
            }
        }

        #endregion Methods

        #region Commands

        private void CmdNextEvent(IPlayer iPlayer, string command, string[] args)
        {
            if (!iPlayer.IsAdmin && !iPlayer.HasPermission(PERMISSION_NEXT))
            {
                Print(iPlayer, Lang("NotAllowed", iPlayer.Id, command));
                return;
            }
            if (args == null || args.Length < 1)
            {
                Print(iPlayer, Lang("BlankEvent", iPlayer.Id));
                return;
            }

            var argString = args[0].ToLower();
            switch (argString)
            {
                case "*":
                case "all":
                    {
                        StringBuilder stringBuilder = new StringBuilder();
                        stringBuilder.AppendLine();
                        foreach (EventType eventType in Enum.GetValues(typeof(EventType)))
                        {
                            if (eventType == EventType.None) continue;
                            string result;
                            if (GetNextEventRunTime(iPlayer, eventType, out result))
                            {
                                stringBuilder.AppendLine(result);
                            }
                        }
                        Print(iPlayer, stringBuilder.ToString());
                    }
                    return;

                default:
                    {
                        var eventType = GetEventTypeFromStr(argString);
                        if (eventType == EventType.None)
                        {
                            Print(iPlayer, Lang("UnknownEvent", iPlayer.Id, args[0]));
                            return;
                        }
                        string result;
                        GetNextEventRunTime(iPlayer, eventType, out result);
                        Print(iPlayer, result);
                    }
                    return;
            }
        }

        private void CmdRunEvent(IPlayer iPlayer, string command, string[] args)
        {
            if (!iPlayer.IsAdmin && !iPlayer.HasPermission(PERMISSION_USE))
            {
                Print(iPlayer, Lang("NotAllowed", iPlayer.Id, command));
                return;
            }
            if (args == null || args.Length < 1)
            {
                Print(iPlayer, Lang("BlankEvent", iPlayer.Id));
                return;
            }

            var eventType = GetEventTypeFromStr(args[0].ToLower());
            if (eventType == EventType.None)
            {
                Print(iPlayer, Lang("UnknownEvent", iPlayer.Id, args[0]));
                return;
            }
            RunEvent(eventType, true, true);
            Print(iPlayer, Lang("Running", iPlayer.Id, iPlayer.Name, GetEventTypeDisplayName(eventType)));
        }

        private void CmdKillEvent(IPlayer iPlayer, string command, string[] args)
        {
            if (!iPlayer.IsAdmin && !iPlayer.HasPermission(PERMISSION_USE))
            {
                Print(iPlayer, Lang("NotAllowed", iPlayer.Id, command));
                return;
            }
            if (args == null || args.Length < 1)
            {
                Print(iPlayer, Lang("BlankEvent", iPlayer.Id));
                return;
            }

            var eventType = GetEventTypeFromStr(args[0].ToLower());
            if (eventType == EventType.None)
            {
                Print(iPlayer, Lang("UnknownEvent", iPlayer.Id, args[0]));
                return;
            }
            KillEvent(eventType);
            Print(iPlayer, Lang("Removing", iPlayer.Id, iPlayer.Name, GetEventTypeDisplayName(eventType)));
        }

        #endregion Commands

        #region Helpers

        private static string GetPrefabShortName(string prefabName) => Utility.GetFileNameWithoutExtension(prefabName);

        private static EventType GetEventTypeFromStr(string eventTypeStr)
        {
            if (eventTypeStr.Contains("brad"))
                return EventType.Bradley;
            if (eventTypeStr.Contains("heli") || eventTypeStr.Contains("copter"))
                return EventType.Helicopter;
            if (eventTypeStr.Contains("plane"))
                return EventType.CargoPlane;
            if (eventTypeStr.Contains("ship"))
                return EventType.CargoShip;
            if (eventTypeStr.Contains("ch47") || eventTypeStr.Contains("chin"))
                return EventType.Chinook;
            if (eventTypeStr.Contains("xmas") || eventTypeStr.Contains("chris") || eventTypeStr.Contains("yule"))
                return EventType.Christmas;
            if (eventTypeStr.Contains("santa") || eventTypeStr.Contains("nick") || eventTypeStr.Contains("wodan"))
                return EventType.SantaSleigh;
            if (eventTypeStr.Contains("easter") || eventTypeStr.Contains("egg") || eventTypeStr.Contains("bunny"))
                return EventType.Easter;
            if (eventTypeStr.Contains("hall") || eventTypeStr.Contains("spooky") || eventTypeStr.Contains("candy") || eventTypeStr.Contains("samhain"))
                return EventType.Halloween;
            return EventType.None;
        }

        private static EventType GetEventTypeFromEntity(BaseEntity baseEntity)
        {
            if (baseEntity is BradleyAPC) return EventType.Bradley;
            if (baseEntity is CargoPlane) return EventType.CargoPlane;
            if (baseEntity is CargoShip) return EventType.CargoShip;
            if (baseEntity is BaseHelicopter) return EventType.Helicopter;
            if (baseEntity is SantaSleigh) return EventType.SantaSleigh;
            if (baseEntity is XMasRefill) return EventType.Christmas;
            if (baseEntity is HalloweenHunt) return EventType.Halloween;
            if (baseEntity is EggHuntEvent) return EventType.Easter;
            var ch47HelicopterAiController = baseEntity as CH47HelicopterAIController;
            if (ch47HelicopterAiController != null && ch47HelicopterAiController.landingTarget == Vector3.zero) return EventType.Chinook;
            return EventType.None;
        }

        private static int GetEventIndexFromWeight(Dictionary<int, float> weightDict)
        {
            if (weightDict.Count <= 0) return 0;
            if (weightDict.Count == 1) return weightDict.Keys.FirstOrDefault();
            var sum = weightDict.Sum(x => x.Value);
            var rand = UnityEngine.Random.Range(0f, sum);
            foreach (var entry in weightDict)
            {
                if ((rand -= entry.Value) <= 0f)
                {
                    return entry.Key;
                }
            }
            return 0;
        }

        private static IEnumerable<T> GetEventEntities<T>(BaseEvent baseEvent, Func<T, bool> filter = null) where T : BaseEntity
        {
            var excludePlayerEntity = (baseEvent as CoexistEvent)?.ExcludePlayerEntity ?? false;
            foreach (var serverEntity in BaseNetworkable.serverEntities)
            {
                var entity = serverEntity as T;
                if (entity == null) continue;
                if (excludePlayerEntity && entity.OwnerID.IsSteamId()) continue;
                if (filter != null && !filter(entity)) continue;
                yield return entity;
            }
        }

        private static bool CanRunEvent<T>(EventType eventType, BaseEvent baseEvent, bool vanilla = true, Func<T, bool> filter = null) where T : BaseEntity
        {
            return CheckOnlinePlayers(eventType, baseEvent, vanilla) && CanRunCoexistEvent(eventType, baseEvent, vanilla, filter);
        }

        private static bool CheckOnlinePlayers(EventType eventType, BaseEvent baseEvent, bool vanilla = true)
        {
            var onlinePlayers = BasePlayer.activePlayerList.Count;
            if (baseEvent.MinimumOnlinePlayers > 0 && onlinePlayers < baseEvent.MinimumOnlinePlayers)
            {
                instance?.PrintDebug($"The online players is less than {baseEvent.MinimumOnlinePlayers}, so the {eventType} {(vanilla ? "vanilla" : "auto")} event cannot run", true);
                return false;
            }
            if (baseEvent.MaximumOnlinePlayers > 0 && onlinePlayers > baseEvent.MaximumOnlinePlayers)
            {
                instance?.PrintDebug($"The online players is greater than {baseEvent.MaximumOnlinePlayers}, so the {eventType} {(vanilla ? "vanilla" : "auto")} event cannot run", true);
                return false;
            }
            return true;
        }

        private static bool CanRunCoexistEvent<T>(EventType eventType, BaseEvent baseEvent, bool vanilla = true, Func<T, bool> filter = null) where T : BaseEntity
        {
            var coexistEventS = baseEvent as CoexistEvent;
            if (coexistEventS != null && coexistEventS.ServerMaximumNumber > 0)
            {
                if (BaseNetworkable.serverEntities.Count(x =>
                {
                    var entity = x as T;
                    if (entity == null) return false;
                    if (filter != null && !filter(entity)) return false;
                    return !coexistEventS.ExcludePlayerEntity || !entity.OwnerID.IsSteamId();
                }) >= coexistEventS.ServerMaximumNumber)
                {
                    instance?.PrintDebug($"The number of {eventType} {(vanilla ? "vanilla" : "auto")} events has reached the limit of {coexistEventS.ServerMaximumNumber}", true);
                    return false;
                }
            }
            return true;
        }

        private static bool CanRunHuntEvent<T>(EventType eventType, BaseEvent baseEvent, bool vanilla = true) where T : EggHuntEvent
        {
            if (!CheckOnlinePlayers(eventType, baseEvent, vanilla)) return false;
            return true;
        }

        #endregion Helpers

        #region Debug

        private void PrintDebug(string message, bool warning = false)
        {
            if (configData.Global.DebugEnabled)
            {
                if (warning) PrintWarning(message);
                else Puts(message);
            }
        }

        #endregion Debug

        #region ConfigurationFile

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Settings")]
            public Settings Global { get; set; } = new Settings();

            public class Settings
            {
                [JsonProperty(PropertyName = "Enable Debug Mode")]
                public bool DebugEnabled { get; set; } = true;

                [JsonProperty(PropertyName = "Announce On Plugin Loaded")]
                public bool AnnounceOnLoaded { get; set; }

                [JsonProperty(PropertyName = "Announce On Event Triggered")]
                public bool AnnounceEventTriggered { get; set; }

                [JsonProperty(PropertyName = "Use GUIAnnouncements Plugin")]
                public bool UseGuiAnnouncements { get; set; }
            }

            [JsonProperty(PropertyName = "Chat Settings")]
            public ChatSettings Chat { get; set; } = new ChatSettings();

            public class ChatSettings
            {
                [JsonProperty(PropertyName = "Next Event Command")]
                public string NextEventCommand { get; set; } = "nextevent";

                [JsonProperty(PropertyName = "Run Event Command")]
                public string RunEventCommand { get; set; } = "runevent";

                [JsonProperty(PropertyName = "Kill Event Command")]
                public string KillEventCommand { get; set; } = "killevent";

                [JsonProperty(PropertyName = "Chat Prefix")]
                public string Prefix { get; set; } = "[AutomatedEvents]: ";

                [JsonProperty(PropertyName = "Chat Prefix Color")]
                public string PrefixColor { get; set; } = "#00FFFF";

                [JsonProperty(PropertyName = "Chat SteamID Icon")]
                public ulong SteamIdIcon { get; set; } = 0;
            }

            [JsonProperty(PropertyName = "Event Settings")]
            public EventSettings Events { get; set; } = new EventSettings();

            public class EventSettings
            {
                [JsonProperty(PropertyName = "Bradley Event")]
                public CoexistEvent Bradley { get; set; } = new CoexistEvent
                {
                    DisplayName = "Bradley",
                    MinimumTimeBetween = 30,
                    MaximumTimeBetween = 45,
                };

                [JsonProperty(PropertyName = "Cargo Plane Event")]
                public CoexistEvent Plane { get; set; } = new CoexistEvent
                {
                    DisplayName = "Cargo Plane",
                    MinimumTimeBetween = 30,
                    MaximumTimeBetween = 45,
                    EventWeights = new List<EventWeight>
                    {
                        new EventWeight {Weight = 60},
                        new EventWeight
                        {
                            Name = "FancyDrop",
                            Weight = 20,
                            ArgType = ArgumentType.Command,
                            Args = new List<string> { "ad.random" }
                        },
                        new EventWeight
                        {
                            Name = "PlaneCrash",
                            Weight = 20,
                            ArgType = ArgumentType.Command,
                            Args = new List<string> { "callcrash" }
                        }
                    }
                };

                [JsonProperty(PropertyName = "Cargo Ship Event")]
                public CoexistEvent Ship { get; set; } = new CoexistEvent
                {
                    DisplayName = "Cargo Ship",
                    MinimumTimeBetween = 30,
                    MaximumTimeBetween = 45,
                    EventWeights = new List<EventWeight>
                    {
                        new EventWeight {Weight = 80},
                        new EventWeight
                        {
                            Name = "RustTanic",
                            Weight = 20,
                            ArgType = ArgumentType.Command,
                            Args = new List<string> { "calltitanic" }
                        }
                    }
                };

                [JsonProperty(PropertyName = "Chinook (CH47) Event")]
                public CoexistEvent Chinook { get; set; } = new CoexistEvent
                {
                    DisplayName = "Chinook",
                    MinimumTimeBetween = 30,
                    MaximumTimeBetween = 45
                };

                [JsonProperty(PropertyName = "Helicopter Event")]
                public CoexistEvent Helicopter { get; set; } = new CoexistEvent
                {
                    DisplayName = "Helicopter",
                    MinimumTimeBetween = 45,
                    MaximumTimeBetween = 60,
                    EventWeights = new List<EventWeight>
                    {
                        new EventWeight {Weight = 60},
                        new EventWeight
                        {
                            Name = "HeliRefuel",
                            Weight = 20,
                            ArgType = ArgumentType.Command,
                            Args = new List<string> { "pe call" }
                        },
                        new EventWeight
                        {
                            Name = "PilotEject",
                            Weight = 20,
                            ArgType = ArgumentType.Command,
                            Args = new List<string> { "hr call" }
                        }
                    }
                };

                [JsonProperty(PropertyName = "Santa Sleigh Event")]
                public CoexistEvent SantaSleigh { get; set; } = new CoexistEvent
                {
                    DisplayName = "Santa Sleigh",
                    MinimumTimeBetween = 30,
                    MaximumTimeBetween = 60
                };

                [JsonProperty(PropertyName = "Christmas Event")]
                public CoexistEvent Christmas { get; set; } = new CoexistEvent
                {
                    DisplayName = "Christmas",
                    MinimumTimeBetween = 60,
                    MaximumTimeBetween = 120,
                    EventWeights = new List<EventWeight>
                    {
                        new EventWeight {Weight = 80},
                        new EventWeight
                        {
                            Name = "AlphaChristmas",
                            Weight = 20,
                            ArgType = ArgumentType.Command,
                            Args = new List<string> {"alphachristmas.refill"}
                        }
                    }
                };

                [JsonProperty(PropertyName = "Easter Event")]
                public BaseEvent Easter { get; set; } = new BaseEvent
                {
                    DisplayName = "Easter",
                    MinimumTimeBetween = 30,
                    MaximumTimeBetween = 60
                };

                [JsonProperty(PropertyName = "Halloween Event")]
                public BaseEvent Halloween { get; set; } = new BaseEvent
                {
                    DisplayName = "Halloween",
                    MinimumTimeBetween = 30,
                    MaximumTimeBetween = 60
                };
            }
        }

        private class BaseEvent
        {
            [JsonProperty(PropertyName = "Enabled", Order = 1)]
            public bool Enabled { get; set; }

            [JsonProperty(PropertyName = "Display Name", Order = 2)]
            public string DisplayName { get; set; }

            [JsonProperty(PropertyName = "Disable Vanilla Event", Order = 3)]
            public bool DisableVanillaEvent { get; set; }

            [JsonProperty(PropertyName = "Event Start Offset (Minutes)", Order = 4)]
            public float StartOffset { get; set; }

            [JsonProperty(PropertyName = "Minimum Time Between (Minutes)", Order = 5)]
            public float MinimumTimeBetween { get; set; }

            [JsonProperty(PropertyName = "Maximum Time Between (Minutes)", Order = 6)]
            public float MaximumTimeBetween { get; set; }

            [JsonProperty(PropertyName = "Minimum Online Players Required (0 = Disabled)", Order = 7)]
            public int MinimumOnlinePlayers { get; set; } = 0;

            [JsonProperty(PropertyName = "Maximum Online Players Required (0 = Disabled)", Order = 8)]
            public int MaximumOnlinePlayers { get; set; } = 0;

            [JsonProperty(PropertyName = "Announce Next Run Time", Order = 9)]
            public bool AnnounceNext { get; set; }

            [JsonProperty(PropertyName = "Restart Timer On Entity Kill", Order = 10)]
            public bool RestartTimerOnKill { get; set; } = true;

            [JsonProperty(PropertyName = "Kill Existing Event On Plugin Loaded", Order = 11)]
            public bool KillEventOnLoaded { get; set; }

            [JsonIgnore]
            public double NextRunTime { get; set; }

            public virtual EventWeight GetRandomEventWeight()
            {
                return null;
            }
        }

        private class CoexistEvent : BaseEvent
        {
            [JsonProperty(PropertyName = "Maximum Number On Server", Order = 19)]
            public int ServerMaximumNumber { get; set; } = 1;

            [JsonProperty(PropertyName = "Exclude Player's Entity", Order = 20)]
            public bool ExcludePlayerEntity { get; set; } = true;

            [JsonProperty(PropertyName = "Event Weights", Order = 21, ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<EventWeight> EventWeights { get; set; } = new List<EventWeight>();

            [JsonIgnore]
            private readonly List<EventWeight> _validEventWeights = new List<EventWeight>();

            public override EventWeight GetRandomEventWeight()
            {
                if (EventWeights.Count <= 0)
                {
                    return null;
                }
                _validEventWeights.Clear();
                _validEventWeights.AddRange(EventWeights.Where(x => x.IsValid()));
                if (_validEventWeights.Count <= 0)
                {
                    return null;
                }

                int totalWeight = 0;
                foreach (var eventWeight in _validEventWeights)
                {
                    totalWeight += eventWeight.Weight;
                }

                int random = UnityEngine.Random.Range(0, totalWeight);
                foreach (var eventWeight in _validEventWeights)
                {
                    if ((random -= eventWeight.Weight) < 0)
                    {
                        return eventWeight;
                    }
                }

                return null;
            }
        }

        private enum ArgumentType
        {
            Command,
            CallHook,
        }

        private class EventWeight
        {
            [JsonProperty(PropertyName = "Weight")]
            public int Weight { get; set; }

            [JsonProperty(PropertyName = "Name")]
            public string Name { get; set; }

            [JsonConverter(typeof(StringEnumConverter))]
            [JsonProperty(PropertyName = "Argument Type (Command or CallHook)")]
            public ArgumentType? ArgType { get; set; }

            [JsonProperty(PropertyName = "Arguments")]
            public List<string> Args { get; set; }

            [JsonIgnore] public bool IsNormalEvent => string.IsNullOrEmpty(Name);

            public bool IsValid()
            {
                if (Weight <= 0)
                {
                    return false;
                }
                if (IsNormalEvent)
                {
                    return true;
                }
                if (!ArgType.HasValue)
                {
                    return false;
                }
                if (ArgType.Value == ArgumentType.CallHook)
                {
                    if (Args.Count >= 2)
                    {
                        var plugin = instance.Manager.GetPlugin(Args[0]);
                        if (plugin != null)
                        {
                            return true;
                        }
                    }
                }
                else if (ArgType.Value == ArgumentType.Command)
                {
                    if (Args.Count >= 1)
                    {
                        return true;
                    }
                }

                return false;
            }

            public void RunCustomEvent()
            {
                if (Weight <= 0 || IsNormalEvent || !ArgType.HasValue)
                {
                    return;
                }
                if (ArgType.Value == ArgumentType.CallHook)
                {
                    var pluginName = Args[0];
                    var hookName = Args[1];
                    var plugin = instance.Manager.GetPlugin(pluginName);
                    if (plugin != null)
                    {
                        var args = Args.Skip(2);
                        plugin.Call(hookName, args.Select(x => (object)x).ToArray());
                    }
                }
                else if (ArgType.Value == ArgumentType.Command)
                {
                    var command = Args[0];
                    var args = Args.Skip(1) ;
                    ConsoleSystem.Run(ConsoleSystem.Option.Server, command, args.Select(x => (object)x).ToArray());
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
                    LoadDefaultConfig();
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

        #region LanguageFile

        private void Print(BasePlayer player, string message)
        {
            Player.Message(player, message,
                string.IsNullOrEmpty(configData.Chat.Prefix)
                    ? string.Empty
                    : $"<color={configData.Chat.PrefixColor}>{configData.Chat.Prefix}</color>",
                configData.Chat.SteamIdIcon);
        }

        private void Print(IPlayer iPlayer, string message)
        {
            iPlayer.Reply(message,
                iPlayer.Id == "server_console"
                    ? $"{configData.Chat.Prefix}"
                    : $"<color={configData.Chat.PrefixColor}>{configData.Chat.Prefix}</color>");
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
                ["NotAllowed"] = "You are not allowed to use the '{0}' command",
                ["BlankEvent"] = "You need to specify an event",
                ["UnknownEvent"] = "'{0}' is an unknown event type",
                ["NotSet"] = "'{0}' is not set to run via Automated Events",
                ["NextRunTime"] = "Next '{0}' event will be ran after {1}",
                ["Running"] = "'{0}' attempting to run automated event: {1}",
                ["Removing"] = "'{0}' attempting to remove any current running event: {1}",

                ["Bradley"] = "Bradley event has been triggered",
                ["CargoPlane"] = "CargoPlane event has been triggered",
                ["FancyDrop"] = "FancyDrop event has been triggered",
                ["PlaneCrash"] = "PlaneCrash event has been triggered",
                ["CargoShip"] = "CargoShip event has been triggered",
                ["RustTanic"] = "RustTanic event has been triggered",
                ["Chinook"] = "Chinook event has been triggered",
                ["Helicopter"] = "Helicopter event has been triggered",
                ["PilotEject"] = "PilotEject event has been triggered",
                ["HeliRefuel"] = "HeliRefuel event has been triggered",
                ["SantaSleigh"] = "SantaSleigh event has been triggered",
                ["Christmas"] = "Christmas event has been triggered",
                ["AlphaChristmas"] = "AlphaChristmas event has been triggered",
                ["Easter"] = "Easter event has been triggered",
                ["Halloween"] = "Halloween event has been triggered",
            }, this);
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NotAllowed"] = "您没有使用 '{0}' 命令的权限",
                ["BlankEvent"] = "您需要指定一个事件的类型",
                ["UnknownEvent"] = "'{0}' 是一个未知的事件类型",
                ["NotSet"] = "'{0}' 事件没有启用",
                ["NextRunTime"] = "下次 '{0}' 事件将在 {1} 后运行",
                ["Running"] = "'{0}' 通过命令运行了 {1} 事件",
                ["Removing"] = "'{0}' 通过命令删除了 {1} 事件",

                ["Bradley"] = "坦克出来装逼了",
                ["CargoPlane"] = "货机事件触发了",
                ["FancyDrop"] = "货机事件触发了",
                ["PlaneCrash"] = "货机坠毁事件触发了",
                ["CargoShip"] = "货船事件触发了",
                ["RustTanic"] = "冰山货船事件触发了",
                ["Chinook"] = "双螺旋飞机来咯",
                ["Helicopter"] = "武直出来装逼了",
                ["PilotEject"] = "武直驾驶员弹出事件触发了",
                ["HeliRefuel"] = "武直加油事件触发了",
                ["SantaSleigh"] = "圣诞老人骑着它的雪橇来送礼物咯",
                ["Christmas"] = "圣诞节事件触发了，快点去看看您那臭袜子",
                ["AlphaChristmas"] = "圣诞节事件触发了，快点去看看您那臭袜子",
                ["Easter"] = "复活节事件触发了，快点去捡彩蛋啊",
                ["Halloween"] = "万圣节事件触发了，快点去捡糖果吧",
            }, this, "zh-CN");
        }

        #endregion LanguageFile
    }
}