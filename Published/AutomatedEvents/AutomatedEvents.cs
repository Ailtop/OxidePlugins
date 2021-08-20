﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Automated Events", "k1lly0u/mspeedie/Arainrr", "1.0.10")]
    internal class AutomatedEvents : RustPlugin
    {
        #region Fields

        [PluginReference] private Plugin GUIAnnouncements, AlphaChristmas, FancyDrop, PlaneCrash, RustTanic, HeliRefuel, PilotEject;

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
        private Dictionary<BaseEntity, EventType> eventEntities;
        private readonly Dictionary<EventType, Timer> eventTimers = new Dictionary<EventType, Timer>();
        private readonly Dictionary<EventSchedule, EventType> disabledVanillaEvents = new Dictionary<EventSchedule, EventType>();

        private readonly Dictionary<string, EventType> eventSchedulePrefabShortNames = new Dictionary<string, EventType>
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
            LoadDefaultMessages();
            permission.RegisterPermission(PERMISSION_USE, this);
            permission.RegisterPermission(PERMISSION_NEXT, this);
            AddCovalenceCommand(configData.chatS.nextEventCommand, nameof(CmdNextEvent));
            AddCovalenceCommand(configData.chatS.runEventCommand, nameof(CmdRunEvent));
            AddCovalenceCommand(configData.chatS.killEventCommand, nameof(CmdKillEvent));

            var eventTypes = new List<EventType>(Enum.GetValues(typeof(EventType)).Cast<EventType>());
            if (!eventTypes.Any(x =>
            {
                if (x == EventType.None) return false;
                var baseEventS = GetBaseEventS(x);
                return baseEventS.enabled && baseEventS.restartTimerOnKill;
            }))
            {
                Unsubscribe(nameof(OnEntityKill));
            }
            else
            {
                eventEntities = new Dictionary<BaseEntity, EventType>();
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
                var baseEventS = GetBaseEventS(eventType);
                switch (eventType)
                {
                    case EventType.Bradley:
                        {
                            var bradleySpawner = BradleySpawner.singleton;
                            if (bradleySpawner != null)
                            {
                                if (baseEventS.disableVanillaEvent)
                                {
                                    ConVar.Bradley.enabled = false;
                                    bradleySpawner.enabled = false;
                                    bradleySpawner.CancelInvoke(nameof(bradleySpawner.DelayedStart));
                                    bradleySpawner.CancelInvoke(nameof(bradleySpawner.CheckIfRespawnNeeded));
                                    PrintDebug($"The vanilla {eventType} event is disabled");
                                }
                            }
                            else if (baseEventS.enabled)
                            {
                                PrintError("There is no Bradley Spawner on your server, so the Bradley event is disabled");
                                continue;
                            }
                        }
                        break;
                }
                if (baseEventS.enabled)
                {
                    eventTimers[eventType] = timer.Once(5f, () => StartEventTimer(eventType, configData.globalS.announceOnLoaded));
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
                            var baseEventS = GetBaseEventS(eventType);
                            var bradleySpawner = BradleySpawner.singleton;
                            if (bradleySpawner != null && baseEventS.disableVanillaEvent)
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

            foreach (var entry in disabledVanillaEvents)
            {
                entry.Key.enabled = true;
                PrintDebug($"The vanilla {entry.Value} event is enabled");
            }
            foreach (var value in eventTimers.Values)
            {
                value?.Destroy();
            }
            instance = null;
        }

        private void OnEntityKill(BaseEntity entity)
        {
            if (entity == null) return;
            EventType eventType;
            if (!eventEntities.TryGetValue(entity, out eventType)) return;
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
            if (eventSchedulePrefabShortNames.TryGetValue(prefabShortName, out eventType))
            {
                var baseEventS = GetBaseEventS(eventType);
                if (baseEventS.disableVanillaEvent)
                {
                    var eventSchedule = eventPrefab.GetComponent<EventSchedule>();
                    if (eventSchedule == null)
                    {
                        PrintError($"{eventPrefab.name} has no EventSchedule component. Please notify the plugin developer");
                        return null;
                    }
                    eventSchedule.enabled = false;
                    disabledVanillaEvents.Add(eventSchedule, eventType);
                    PrintDebug($"The vanilla {eventType} event is disabled", true);
                    return false;
                }
                if (!baseEventS.enabled) return null;
                switch (eventType)
                {
                    case EventType.CargoPlane:
                        if (!CanRunEvent<CargoPlane>(eventType, baseEventS))
                        {
                            return false;
                        }
                        break;

                    case EventType.CargoShip:
                        if (!CanRunEvent<CargoShip>(eventType, baseEventS))
                        {
                            return false;
                        }
                        break;

                    case EventType.Chinook:
                        if (!CanRunEvent<CH47HelicopterAIController>(eventType, baseEventS))
                        {
                            return false;
                        }
                        break;

                    case EventType.Helicopter:
                        if (!CanRunEvent<BaseHelicopter>(eventType, baseEventS))
                        {
                            return false;
                        }
                        break;

                    case EventType.Christmas:
                        if (!CanRunEvent<XMasRefill>(eventType, baseEventS))
                        {
                            return false;
                        }
                        break;

                    case EventType.Easter:
                        if (!CanRunHuntEvent<EggHuntEvent>(eventType, baseEventS))
                        {
                            return false;
                        }
                        break;

                    case EventType.Halloween:
                        if (!CanRunHuntEvent<HalloweenHunt>(eventType, baseEventS))
                        {
                            return false;
                        }
                        break;

                    default:
                        PrintError($"The vanilla {eventType} event was triggered, but not handled. Please notify the plugin developer");
                        return null;
                }
                if (configData.globalS.announceEventTriggered)
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
                var baseEventS = GetBaseEventS(eventType);
                if (baseEventS.enabled && baseEventS.killEventOnLoaded)
                {
                    var excludePlayerEntity = (baseEventS as CoexistEventS)?.excludePlayerEntity ?? false;
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
            var baseEventS = GetBaseEventS(eventType);
            if (!baseEventS.enabled)
            {
                PrintDebug($"Unable to running {eventType} event, because the event is disabled");
                return;
            }
            var randomTime = timeOverride <= 0f
                ? baseEventS.minimumTimeBetween <= baseEventS.maximumTimeBetween
                    ? UnityEngine.Random.Range(baseEventS.minimumTimeBetween, baseEventS.maximumTimeBetween)
                    : UnityEngine.Random.Range(baseEventS.maximumTimeBetween, baseEventS.minimumTimeBetween)
                : timeOverride;
            randomTime += baseEventS.startOffset;
            var nextDateTime = DateTime.UtcNow.AddMinutes(randomTime);
            baseEventS.nextRunTime = Facepunch.Math.Epoch.FromDateTime(nextDateTime);

            Timer value;
            if (eventTimers.TryGetValue(eventType, out value))
            {
                value?.Destroy();
            }
            eventTimers[eventType] = timer.Once(randomTime * 60f, () => RunEvent(eventType));

            if (onKill || !baseEventS.restartTimerOnKill)
            {
                var timeLeft = TimeSpan.FromSeconds(baseEventS.nextRunTime - Facepunch.Math.Epoch.Current).ToShortString();
                PrintDebug($"Next {eventType} event will be ran after {timeLeft}");
                if (announce && baseEventS.announceNext)
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
            var baseEventS = GetBaseEventS(eventType);
            switch (eventType)
            {
                case EventType.Bradley:
                    {
                        if (bypass || CanRunEvent<BradleyAPC>(eventType, baseEventS, false))
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
                    }
                    break;

                case EventType.CargoPlane:
                    {
                        if (bypass || CanRunEvent<CargoPlane>(eventType, baseEventS, false))
                        {
                            var planeEventS = baseEventS as PlaneEventS;
                            var weightDict = new Dictionary<int, float>();
                            if (planeEventS.normalWeight > 0)
                            {
                                weightDict.Add(0, planeEventS.normalWeight);
                            }
                            if (planeEventS.fancyDropWeight > 0 && FancyDrop != null)
                            {
                                weightDict.Add(1, planeEventS.fancyDropWeight);
                            }
                            if (planeEventS.planeCrashWeight > 0 && PlaneCrash != null)
                            {
                                weightDict.Add(2, planeEventS.planeCrashWeight);
                            }

                            var index = GetEventIndexFromWeight(weightDict);
                            switch (index)
                            {
                                case 0:
                                    PrintDebug("Spawning Cargo Plane");
                                    var plane = GameManager.server.CreateEntity(PREFAB_PLANE) as CargoPlane;
                                    if (plane == null)
                                    {
                                        goto NotifyDeveloper;
                                    }
                                    plane.Spawn();
                                    eventEntity = plane;
                                    eventTypeStr = eventType.ToString();
                                    break;

                                case 1:
                                    PrintDebug("Spawning FancyDrop Cargo Plane");
                                    rust.RunServerCommand("ad.random");
                                    eventTypeStr = "FancyDrop";
                                    break;

                                case 2:
                                    PrintDebug("Spawning PlaneCrash Cargo Plane");
                                    rust.RunServerCommand("callcrash");
                                    eventTypeStr = "PlaneCrash";
                                    break;
                            }
                        }
                    }
                    break;

                case EventType.CargoShip:
                    {
                        if (bypass || CanRunEvent<CargoShip>(eventType, baseEventS, false))
                        {
                            var cargoShipEventS = baseEventS as ShipEventS;
                            var weightDict = new Dictionary<int, float>();
                            if (cargoShipEventS.normalWeight > 0)
                            {
                                weightDict.Add(0, cargoShipEventS.normalWeight);
                            }
                            if (cargoShipEventS.rustTanicWeight > 0 && RustTanic != null)
                            {
                                weightDict.Add(1, cargoShipEventS.rustTanicWeight);
                            }
                            var index = GetEventIndexFromWeight(weightDict);
                            switch (index)
                            {
                                case 0:
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
                                    break;

                                case 1:
                                    PrintDebug("Spawning RustTanic Cargo Ship");
                                    rust.RunServerCommand("calltitanic");
                                    eventTypeStr = "RustTanic";
                                    break;
                            }
                        }
                    }
                    break;

                case EventType.Chinook:
                    {
                        if (bypass || CanRunEvent<CH47HelicopterAIController>(eventType, baseEventS, false, entity => entity.landingTarget == Vector3.zero))
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
                    }
                    break;

                case EventType.Helicopter:
                    {
                        if (bypass || CanRunEvent<BaseHelicopter>(eventType, baseEventS, false))
                        {
                            var heliEventS = baseEventS as HeliEventS;
                            var weightDict = new Dictionary<int, float>();
                            if (heliEventS.normalWeight > 0)
                            {
                                weightDict.Add(0, heliEventS.normalWeight);
                            }
                            if (heliEventS.pilotEjectWeight > 0 && PilotEject != null)
                            {
                                weightDict.Add(1, heliEventS.pilotEjectWeight);
                            }
                            if (heliEventS.heliRefuelWeight > 0 && HeliRefuel != null)
                            {
                                weightDict.Add(2, heliEventS.heliRefuelWeight);
                            }

                            var index = GetEventIndexFromWeight(weightDict);
                            switch (index)
                            {
                                case 0:
                                    PrintDebug("Spawning Helicopter");
                                    var helicopter = GameManager.server.CreateEntity(PREFAB_HELI) as BaseHelicopter;
                                    if (helicopter == null)
                                    {
                                        goto NotifyDeveloper;
                                    }
                                    helicopter.Spawn();
                                    eventEntity = helicopter;
                                    eventTypeStr = eventType.ToString();
                                    break;

                                case 1:
                                    PrintDebug("Spawning PilotEject Helicopter");
                                    rust.RunServerCommand("pe call");
                                    eventTypeStr = "PilotEject";
                                    break;

                                case 2:
                                    PrintDebug("Spawning HeliRefuel Helicopter");
                                    rust.RunServerCommand("hr call");
                                    eventTypeStr = "HeliRefuel";
                                    break;
                            }
                        }
                    }
                    break;

                case EventType.SantaSleigh:
                    {
                        if (bypass || CanRunEvent<SantaSleigh>(eventType, baseEventS, false))
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
                    }
                    break;

                case EventType.Christmas:
                    {
                        if (bypass || CanRunEvent<XMasRefill>(eventType, baseEventS, false))
                        {
                            var christmasEventS = baseEventS as ChristmasEventS;
                            var weightDict = new Dictionary<int, float>();
                            if (christmasEventS.normalWeight > 0)
                            {
                                weightDict.Add(0, christmasEventS.normalWeight);
                            }
                            if (christmasEventS.alphaChristmasWeight > 0 && AlphaChristmas != null)
                            {
                                weightDict.Add(1, christmasEventS.alphaChristmasWeight);
                            }
                            var index = GetEventIndexFromWeight(weightDict);
                            switch (index)
                            {
                                case 0:
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
                                    break;

                                case 1:
                                    PrintDebug("Running AlphaChristmas Refill");
                                    rust.RunServerCommand("alphachristmas.refill");
                                    eventTypeStr = "AlphaChristmas";
                                    break;
                            }
                        }
                    }
                    break;

                case EventType.Easter:
                    {
                        if (bypass || CanRunHuntEvent<EggHuntEvent>(eventType, baseEventS, false))
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
                        if (bypass || CanRunHuntEvent<HalloweenHunt>(eventType, baseEventS, false))
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

            if (eventEntity != null && baseEventS.enabled && baseEventS.restartTimerOnKill)
            {
                foreach (var entry in eventEntities.ToArray())
                {
                    if (entry.Value == eventType)
                    {
                        eventEntities.Remove(entry.Key);
                    }
                }
                eventEntities.Add(eventEntity, eventType);
            }
            if (!string.IsNullOrEmpty(eventTypeStr))
            {
                if (configData.globalS.announceEventTriggered)
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
            var baseEventS = GetBaseEventS(eventType);
            switch (eventType)
            {
                case EventType.Bradley:
                    foreach (var bradley in GetEventEntities<BradleyAPC>(baseEventS).ToArray())
                    {
                        PrintDebug("Killing a Bradley");
                        bradley.Kill();
                    }
                    return;

                case EventType.CargoPlane:
                    foreach (var cargoPlane in GetEventEntities<CargoPlane>(baseEventS).ToArray())
                    {
                        PrintDebug("Killing a Cargo Plane");
                        cargoPlane.Kill();
                    }
                    return;

                case EventType.CargoShip:
                    foreach (var cargoShip in GetEventEntities<CargoShip>(baseEventS).ToArray())
                    {
                        PrintDebug("Killing a Cargo Ship");
                        cargoShip.Kill();
                    }
                    return;

                case EventType.Chinook:
                    foreach (var ch47Helicopter in GetEventEntities<CH47HelicopterAIController>(baseEventS, entity => entity.landingTarget == Vector3.zero).ToArray())
                    {
                        PrintDebug("Killing a Chinook (CH47)");
                        ch47Helicopter.Kill();
                    }
                    return;

                case EventType.Helicopter:
                    foreach (var helicopter in GetEventEntities<BaseHelicopter>(baseEventS).ToArray())
                    {
                        PrintDebug("Killing a Helicopter");
                        helicopter.Kill();
                    }
                    return;

                case EventType.SantaSleigh:
                    foreach (var santaSleigh in GetEventEntities<SantaSleigh>(baseEventS).ToArray())
                    {
                        PrintDebug("Killing a Santa Sleigh");
                        santaSleigh.Kill();
                    }
                    return;

                case EventType.Christmas:
                    foreach (var christmas in GetEventEntities<XMasRefill>(baseEventS).ToArray())
                    {
                        PrintDebug("Killing a Christmas");
                        christmas.Kill();
                    }
                    return;

                case EventType.Easter:
                    foreach (var easter in GetEventEntities<EggHuntEvent>(baseEventS, entity => entity.ShortPrefabName == "egghunt").ToArray())
                    {
                        PrintDebug("Killing a Easter");
                        easter.Kill();
                    }
                    return;

                case EventType.Halloween:
                    foreach (var halloween in GetEventEntities<HalloweenHunt>(baseEventS).ToArray())
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
            var baseEventS = GetBaseEventS(eventType);
            if (!baseEventS.enabled || baseEventS.nextRunTime <= 0)
            {
                nextTime = Lang("NotSet", iPlayer.Id, baseEventS.displayName);
                return false;
            }
            var timeLeft = TimeSpan.FromSeconds(baseEventS.nextRunTime - Facepunch.Math.Epoch.Current).ToShortString();
            nextTime = Lang("NextRunTime", iPlayer.Id, baseEventS.displayName, timeLeft);
            return true;
        }

        private BaseEventS GetBaseEventS(EventType eventType)
        {
            switch (eventType)
            {
                case EventType.Bradley: return configData.events.bradleyEventS;
                case EventType.CargoPlane: return configData.events.planeEventS;
                case EventType.CargoShip: return configData.events.shipEventS;
                case EventType.Chinook: return configData.events.chinookEventS;
                case EventType.Helicopter: return configData.events.helicopterEventS;
                case EventType.SantaSleigh: return configData.events.santaSleighEventS;
                case EventType.Christmas: return configData.events.christmasEventS;
                case EventType.Easter: return configData.events.easterEventS;
                case EventType.Halloween: return configData.events.halloweenEventS;
                default: PrintError($"GetBaseEventS: Unknown EventType: {eventType}"); return null;
            }
        }

        private string GetEventTypeDisplayName(EventType eventType)
        {
            if (eventType == EventType.None) return "None";
            var baseEventS = GetBaseEventS(eventType);
            return baseEventS.displayName;
        }

        private void SendEventNextRunMessage(EventType eventType, string timeLeft)
        {
            if (configData.globalS.useGUIAnnouncements && GUIAnnouncements != null)
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
            if (configData.globalS.useGUIAnnouncements && GUIAnnouncements != null)
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

        private static IEnumerable<T> GetEventEntities<T>(BaseEventS baseEventS, Func<T, bool> filter = null) where T : BaseEntity
        {
            var excludePlayerEntity = (baseEventS as CoexistEventS)?.excludePlayerEntity ?? false;
            foreach (var serverEntity in BaseNetworkable.serverEntities)
            {
                var entity = serverEntity as T;
                if (entity == null) continue;
                if (excludePlayerEntity && entity.OwnerID.IsSteamId()) continue;
                if (filter != null && !filter(entity)) continue;
                yield return entity;
            }
        }

        private static bool CanRunEvent<T>(EventType eventType, BaseEventS baseEventS, bool vanilla = true, Func<T, bool> filter = null) where T : BaseEntity
        {
            return CheckOnlinePlayers(eventType, baseEventS, vanilla) && CanRunCoexistEvent(eventType, baseEventS, vanilla, filter);
        }

        private static bool CheckOnlinePlayers(EventType eventType, BaseEventS baseEventS, bool vanilla = true)
        {
            var onlinePlayers = BasePlayer.activePlayerList.Count;
            if (baseEventS.minimumOnlinePlayers > 0 && onlinePlayers < baseEventS.minimumOnlinePlayers)
            {
                instance?.PrintDebug($"The online players is less than {baseEventS.minimumOnlinePlayers}, so the {eventType} {(vanilla ? "vanilla" : "auto")} event cannot run", true);
                return false;
            }
            if (baseEventS.maximumOnlinePlayers > 0 && onlinePlayers > baseEventS.maximumOnlinePlayers)
            {
                instance?.PrintDebug($"The online players is greater than {baseEventS.maximumOnlinePlayers}, so the {eventType} {(vanilla ? "vanilla" : "auto")} event cannot run", true);
                return false;
            }
            return true;
        }

        private static bool CanRunCoexistEvent<T>(EventType eventType, BaseEventS baseEventS, bool vanilla = true, Func<T, bool> filter = null) where T : BaseEntity
        {
            var coexistEventS = baseEventS as CoexistEventS;
            if (coexistEventS != null && coexistEventS.serverMaximumNumber > 0)
            {
                if (BaseNetworkable.serverEntities.Count(x =>
                {
                    var entity = x as T;
                    if (entity == null) return false;
                    if (filter != null && !filter(entity)) return false;
                    return !coexistEventS.excludePlayerEntity || !entity.OwnerID.IsSteamId();
                }) >= coexistEventS.serverMaximumNumber)
                {
                    instance?.PrintDebug($"The number of {eventType} {(vanilla ? "vanilla" : "auto")} events has reached the limit of {coexistEventS.serverMaximumNumber}", true);
                    return false;
                }
            }
            return true;
        }

        private static bool CanRunHuntEvent<T>(EventType eventType, BaseEventS baseEventS, bool vanilla = true) where T : EggHuntEvent
        {
            if (!CheckOnlinePlayers(eventType, baseEventS, vanilla)) return false;
            return true;
        }

        #endregion Helpers

        #region Debug

        private void PrintDebug(string message, bool warning = false)
        {
            if (configData.globalS.debugEnabled)
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
            public Settings globalS = new Settings();

            public class Settings
            {
                [JsonProperty(PropertyName = "Enable Debug Mode")]
                public bool debugEnabled = true;

                [JsonProperty(PropertyName = "Announce On Plugin Loaded")]
                public bool announceOnLoaded;

                [JsonProperty(PropertyName = "Announce On Event Triggered")]
                public bool announceEventTriggered;

                [JsonProperty(PropertyName = "Use GUIAnnouncements Plugin")]
                public bool useGUIAnnouncements;
            }

            [JsonProperty(PropertyName = "Chat Settings")]
            public ChatSettings chatS = new ChatSettings();

            public class ChatSettings
            {
                [JsonProperty(PropertyName = "Next Event Command")]
                public string nextEventCommand = "nextevent";

                [JsonProperty(PropertyName = "Run Event Command")]
                public string runEventCommand = "runevent";

                [JsonProperty(PropertyName = "Kill Event Command")]
                public string killEventCommand = "killevent";

                [JsonProperty(PropertyName = "Chat Prefix")]
                public string prefix = "[AutomatedEvents]: ";

                [JsonProperty(PropertyName = "Chat Prefix Color")]
                public string prefixColor = "#00FFFF";

                [JsonProperty(PropertyName = "Chat SteamID Icon")]
                public ulong steamIDIcon = 0;
            }

            [JsonProperty(PropertyName = "Event Settings")]
            public EventSettings events = new EventSettings();

            public class EventSettings
            {
                [JsonProperty(PropertyName = "Bradley Event")]
                public CoexistEventS bradleyEventS = new CoexistEventS
                {
                    displayName = "Bradley",
                    minimumTimeBetween = 30,
                    maximumTimeBetween = 45
                };

                [JsonProperty(PropertyName = "Cargo Plane Event")]
                public PlaneEventS planeEventS = new PlaneEventS
                {
                    displayName = "Cargo Plane",
                    minimumTimeBetween = 30,
                    maximumTimeBetween = 45
                };

                [JsonProperty(PropertyName = "Cargo Ship Event")]
                public ShipEventS shipEventS = new ShipEventS
                {
                    displayName = "Cargo Ship",
                    minimumTimeBetween = 30,
                    maximumTimeBetween = 45
                };

                [JsonProperty(PropertyName = "Chinook (CH47) Event")]
                public CoexistEventS chinookEventS = new CoexistEventS
                {
                    displayName = "Chinook",
                    minimumTimeBetween = 30,
                    maximumTimeBetween = 45
                };

                [JsonProperty(PropertyName = "Helicopter Event")]
                public HeliEventS helicopterEventS = new HeliEventS
                {
                    displayName = "Helicopter",
                    minimumTimeBetween = 45,
                    maximumTimeBetween = 60
                };

                [JsonProperty(PropertyName = "Santa Sleigh Event")]
                public CoexistEventS santaSleighEventS = new CoexistEventS
                {
                    displayName = "Santa Sleigh",
                    minimumTimeBetween = 30,
                    maximumTimeBetween = 60
                };

                [JsonProperty(PropertyName = "Christmas Event")]
                public ChristmasEventS christmasEventS = new ChristmasEventS
                {
                    displayName = "Christmas",
                    minimumTimeBetween = 60,
                    maximumTimeBetween = 120
                };

                [JsonProperty(PropertyName = "Easter Event")]
                public BaseEventS easterEventS = new BaseEventS
                {
                    displayName = "Easter",
                    minimumTimeBetween = 30,
                    maximumTimeBetween = 60
                };

                [JsonProperty(PropertyName = "Halloween Event")]
                public BaseEventS halloweenEventS = new BaseEventS
                {
                    displayName = "Halloween",
                    minimumTimeBetween = 30,
                    maximumTimeBetween = 60
                };
            }
        }

        private class BaseEventS
        {
            [JsonProperty(PropertyName = "Enabled", Order = 1)]
            public bool enabled;

            [JsonProperty(PropertyName = "Display Name", Order = 2)]
            public string displayName;

            [JsonProperty(PropertyName = "Disable Vanilla Event", Order = 3)]
            public bool disableVanillaEvent;

            [JsonProperty(PropertyName = "Event Start Offset (Minutes)", Order = 4)]
            public float startOffset;

            [JsonProperty(PropertyName = "Minimum Time Between (Minutes)", Order = 5)]
            public float minimumTimeBetween;

            [JsonProperty(PropertyName = "Maximum Time Between (Minutes)", Order = 6)]
            public float maximumTimeBetween;

            [JsonProperty(PropertyName = "Minimum Online Players Required (0 = Disabled)", Order = 7)]
            public int minimumOnlinePlayers = 0;

            [JsonProperty(PropertyName = "Maximum Online Players Required (0 = Disabled)", Order = 8)]
            public int maximumOnlinePlayers = 0;

            [JsonProperty(PropertyName = "Announce Next Run Time", Order = 9)]
            public bool announceNext;

            [JsonProperty(PropertyName = "Restart Timer On Entity Kill", Order = 10)]
            public bool restartTimerOnKill = true;

            [JsonProperty(PropertyName = "Kill Existing Event On Plugin Loaded", Order = 11)]
            public bool killEventOnLoaded;

            [JsonIgnore] public double nextRunTime;
        }

        private class CoexistEventS : BaseEventS
        {
            [JsonProperty(PropertyName = "Maximum Number On Server", Order = 19)]
            public int serverMaximumNumber = 1;

            [JsonProperty(PropertyName = "Exclude Player's Entity", Order = 20)]
            public bool excludePlayerEntity = true;
        }

        private class PlaneEventS : CoexistEventS
        {
            [JsonProperty(PropertyName = "Normal Event Weight (0 = Disable)", Order = 21)]
            public float normalWeight = 60;

            [JsonProperty(PropertyName = "FancyDrop Plugin Event Weight (0 = Disable)", Order = 22)]
            public float fancyDropWeight = 20;

            [JsonProperty(PropertyName = "PlaneCrash Plugin Event Weight (0 = Disable)", Order = 23)]
            public float planeCrashWeight = 20;
        }

        private class ShipEventS : CoexistEventS
        {
            [JsonProperty(PropertyName = "Normal Event Weight (0 = Disable)", Order = 21)]
            public float normalWeight = 80;

            [JsonProperty(PropertyName = "RustTanic Plugin Event Weight (0 = Disable)", Order = 22)]
            public float rustTanicWeight = 20;
        }

        private class HeliEventS : CoexistEventS
        {
            [JsonProperty(PropertyName = "Normal Event Weight (0 = Disable)", Order = 21)]
            public float normalWeight = 60;

            [JsonProperty(PropertyName = "HeliRefuel Plugin Event Weight (0 = Disable)", Order = 22)]
            public float heliRefuelWeight = 20;

            [JsonProperty(PropertyName = "PilotEject Plugin Event Weight (0 = Disable)", Order = 23)]
            public float pilotEjectWeight = 20;
        }

        private class ChristmasEventS : CoexistEventS
        {
            [JsonProperty(PropertyName = "Normal Event Weight (0 = Disable)", Order = 21)]
            public float normalWeight = 20;

            [JsonProperty(PropertyName = "AlphaChristmas Plugin Event Weight (0 = Disable)", Order = 22)]
            public float alphaChristmasWeight = 80;
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
                string.IsNullOrEmpty(configData.chatS.prefix)
                    ? string.Empty
                    : $"<color={configData.chatS.prefixColor}>{configData.chatS.prefix}</color>",
                configData.chatS.steamIDIcon);
        }

        private void Print(IPlayer iPlayer, string message)
        {
            iPlayer.Reply(message,
                iPlayer.Id == "server_console"
                    ? $"{configData.chatS.prefix}"
                    : $"<color={configData.chatS.prefixColor}>{configData.chatS.prefix}</color>");
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