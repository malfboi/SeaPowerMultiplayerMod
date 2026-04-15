using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using SeaPower;
using SeaPower.Decals;
using SeapowerMultiplayer.Messages;
using SeapowerUI;
using UniRx;
using UnityEngine;
using VesselStates;

namespace SeapowerMultiplayer
{
    // ── UnitRegistry lifecycle hooks ────────────────────────────────────────
    // Harmony patches ObjectBase.Awake (non-virtual, public) and OnDestroy (private)
    // to maintain the UnitRegistry without per-frame FindObjectsByType calls.

    [HarmonyPatch(typeof(ObjectBase), "Awake")]
    public static class Patch_ObjectBase_Register
    {
        static void Postfix(ObjectBase __instance) => UnitRegistry.Register(__instance);
    }

    [HarmonyPatch(typeof(ObjectBase), "OnDestroy")]
    public static class Patch_ObjectBase_Unregister
    {
        static void Postfix(ObjectBase __instance) => UnitRegistry.Unregister(__instance);
    }

    // ── Client physics: targeted null-guard patches ────────────────────────
    //
    // After save-file load, SpeedCommand.Value is null (only set when
    // setTelegraph() is called) and Formation can be null. These targeted
    // guards let physics run normally once the values are initialised.
    // NO blanket host-only suppressions — the client runs full local physics.

    [HarmonyPatch(typeof(Compartments), "CalculateWantedVelocityInKnots")]
    public static class Patch_Compartments_CalculateWantedVelocityInKnots
    {
        private static readonly HashSet<int> _loggedIds = new();
        internal static void ClearLogCache() => _loggedIds.Clear();

        static bool Prefix(Compartments __instance, ref float __result)
        {
            if (__instance._baseObject?.SpeedCommand?.Value == null)
            {
                int id = __instance._baseObject?.UniqueID ?? -1;
                if (_loggedIds.Add(id))
                    Plugin.Log.LogWarning($"[Physics] SpeedCommand.Value is NULL for entity {id} — returning speed=0 (this blocks movement)");
                __result = 0f;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Vessel), "applyRudderThrust")]
    public static class Patch_Vessel_ApplyRudderThrust
    {
        private static readonly HashSet<int> _loggedIds = new();
        internal static void ClearLogCache() => _loggedIds.Clear();

        static bool Prefix(Vessel __instance)
        {
            if (__instance.SpeedCommand?.Value == null)
            {
                if (_loggedIds.Add(__instance.UniqueID))
                    Plugin.Log.LogWarning($"[Physics] applyRudderThrust blocked for entity {__instance.UniqueID} — SpeedCommand.Value is NULL");
                return false;
            }
            return true;
        }
    }

    // Guard MovingInFormation.setRudderBasedOnCourse — Formation is null after save load
    [HarmonyPatch(typeof(MovingInFormation), "setRudderBasedOnCourse")]
    public static class Patch_MovingInFormation_SetRudderBasedOnCourse
    {
        private static readonly FieldInfo _vesselField =
            AccessTools.Field(typeof(MovingInFormation), "_vessel");

        static bool Prefix(MovingInFormation __instance)
        {
            var vessel = _vesselField?.GetValue(__instance) as Vessel;
            return vessel?.Formation != null;
        }
    }

    // Guard VesselPropulsionSystem.OnUpdate — SpeedCommand null after save load
    [HarmonyPatch(typeof(VesselPropulsionSystem), "OnUpdate")]
    public static class Patch_VesselPropulsionSystem_OnUpdate
    {
        private static readonly HashSet<int> _loggedIds = new();
        internal static void ClearLogCache() => _loggedIds.Clear();
        private static readonly FieldInfo _vesselField =
            AccessTools.Field(typeof(VesselPropulsionSystem), "_vessel");

        static bool Prefix(VesselPropulsionSystem __instance)
        {
            var vessel = _vesselField?.GetValue(__instance) as Vessel;
            if (vessel?.SpeedCommand?.Value == null)
            {
                int id = vessel?.UniqueID ?? -1;
                if (_loggedIds.Add(id))
                    Plugin.Log.LogWarning($"[Physics] VesselPropulsionSystem.OnUpdate blocked for entity {id} — SpeedCommand.Value is NULL");
                return false;
            }
            return true;
        }
    }

    // ── Scene-loading guards ──────────────────────────────────────────────
    //
    // During client scene load, suppress systems that crash on partially
    // initialised state. Cleared once SceneLoading = false.

    [HarmonyPatch(typeof(TaskforceManager), nameof(TaskforceManager.OnUpdate))]
    public static class Patch_TaskforceManager_OnUpdate
    {
        static bool Prefix() => !SessionManager.SceneLoading;
    }

    [HarmonyPatch(typeof(SensorSystemsLink), nameof(SensorSystemsLink.OnUpdate))]
    public static class Patch_SensorSystemsLink_OnUpdate
    {
        static Exception? Finalizer(Exception __exception)
        {
            if (SessionManager.SceneLoading && __exception is NullReferenceException)
                return null;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(SensorSystemVisual), nameof(SensorSystemVisual.runVisualScan))]
    public static class Patch_SensorSystemVisual_RunVisualScan
    {
        static Exception? Finalizer(Exception __exception)
        {
            if (SessionManager.SceneLoading && __exception is NullReferenceException)
                return null;
            return __exception;
        }
    }

    // Guard EnvironmentAudioManager.OnStart — _mixer (AudioMixer) is null during save-file load.
    // This runs inside GameInitializer.init(), and if it throws, it kills the ENTIRE init chain
    // (TaskforceManager, MissionManager, AIController etc. never initialize).
    [HarmonyPatch(typeof(EnvironmentAudioManager), nameof(EnvironmentAudioManager.OnStart))]
    public static class Patch_EnvironmentAudioManager_OnStart
    {
        static Exception? Finalizer(Exception __exception)
        {
            if (__exception != null)
                Plugin.Log.LogWarning($"[Patch] EnvironmentAudioManager.OnStart failed: {__exception.GetType().Name} — suppressed to keep init chain alive");
            return null;
        }
    }

    // Guard CIWS weapon constructor — effect prefab can be null during save-file load
    [HarmonyPatch(typeof(WeaponSystemCIWS),
        MethodType.Constructor,
        new[] { typeof(ObjectBase), typeof(WeaponParameters), typeof(UnityEngine.GameObject), typeof(ObjectBaseParameters) })]
    public static class Patch_WeaponSystemCIWS_Ctor
    {
        static Exception? Finalizer(Exception __exception)
        {
            if (SessionManager.SceneLoading && __exception is NullReferenceException)
            {
                Plugin.Log.LogWarning("[Patch] WeaponSystemCIWS NRE suppressed during scene load");
                return null;
            }
            return __exception;
        }
    }


    // ── Bidirectional order sync ────────────────────────────────────────────
    //
    // All order patches follow the same pattern:
    //  - If applying from network (OrderHandler guard), just execute locally
    //  - Client: send to host + apply locally (UI updates immediately)
    //  - Host: apply locally + broadcast to clients via Postfix
    //
    // The OrderHandler.ApplyingFromNetwork flag prevents infinite loops.

    [HarmonyPatch(typeof(Vessel), nameof(Vessel.setTelegraph))]
    public static class Patch_Vessel_SetTelegraph
    {
        static PlayerOrderMessage Msg(Vessel v, int telegraph) => new PlayerOrderMessage
        {
            SourceEntityId = v.UniqueID,
            Order          = OrderType.SetSpeed,
            Speed          = telegraph,
        };

        static bool Prefix(Vessel __instance, int telegraph) =>
            OrderSyncHelper.Prefix(__instance, Msg(__instance, telegraph));

        static void Postfix(Vessel __instance, int telegraph) =>
            OrderSyncHelper.Postfix(__instance, Msg(__instance, telegraph));
    }

    // NOTE: Patch_Vessel_SetRudderAngle removed.
    // setRudderAngle() takes a PHYSICAL rudder angle (-25..+25), but the receiver
    // interpreted it as a target heading (0-360). This caused ships to turn North
    // after session sync (Drift state calls setRudderAngle with small values →
    // misinterpreted as heading near 0°). Heading is synced indirectly through
    // waypoints + StateApplier position/heading corrections.
    // Also: SetRudderToHeading() writes _setRudderAngle directly, bypassing
    // setRudderAngle(), so the patch never caught normal autopilot steering anyway.


    // ── Waypoint intercept (bidirectional) ──────────────────────────────────

    [HarmonyPatch(typeof(ObjectBase), "setWaypointTask",
        new[] { typeof(GeoPosition), typeof(string), typeof(WaypointData.WaypointHeightState) })]
    public static class Patch_ObjectBase_SetWaypointTask
    {
        static PlayerOrderMessage Msg(ObjectBase u, GeoPosition geoPos)
        {
            return new PlayerOrderMessage
            {
                SourceEntityId = u.UniqueID,
                Order          = OrderType.MoveTo,
                DestX          = (float)geoPos._longitude,
                DestY          = (float)geoPos._height,
                DestZ          = (float)geoPos._latitude,
            };
        }

        static bool Prefix(ObjectBase __instance, GeoPosition geoPos) =>
            OrderSyncHelper.Prefix(__instance, Msg(__instance, geoPos));

        static void Postfix(ObjectBase __instance, GeoPosition geoPos) =>
            OrderSyncHelper.Postfix(__instance, Msg(__instance, geoPos));
    }


    // ── Waypoint delete / clear sync (bidirectional) ──────────────────────

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.RemoveWaypoints))]
    public static class Patch_ObjectBase_RemoveWaypoints
    {
        static PlayerOrderMessage Msg(ObjectBase u) => new PlayerOrderMessage
        {
            SourceEntityId = u.UniqueID,
            Order = OrderType.RemoveWaypoints,
        };

        static bool Prefix(ObjectBase __instance) =>
            OrderSyncHelper.Prefix(__instance, Msg(__instance));

        static void Postfix(ObjectBase __instance) =>
            OrderSyncHelper.Postfix(__instance, Msg(__instance));
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.DeleteSelectedWaypoint))]
    public static class Patch_ObjectBase_DeleteSelectedWaypoint
    {
        [ThreadStatic] static int _pendingIndex;

        static PlayerOrderMessage Msg(ObjectBase u) => new PlayerOrderMessage
        {
            SourceEntityId = u.UniqueID,
            Order = OrderType.DeleteWaypoint,
            Speed = _pendingIndex,
        };

        static bool Prefix(ObjectBase __instance)
        {
            if (OrderHandler.ApplyingFromNetwork) return true;
            if (SessionManager.SceneLoading) return true;

            // Find index of selected waypoint before it's deleted
            _pendingIndex = -1;
            var root = __instance._userRoot;
            if (root != null)
            {
                for (int i = 0; i < root.TaskViewModels.Count; i++)
                {
                    if (root.TaskViewModels[i].Task == root.SelectedTask)
                    {
                        _pendingIndex = i;
                        break;
                    }
                }
            }

            if (_pendingIndex < 0) return true; // nothing to sync
            return OrderSyncHelper.Prefix(__instance, Msg(__instance));
        }

        static void Postfix(ObjectBase __instance)
        {
            if (_pendingIndex < 0) return;
            OrderSyncHelper.Postfix(__instance, Msg(__instance));
            _pendingIndex = -1;
        }
    }

    // ── Waypoint drag sync (instant via UpdateSimulation patch) ─────────

    [HarmonyPatch(typeof(UserRootNode), "UpdateSimulation", new[] { typeof(int) })]
    public static class Patch_UserRootNode_UpdateSimulation
    {
        private static readonly FieldInfo TargetField =
            AccessTools.Field(typeof(UserRootNode), "_target");
        private static readonly Dictionary<int, float> _lastSendTime = new();
        internal static readonly Dictionary<int, (ObjectBase unit, int index)> _pending = new();

        static void Postfix(UserRootNode __instance, int start)
        {
            if (OrderHandler.ApplyingFromNetwork) return;
            if (SessionManager.SceneLoading) return;
            if (!NetworkManager.Instance.IsConnected) return;

            var unit = TargetField.GetValue(__instance) as ObjectBase;
            if (unit == null || unit.UniqueID == 0) return;

            bool isHost = Plugin.Instance.CfgIsHost.Value;
            if (!isHost && !TaskforceAssignmentManager.ClientMayControl(unit)) return;
            if (!Plugin.Instance.CfgPvP.Value && UnitLockManager.IsLockedByRemote(unit.UniqueID)) return;

            var root = unit._userRoot;
            if (root == null || start < 0 || start >= root.TaskViewModels.Count) return;
            if (!(root.TaskViewModels[start].Task is GoToWaypointTask wp)) return;

            // 20Hz throttle per unit — mark pending if too soon
            int uid = unit.UniqueID;
            if (_lastSendTime.TryGetValue(uid, out float last) && Time.time - last < 0.05f)
            {
                _pending[uid] = (unit, start);
                return;
            }

            SendEditWaypoint(unit, start, wp);
            _lastSendTime[uid] = Time.time;
            _pending.Remove(uid);
        }

        internal static void SendEditWaypoint(ObjectBase unit, int index, GoToWaypointTask wp)
        {
            var geo = wp._waypointGeoPos.value;
            var msg = new PlayerOrderMessage
            {
                SourceEntityId = unit.UniqueID,
                Order = OrderType.EditWaypoint,
                Speed = index,
                DestX = (float)geo._longitude, DestY = (float)geo._height, DestZ = (float)geo._latitude,
            };

            if (!OrderDeduplicator.ShouldSend(msg)) return; // position unchanged

            if (Plugin.Instance.CfgIsHost.Value)
                NetworkManager.Instance.BroadcastToClients(msg, DeliveryMethod.ReliableOrdered);
            else
                NetworkManager.Instance.SendToServer(msg, DeliveryMethod.ReliableOrdered);
        }
    }


    // ── Log spam suppression ─────────────────────────────────────────────
    //
    // 3D WebView dumps base64-encoded data into Unity logs, drowning out
    // useful debug output. Suppress any log line containing "[3D WebView]".

    [HarmonyPatch(typeof(Debug), nameof(Debug.Log), new[] { typeof(object) })]
    public static class Patch_Debug_Log_Suppress3DWebView
    {
        static bool Prefix(object message)
        {
            return message is not string s || !s.Contains("[3D WebView]");
        }
    }

    [HarmonyPatch(typeof(Debug), nameof(Debug.LogWarning), new[] { typeof(object) })]
    public static class Patch_Debug_LogWarning_Suppress3DWebView
    {
        static bool Prefix(object message)
        {
            return message is not string s || !s.Contains("[3D WebView]");
        }
    }

    [HarmonyPatch(typeof(Debug), nameof(Debug.LogError), new[] { typeof(object) })]
    public static class Patch_Debug_LogError_Suppress3DWebView
    {
        static bool Prefix(object message)
        {
            return message is not string s || !s.Contains("[3D WebView]");
        }
    }


    // ── PvP: flight deck suppression + full pipeline sync ──────────────────
    //
    // In PvP, each player is authoritative for their own carriers' flight ops.
    // We block independent flight ops on enemy carriers but allow network-synced
    // launches through so both sides run the FULL pipeline.
    //
    // Sync approach: both sides run the full flight deck pipeline (elevator,
    // taxi, launch). When the authoritative player launches from their carrier:
    //   1. createLaunchTask Postfix sends FlightOpsMessage(Launch)
    //   2. Remote side receives → calls createLaunchTask with bypass flag
    //   3. Both sides run the pipeline independently
    //   4. When launchVehicle fires on either side → SpawnId exchange + ID remap
    //   5. State updates are blocked for pipeline aircraft until _isInFlight=true
    //
    // Blocking levels:
    //   - AI.HandleCarrierFunctions — block AI decision-maker
    //   - AI.LaunchAirstrike — block public airstrike method
    //   - FlightDeck.createLaunchTask — block unless ApplyingFromNetwork
    //   - FlightDeck.launchVehicle — block unless NetworkSyncedVessels

    [HarmonyPatch]
    public static class Patch_AI_HandleCarrierFunctions
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(AI), "HandleCarrierFunctions");

        static bool Prefix(AI __instance)
        {
            if (!NetworkManager.Instance.IsConnected) return true;
            if (SessionManager.SceneLoading) return true;

            // PvP hybrid: let HandleCarrierFunctions run for ALL carriers on both
            // sides so carrier AI can manage speed/heading/recovery. Independent
            // launches are prevented by the createLaunchTask prefix instead.
            if (Plugin.Instance.CfgPvP.Value)
                return true;

            // Co-op: only host runs AI flight ops
            return Plugin.Instance.CfgIsHost.Value;
        }
    }

    [HarmonyPatch]
    public static class Patch_AI_LaunchAirstrike
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(AI), "LaunchAirstrike");

        static bool Prefix(AI __instance)
        {
            if (!Plugin.Instance.CfgPvP.Value) return true;
            if (!NetworkManager.Instance.IsConnected) return true;
            // Block enemy AI from independently launching airstrikes;
            // launches are synced via FlightOpsHandler from authoritative side
            var unit = AIAutoFireState._aiBaseObjectField?.GetValue(__instance) as ObjectBase;
            return unit != null && unit._taskforce == Globals._playerTaskforce;
        }
    }

    // ── FlightDeck.createLaunchTask: block + sync ───────────────────────────

    [HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.createLaunchTask))]
    public static class Patch_FlightDeck_CreateLaunchTask
    {
        static bool Prefix(FlightDeck __instance)
        {
            if (!Plugin.Instance.CfgPvP.Value) return true;
            if (!NetworkManager.Instance.IsConnected) return true;

            // Block createLaunchTask on enemy carriers unconditionally in PvP.
            // Remote launches are handled via SpawnVehicle → launchVehicle directly.
            var vessel = __instance._baseObject;
            if (vessel != null && vessel._taskforce != Globals._playerTaskforce)
            {
                Plugin.Log.LogDebug($"[FlightOps] Blocked createLaunchTask on enemy carrier {vessel.UniqueID}");
                return false;
            }
            return true;
        }

        static void Postfix(FlightDeck __instance, PendingLaunchTask __result,
            VehicleTypeOnBoard vehicle, Loadout loadout, Squadron squadron,
            string callsign, LaunchTaskParameters ltp, bool allowLaunch)
        {
            // Don't re-send when applying from network
            if (FlightOpsHandler.ApplyingFromNetwork) return;
            if (!Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (SessionManager.SceneLoading) return;

            var vessel = __instance._baseObject;
            if (vessel == null) return;
            if (vessel._taskforce != Globals._playerTaskforce) return;
            if (__result == null) return; // launch failed (no ammo etc.)

            int vehicleIdx  = __instance._vehiclesOnBoard.IndexOf(vehicle);
            int loadoutIdx  = vehicle.Loadouts.IndexOf(loadout);
            int squadronIdx = vehicle.Squadrons.IndexOf(squadron);
            int callsignIdx = squadron.Callsigns.IndexOf(callsign);

            var msg = new FlightOpsMessage
            {
                OpsType         = FlightOpsType.Launch,
                VesselId        = vessel.UniqueID,
                VehicleIndex    = vehicleIdx,
                LoadoutIndex    = loadoutIdx,
                SquadronIndex   = squadronIdx,
                CallsignIndex   = callsignIdx,
                LaunchCount     = ltp._launchCount,
                MissionType     = (byte)ltp._missionType,
                AllowLaunch     = allowLaunch,
                ReadyUpDuration = __result._duration,
                HostTimingsMultiplier = Globals._multipliers[
                    Singleton<OptionsManager>.Instance.FlightDeckTimingsMode],
            };

            NetworkManager.Instance.SendToOther(msg, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Plugin.Log.LogInfo($"[FlightOps] Sent createLaunchTask: vessel={vessel.UniqueID} " +
                $"vehicle={vehicleIdx} loadout={loadoutIdx} squadron={squadronIdx} " +
                $"count={ltp._launchCount} vehicleType={vehicle._type}");
        }
    }

    // ── FlightDeck.launchVehicle: block + sync SpawnId ──────────────────────

    [HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.launchVehicle))]
    public static class Patch_FlightDeck_LaunchVehicle
    {
        static bool Prefix(FlightDeck __instance)
        {
            if (!Plugin.Instance.CfgPvP.Value) return true;
            if (!NetworkManager.Instance.IsConnected) return true;

            var vessel = __instance._baseObject;
            if (vessel == null) return true;
            if (vessel._taskforce == Globals._playerTaskforce) return true;

            // Enemy carrier: allow if this vessel has network-synced launches
            if (FlightOpsHandler.NetworkSyncedVessels.Contains(vessel.UniqueID))
                return true;

            Plugin.Log.LogDebug($"[FlightOps] Blocked launchVehicle on enemy carrier {vessel.UniqueID}");
            return false;
        }

        static void Postfix(FlightDeck __instance, ObjectBase __result,
            Elevator elevator, int vehicleIndex, int loadoutIndex,
            int squadronIndex, int callsignIndex, bool multipleLaunch,
            LaunchTaskParameters ltp)
        {
            if (!Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (FlightOpsHandler.ApplyingFromNetwork) return;
            if (__result == null) return;

            var vessel = __instance._baseObject;
            if (vessel == null) return;

            // Only the authoritative side (carrier owner) sends SpawnVehicle
            if (vessel._taskforce != Globals._playerTaskforce) return;

            // Assign collision-safe ID. Both sides start the same scenario with
            // the same UniqueID counter, so simultaneous launches produce
            // identical IDs. Range separation (host: 2M+, client: 3M+) prevents
            // cross-side collisions.
            // Use save/restore on _UID to prevent SetUniqueId from polluting
            // the game's global counter into our safe range.
            int safeId = FlightOpsHandler.NextSafeId();
            int originalId = __result.UniqueID;
            int prevUid = Singleton<SceneCreator>.Instance._UID;
            __result.SetUniqueId(safeId);
            Singleton<SceneCreator>.Instance._UID = prevUid;

            int elevatorIdx = __instance._elevators.IndexOf(elevator);

            var msg = new FlightOpsMessage
            {
                OpsType          = FlightOpsType.SpawnVehicle,
                VesselId         = vessel.UniqueID,
                VehicleIndex     = vehicleIndex,
                LoadoutIndex     = loadoutIndex,
                SquadronIndex    = squadronIndex,
                CallsignIndex    = callsignIndex,
                MissionType      = (byte)ltp._missionType,
                SpawnedUnitId    = safeId,
                ElevatorIndex    = elevatorIdx,
                IsMultipleLaunch = multipleLaunch,
                VehicleTypeName  = __result._type.ToString(),
            };
            FlightOpsHandler.RecordSpawn(safeId, new FlightOpsHandler.AircraftSpawnRecord
            {
                CarrierVesselId = vessel.UniqueID,
                VehicleTypeName = __result._type.ToString(),
                LoadoutIndex    = loadoutIndex,
                SquadronIndex   = squadronIndex,
                CallsignIndex   = callsignIndex,
                MissionType     = (byte)ltp._missionType,
                IsMultipleLaunch = multipleLaunch,
            });
            NetworkManager.Instance.SendToOther(msg, LiteNetLib.DeliveryMethod.ReliableOrdered);
            Plugin.Log.LogInfo($"[FlightOps] Sent SpawnVehicle: vessel={vessel.UniqueID} " +
                $"aircraft={originalId}->{safeId} elevator={elevatorIdx} type={__result._type}");
        }
    }


    // ── Host-authoritative AI weapon sync ──────────────────────────────────
    //
    // Client AI weapon methods (AutoFireGunsInRange, AutoAttackOpponentInRange)
    // are suppressed because client sensor/contact state diverges from host —
    // the client may not detect enemies as hostile, so independent AI fails.
    // Host runs AI normally and broadcasts AutoFireWeapon orders to the client.
    // The InsertEngageTask Postfix broadcasts auto-attacks from the host only
    // when InsideAIAutoFire is true (prevents re-broadcast from WeaponSystem
    // re-inserts after alignment).

    /// <summary>
    /// True while inside AI.AutoFireGunsInRange or AI.AutoAttackOpponentInRange
    /// on the host. Prevents the InsertEngageTask Postfix from re-broadcasting
    /// weapon system re-inserts (WeaponSystem.cs:1169 re-calls InsertEngageTask
    /// with _isAutoEngaging=true after weapon alignment).
    /// </summary>
    static class AIAutoFireState
    {
        internal static bool InsideAIAutoFire;

        // ── Diagnostic: auto-fire pipeline tracing ────────────────────────
        private static readonly System.Diagnostics.Stopwatch _diagTimer = System.Diagnostics.Stopwatch.StartNew();
        private static int _diagSeqId;

        /// <summary>Monotonic ms timestamp for correlating log entries across host/client.</summary>
        internal static long DiagMs => _diagTimer.ElapsedMilliseconds;

        /// <summary>Incrementing sequence ID to pair send/receive log entries.</summary>
        internal static int NextDiagSeq() => System.Threading.Interlocked.Increment(ref _diagSeqId);

        // Cached reflection for AI._baseObject (private field)
        // Internal: also used by Patch_AI_HandleCarrierFunctions and Patch_AI_LaunchAirstrike
        internal static readonly System.Reflection.FieldInfo _aiBaseObjectField =
            AccessTools.Field(typeof(AI), "_baseObject");

        /// <summary>Shared prefix for AI auto-fire/auto-attack patches.</summary>
        internal static bool Prefix(AI instance)
        {
            if (!NetworkManager.Instance.IsConnected) return true;

            // PvP: suppress enemy AI auto-fire BEFORE SceneLoading check.
            // During the connection window SceneLoading is true, which would
            // bypass the PvP guard and let enemy AI fire unsuppressed.
            if (Plugin.Instance.CfgPvP.Value)
            {
                var unit = _aiBaseObjectField?.GetValue(instance) as ObjectBase;
                if (unit == null || unit._taskforce != Globals._playerTaskforce) return false;
                InsideAIAutoFire = true;
                return true;
            }

            if (SessionManager.SceneLoading) return true;

            if (!Plugin.Instance.CfgIsHost.Value) return false;
            InsideAIAutoFire = true;
            return true;
        }
    }

    [HarmonyPatch]
    public static class Patch_AI_AutoFireGunsInRange
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(AI), "AutoFireGunsInRange");

        static bool Prefix(AI __instance) => AIAutoFireState.Prefix(__instance);
        static void Postfix() => AIAutoFireState.InsideAIAutoFire = false;
    }

    [HarmonyPatch]
    public static class Patch_AI_AutoAttackOpponentInRange
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(AI), "AutoAttackOpponentInRange");

        static bool Prefix(AI __instance) => AIAutoFireState.Prefix(__instance);
        static void Postfix() => AIAutoFireState.InsideAIAutoFire = false;
    }

    /// <summary>
    /// Prefix: PvP guard — track which enemy puppet units have received a network
    /// fire order. Block HandleEngageTasks for enemy puppets that have never received
    /// a network order, catching pre-existing engage tasks loaded from save files
    /// that bypass AddEngageTask/InsertEngageTask Harmony patches.
    ///
    /// Postfix: zero out the reaction delay for auto-engage tasks on the receiving
    /// side. The delay (Random * _maxReactiontime) causes a 0-2s lag because the
    /// weapon system starts cold after receiving a network fire order. Since ALL
    /// enemy auto-engage tasks come from the network, skipping the delay is safe.
    /// </summary>
    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.HandleEngageTasks))]
    public static class Patch_ObjectBase_HandleEngageTasks
    {
        /// <summary>
        /// Tracks enemy unit IDs that have received at least one network fire order.
        /// Once a unit receives a network order, HandleEngageTasks is allowed to run
        /// for it (the pre-existing tasks were flushed by CeaseFire in OnSceneReady,
        /// so any remaining tasks are from legitimate network orders).
        /// </summary>
        private static readonly HashSet<int> _networkOrderedUnits = new();

        /// <summary>Call when an enemy puppet receives a fire order from the network.</summary>
        internal static void MarkNetworkOrdered(int unitId) => _networkOrderedUnits.Add(unitId);

        /// <summary>Clear tracking on disconnect/scene change.</summary>
        internal static void Reset() => _networkOrderedUnits.Clear();

        static bool Prefix(ObjectBase __instance)
        {
            if (!Plugin.Instance.CfgPvP.Value) return true;
            if (!NetworkManager.Instance.IsConnected) return true;
            if (SessionManager.SceneLoading) return true;

            // Own units: always process normally
            if (__instance._taskforce == Globals._playerTaskforce) return true;

            // Enemy puppet that has received a network order: allow processing
            if (_networkOrderedUnits.Contains(__instance.UniqueID)) return true;

            // Enemy puppet with no network orders: block processing.
            // Any engage tasks are residual from the save file.
            return false;
        }

        static void Postfix(ObjectBase __instance)
        {
            bool isPvP = Plugin.Instance.CfgPvP.Value;
            // Co-op: only client needs zero-delay (host is authoritative)
            if (!isPvP && Plugin.Instance.CfgIsHost.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (__instance._obp == null) return;

            // PvP: only zero delays for enemy ships (whose fires came from network)
            if (isPvP && __instance._taskforce == Globals._playerTaskforce) return;

            foreach (var ws in __instance._obp._weaponSystems)
            {
                if (ws._executingEngageTask && ws._isAutoEngaging && ws._engageDelay > 0f)
                {
                    if (Plugin.Instance.CfgVerboseDebug.Value)
                        Plugin.Log.LogDebug($"[AutoFire DIAG] t={AIAutoFireState.DiagMs}ms ZERO_DELAY " +
                            $"unit={__instance.UniqueID} name={__instance.name} " +
                            $"wasDelay={ws._engageDelay:F3}s");
                    ws._engageDelay = 0f;
                }
            }
        }
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.InsertEngageTask))]
    public static class Patch_ObjectBase_InsertEngageTask
    {
        // AI callers (AutoFireGunsInRange, AutoAttackOpponentInRange) use the
        // returned EngageTask immediately (e.g. engageTask._uid), so the Prefix
        // only intercepts when AddEngageTask sets the DelayingPlayerFire flag
        // (player-initiated fires via AddEngageTask, which ignores the return).

        private static bool _suppressPostfix;

        static bool Prefix(ObjectBase __instance, ref EngageTask __result,
                           string ammoId, ObjectBase targetObject, Vector3 targetPosition,
                           int shotsToFire, bool autoAttack, int priority)
        {
            // PvP safety net: suppress any fire from enemy ships (catches pre-connection
            // queued engage tasks and any paths that bypass AddEngageTask)
            if (!OrderHandler.ApplyingFromNetwork
                && Plugin.Instance.CfgPvP.Value
                && NetworkManager.Instance.IsConnected
                && __instance._taskforce != Globals._playerTaskforce)
            {
                _suppressPostfix = true;
                __result = null;
                return false;
            }

            if (!Patch_ObjectBase_AddEngageTask.DelayingPlayerFire) return true;
            Patch_ObjectBase_AddEngageTask.DelayingPlayerFire = false;

            // Send immediately so remote side gets it as fast as possible
            // PvP: translate target ID so remote side can resolve it
            int fireTargetId = targetObject?.UniqueID ?? 0;
            if (Plugin.Instance.CfgPvP.Value && fireTargetId > 0)
                fireTargetId = ProjectileIdMapper.TranslateForRemote(fireTargetId);

            var msg = new PlayerOrderMessage
            {
                SourceEntityId = __instance.UniqueID,
                Order          = OrderType.FireWeapon,
                AmmoId         = ammoId,
                ShotsToFire    = shotsToFire,
                TargetEntityId = fireTargetId,
            };
            // PvP: always convert to GeoPosition (floating-origin safe fallback)
            if (Plugin.Instance.CfgPvP.Value)
            {
                var geo = Utils.worldPositionFromUnityToLongLat(targetPosition, Globals._currentCenterTile);
                msg.TargetX = (float)geo._longitude;
                msg.TargetY = (float)geo._height;
                msg.TargetZ = (float)geo._latitude;
            }
            else
            {
                msg.TargetX = targetPosition.x;
                msg.TargetY = targetPosition.y;
                msg.TargetZ = targetPosition.z;
            }
            NetworkManager.Instance.SendToOther(msg);

            // Schedule delayed local execution (RTT/2 to match remote arrival)
            var unit = __instance;
            var capturedAmmo = ammoId;
            var capturedTarget = targetObject;
            var capturedPos = targetPosition;
            var capturedShots = shotsToFire;
            var capturedPriority = priority;
            OrderDelayQueue.Enqueue(() =>
            {
                if (unit == null || unit.IsDestroyed) return;
                OrderHandler.ApplyingFromNetwork = true;
                try { unit.InsertEngageTask(capturedAmmo, capturedTarget, capturedPos, capturedShots, capturedPriority, false); }
                finally { OrderHandler.ApplyingFromNetwork = false; }
            });

            _suppressPostfix = true;
            __result = null;
            return false;
        }

        static void Postfix(ObjectBase __instance, string ammoId, ObjectBase targetObject,
                            Vector3 targetPosition, int shotsToFire, bool autoAttack, int priority)
        {
            if (_suppressPostfix) { _suppressPostfix = false; return; }
            if (OrderHandler.ApplyingFromNetwork) return;
            if (SessionManager.SceneLoading) return;
            if (!NetworkManager.Instance.IsConnected) return;

            if (autoAttack)
            {
                // Only broadcast when inside AI auto-fire methods.
                // WeaponSystem re-inserts (after alignment) have autoAttack=true but
                // InsideAIAutoFire is false at that point → no double-broadcast.
                if (!AIAutoFireState.InsideAIAutoFire) return;

                bool isPvP = Plugin.Instance.CfgPvP.Value;

                // Co-op: only host broadcasts. PvP: both sides broadcast.
                if (!isPvP && !Plugin.Instance.CfgIsHost.Value) return;

                int targetId = targetObject?.UniqueID ?? 0;
                // PvP: translate target ID so remote side can resolve it
                if (isPvP && targetId > 0)
                    targetId = ProjectileIdMapper.TranslateForRemote(targetId);

                var order = new PlayerOrderMessage
                {
                    SourceEntityId = __instance.UniqueID,
                    Order          = OrderType.AutoFireWeapon,
                    AmmoId         = ammoId,
                    ShotsToFire    = shotsToFire,
                    TargetEntityId = targetId,
                    Heading        = priority,
                };

                // PvP: use GeoPosition (floating-origin safe)
                if (isPvP)
                {
                    var geo = Utils.worldPositionFromUnityToLongLat(targetPosition, Globals._currentCenterTile);
                    order.TargetX = (float)geo._longitude;
                    order.TargetY = (float)geo._height;
                    order.TargetZ = (float)geo._latitude;
                }
                else
                {
                    order.TargetX = targetPosition.x;
                    order.TargetY = targetPosition.y;
                    order.TargetZ = targetPosition.z;
                }

                int diagSeq = AIAutoFireState.NextDiagSeq();

                NetworkManager.Instance.SendToOther(order);

                if (Plugin.Instance.CfgVerboseDebug.Value)
                    Plugin.Log.LogDebug($"[AutoFire DIAG] t={AIAutoFireState.DiagMs}ms SEND seq={diagSeq} " +
                        $"unit={__instance.UniqueID} name={__instance.name} ammo={ammoId} " +
                        $"targetLocal={targetObject?.UniqueID ?? 0} targetSent={targetId} targetName={targetObject?.name ?? "pos"} " +
                        $"shots={shotsToFire} priority={priority} isHost={Plugin.Instance.CfgIsHost.Value}");
                return;
            }

            // Player orders (co-op path — PvP player fires are handled by the Prefix above)
            if (!Plugin.Instance.CfgPvP.Value && NetworkManager.Instance.IsConnected
                && UnitLockManager.IsLockedByRemote(__instance.UniqueID)) return;
            if (!Plugin.Instance.CfgIsHost.Value && !TaskforceAssignmentManager.ClientMayControl(__instance))
                return;

            var playerOrder = new PlayerOrderMessage
            {
                SourceEntityId = __instance.UniqueID,
                Order          = OrderType.FireWeapon,
                AmmoId         = ammoId,
                ShotsToFire    = shotsToFire,
                TargetEntityId = targetObject?.UniqueID ?? 0,
                TargetX        = targetPosition.x,
                TargetY        = targetPosition.y,
                TargetZ        = targetPosition.z,
            };

            if (Plugin.Instance.CfgIsHost.Value)
                NetworkManager.Instance.BroadcastToClients(playerOrder);
            else
                NetworkManager.Instance.SendToServer(playerOrder);
        }
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.AddEngageTask))]
    public static class Patch_ObjectBase_AddEngageTask
    {
        /// <summary>
        /// Set by AddEngageTask Prefix when a PvP player fire should be delayed.
        /// Checked and reset by InsertEngageTask Prefix (which has the method params).
        /// </summary>
        internal static bool DelayingPlayerFire;

        static bool Prefix(ObjectBase __instance, EngageTask engageTask)
        {
            // Safety reset: if a previous fire path set this flag but never
            // reached InsertEngageTask (e.g. DropSonobuoyTask calling AddEngageTask
            // directly), clear it so it doesn't corrupt the next fire order.
            if (DelayingPlayerFire)
            {
                Plugin.Log.LogWarning("[Fire] DelayingPlayerFire was stale — resetting");
                DelayingPlayerFire = false;
            }

            // Network-applied orders always execute
            if (OrderHandler.ApplyingFromNetwork) return true;
            if (SessionManager.SceneLoading) return true;

            // PvP: suppress ALL engage tasks from ships we don't control.
            // The other player's instance runs AI for their own ships and syncs
            // the results to us via network orders (caught by ApplyingFromNetwork above).
            // This catches every AI firing path: AutoAttackOpponentInRange,
            // CheckForPresetAttack, BidOnEngagement, CounterLaunchOnBearing, etc.
            if (Plugin.Instance.CfgPvP.Value && NetworkManager.Instance.IsConnected
                && __instance._taskforce != Globals._playerTaskforce)
                return false;

            // Auto-attacks pass through (sync handled by InsertEngageTask Postfix)
            if (engageTask._isAutoAttack) return true;

            // Client control check for direct AddEngageTask callers
            // (e.g. LaunchNoisemaker). Send logic is in InsertEngageTask.
            if (!Plugin.Instance.CfgPvP.Value && NetworkManager.Instance.IsConnected
                && UnitLockManager.IsLockedByRemote(__instance.UniqueID)) return false;
            if (!Plugin.Instance.CfgIsHost.Value && !TaskforceAssignmentManager.ClientMayControl(__instance))
                return false;

            // Sonobuoy drops come through AddEngageTask directly (never InsertEngageTask),
            // so we must send the network message here. Detect by ammo name prefix.
            bool isSonobuoy = engageTask._ammoId != null
                && engageTask._ammoId.IndexOf("ssq", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isSonobuoy && NetworkManager.Instance.IsConnected)
            {
                bool isPvPSono = Plugin.Instance.CfgPvP.Value;
                bool isHostSono = Plugin.Instance.CfgIsHost.Value;

                if (isPvPSono)
                {
                    // PvP: encode as GeoPosition (floating-origin safe) and send to opponent
                    var geo = Utils.worldPositionFromUnityToLongLat(
                        engageTask._targetPosition, Globals._currentCenterTile);
                    var msg = new PlayerOrderMessage
                    {
                        SourceEntityId = __instance.UniqueID,
                        Order          = OrderType.DropSonobuoy,
                        AmmoId         = engageTask._ammoId,
                        DestX          = (float)geo._longitude,
                        DestY          = (float)geo._height,
                        DestZ          = (float)geo._latitude,
                    };
                    NetworkManager.Instance.SendToOther(msg);
                    Plugin.Log.LogInfo($"[Sonobuoy] PvP sent drop: unit={__instance.UniqueID} ammo={engageTask._ammoId}");
                }
                else if (!isHostSono && TaskforceAssignmentManager.ClientMayControl(__instance))
                {
                    // Co-op client: send to host using local coords (shared origin in co-op)
                    var msg = new PlayerOrderMessage
                    {
                        SourceEntityId = __instance.UniqueID,
                        Order          = OrderType.DropSonobuoy,
                        AmmoId         = engageTask._ammoId,
                        DestX          = engageTask._targetPosition.x,
                        DestY          = engageTask._targetPosition.y,
                        DestZ          = engageTask._targetPosition.z,
                    };
                    NetworkManager.Instance.SendToServer(msg);
                    Plugin.Log.LogInfo($"[Sonobuoy] Co-op client sent drop: unit={__instance.UniqueID} ammo={engageTask._ammoId}");
                }
                else if (isHostSono)
                {
                    // Co-op host: broadcast to client so client sees host-initiated sonobuoy drops
                    var msg = new PlayerOrderMessage
                    {
                        SourceEntityId = __instance.UniqueID,
                        Order          = OrderType.DropSonobuoy,
                        AmmoId         = engageTask._ammoId,
                        DestX          = engageTask._targetPosition.x,
                        DestY          = engageTask._targetPosition.y,
                        DestZ          = engageTask._targetPosition.z,
                    };
                    NetworkManager.Instance.BroadcastToClients(msg);
                    Plugin.Log.LogInfo($"[Sonobuoy] Co-op host broadcast drop: unit={__instance.UniqueID} ammo={engageTask._ammoId}");
                }
                return true; // execute locally on whoever sent
            }

            // PvP: flag player fires for delay handling in InsertEngageTask Prefix
            if (Plugin.Instance.CfgPvP.Value && NetworkManager.Instance.IsConnected)
                DelayingPlayerFire = true;

            return true;
        }
    }


    // NOTE: DropSonobuoyTask.OnExecute() cannot be patched by Harmony (protected
    // override on a NodeCanvas ActionTask — patch silently fails to apply).
    // Sonobuoy sync is handled in AddEngageTask above by detecting "ssq" ammo names.

    // ── Phase 3: Additional command replication ─────────────────────────────

    [HarmonyPatch(typeof(Submarine), nameof(Submarine.setDepth))]
    public static class Patch_Submarine_SetDepth
    {
        // The game internally calls setDepth() every update for depth-keeping.
        // Without guards, the Harmony patch broadcasts every one of these calls,
        // flooding the network with stale depth values that override player commands.
        //
        // Fix: after a player/network depth command, lock to that depth briefly.
        // Internal calls that try to revert to the old depth during the lock are
        // suppressed. Calls arriving after the grace period are treated as new
        // player commands.
        private static readonly Dictionary<int, float> _lockedDepth = new();
        private static readonly Dictionary<int, float> _lockTime = new();
        private const float GracePeriod = 1f; // seconds to suppress internal reverts

        static PlayerOrderMessage Msg(Submarine s, float depth) => new PlayerOrderMessage
        {
            SourceEntityId = s.UniqueID,
            Order          = OrderType.SetDepth,
            Speed          = depth,
        };

        /// <summary>Clear locks on disconnect / scene load.</summary>
        internal static void Reset()
        {
            _lockedDepth.Clear();
            _lockTime.Clear();
        }

        static bool Prefix(Submarine __instance, float depth, out bool __state)
        {
            __state = false; // Postfix broadcast flag

            // Network-applied order: always allow, set lock
            if (OrderHandler.ApplyingFromNetwork)
            {
                _lockedDepth[__instance.UniqueID] = depth;
                _lockTime[__instance.UniqueID] = Time.unscaledTime;
                return true;
            }

            if (SessionManager.SceneLoading) return true;
            if (!NetworkManager.Instance.IsConnected) return true;

            int id = __instance.UniqueID;
            float now = Time.unscaledTime;

            // Check if we have an active lock
            if (_lockTime.TryGetValue(id, out float setAt) && _lockedDepth.TryGetValue(id, out float locked))
            {
                bool sameDepth = Mathf.Abs(depth - locked) < 1f;
                bool inGrace = (now - setAt) < GracePeriod;

                if (sameDepth)
                    return true; // Maintenance of current depth — execute locally, don't send

                if (inGrace)
                    return false; // Internal call trying to revert during grace — suppress entirely
            }

            // Genuine depth change (player command or AI after grace period)
            _lockedDepth[id] = depth;
            _lockTime[id] = now;
            __state = true; // Signal Postfix to broadcast

            if (!Plugin.Instance.CfgPvP.Value && NetworkManager.Instance.IsConnected
                && UnitLockManager.IsLockedByRemote(__instance.UniqueID))
            {
                __state = false; // don't broadcast
                return false;
            }

            if (Plugin.Instance.CfgIsHost.Value) return true;

            // PvP: don't sync weapon internals
            if (Plugin.Instance.CfgPvP.Value && __instance is WeaponBase) return true;

            if (!TaskforceAssignmentManager.ClientMayControl(__instance)) return false;
            NetworkManager.Instance.SendToServer(Msg(__instance, depth));
            return true;
        }

        static void Postfix(Submarine __instance, float depth, bool __state)
        {
            if (!__state) return; // Prefix didn't flag this as a genuine change
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (SessionManager.SceneLoading) return;
            NetworkManager.Instance.BroadcastToClients(Msg(__instance, depth));
        }
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.CeaseFire))]
    public static class Patch_ObjectBase_CeaseFire
    {
        static PlayerOrderMessage Msg(ObjectBase u) => new PlayerOrderMessage
        {
            SourceEntityId = u.UniqueID,
            Order          = OrderType.CeaseFire,
        };

        static bool Prefix(ObjectBase __instance, bool report)
        {
            if (OrderHandler.ApplyingFromNetwork) return true;
            if (SessionManager.SceneLoading) return true;
            if (!report) return true;

            // PvP: delay local execution so both sides cease at the same wall-clock time
            if (Plugin.Instance.CfgPvP.Value && NetworkManager.Instance.IsConnected
                && __instance._taskforce == Globals._playerTaskforce)
            {
                NetworkManager.Instance.SendToOther(Msg(__instance));

                var unit = __instance;
                OrderDelayQueue.Enqueue(() =>
                {
                    if (unit == null || unit.IsDestroyed) return;
                    OrderHandler.ApplyingFromNetwork = true;
                    try { unit.CeaseFire(true, true, true, false, true, true); }
                    finally { OrderHandler.ApplyingFromNetwork = false; }
                });

                return false;
            }

            // Co-op: unchanged
            return OrderSyncHelper.Prefix(__instance, Msg(__instance));
        }

        static void Postfix(ObjectBase __instance, bool report)
        {
            if (!report) return;
            // PvP: message already sent in Prefix
            if (Plugin.Instance.CfgPvP.Value && NetworkManager.Instance.IsConnected) return;

            // Co-op: unchanged
            OrderSyncHelper.Postfix(__instance, Msg(__instance));
        }
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.SetWeaponStatus))]
    public static class Patch_ObjectBase_SetWeaponStatus
    {
        static PlayerOrderMessage Msg(ObjectBase u, ObjectBase.WeaponStatus status) => new PlayerOrderMessage
        {
            SourceEntityId = u.UniqueID,
            Order          = OrderType.SetWeaponStatus,
            Speed          = (float)(int)status,
        };

        static bool Prefix(ObjectBase __instance, ObjectBase.WeaponStatus status) =>
            OrderSyncHelper.Prefix(__instance, Msg(__instance, status));

        static void Postfix(ObjectBase __instance, ObjectBase.WeaponStatus status) =>
            OrderSyncHelper.Postfix(__instance, Msg(__instance, status));
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.setEMCON))]
    public static class Patch_ObjectBase_SetEMCON
    {
        static PlayerOrderMessage Msg(ObjectBase u, bool emcon) => new PlayerOrderMessage
        {
            SourceEntityId = u.UniqueID,
            Order          = OrderType.SetEMCON,
            Speed          = emcon ? 1f : 0f,
        };

        static bool Prefix(ObjectBase __instance, bool emcon) =>
            OrderSyncHelper.Prefix(__instance, Msg(__instance, emcon));

        static void Postfix(ObjectBase __instance, bool emcon) =>
            OrderSyncHelper.Postfix(__instance, Msg(__instance, emcon));
    }


    // ── Order sync helper ──────────────────────────────────────────────────
    //
    // Shared helper reduces boilerplate across all order sync patches.
    // Each patch defines a Msg() factory and delegates to OrderSyncHelper
    // for the Prefix/Postfix guard logic and network dispatch.

    static class OrderSyncHelper
    {
        /// <summary>Set during mast toggles to prevent SensorSystem patches from double-sending.</summary>
        internal static bool SuppressSensorPatch;

        internal static bool Prefix(ObjectBase unit, PlayerOrderMessage msg)
        {
            if (OrderHandler.ApplyingFromNetwork) return true;
            if (SessionManager.SceneLoading) return true; // don't send during scene load
            // PvP: don't sync orders for weapons (missiles/torpedoes) — their internal
            // waypoint/guidance operations use local IDs meaningless to the remote side
            if (Plugin.Instance.CfgPvP.Value && unit is WeaponBase) return true;
            // Fix #54 (enhanced Fix #49): Skip order routing for chaff/countermeasure entities.
            // Primary check: ammunition type (covers initialized entities).
            // Fallback check: class name (covers entities where _ap is null during spawn).
            if (unit is WeaponBase wb)
            {
                if (wb._ap != null &&
                    (wb._ap._type == Ammunition.Type.Chaff || wb._ap._type == Ammunition.Type.Noisemaker))
                    return true;

                // Fallback: check by class name when _ap is not yet initialized
                string typeName = unit.GetType().Name;
                if (typeName.Contains("Chaff") || typeName.Contains("Noisemaker"))
                    return true;
            }
            if (!Plugin.Instance.CfgPvP.Value && NetworkManager.Instance.IsConnected
                && UnitLockManager.IsLockedByRemote(unit.UniqueID)) return false;
            if (Plugin.Instance.CfgIsHost.Value) return true;
            if (!TaskforceAssignmentManager.ClientMayControl(unit)) return false;
            if (!OrderDeduplicator.ShouldSend(msg)) return true; // duplicate — skip send, still execute locally
            NetworkManager.Instance.SendToServer(msg);
            return true;
        }

        internal static void Postfix(ObjectBase unit, PlayerOrderMessage msg)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (OrderHandler.ApplyingFromNetwork) return;
            // PvP: don't sync orders for weapons (missiles/torpedoes)
            if (Plugin.Instance.CfgPvP.Value && unit is WeaponBase) return;
            // Fix #54 (enhanced Fix #49): Same chaff/noisemaker filter as Prefix
            if (unit is WeaponBase wb2)
            {
                if (wb2._ap != null &&
                    (wb2._ap._type == Ammunition.Type.Chaff || wb2._ap._type == Ammunition.Type.Noisemaker))
                    return;

                string typeName = unit.GetType().Name;
                if (typeName.Contains("Chaff") || typeName.Contains("Noisemaker"))
                    return;
            }
            if (SessionManager.SceneLoading) return; // don't broadcast during scene load
            if (!OrderDeduplicator.ShouldSend(msg)) return; // duplicate — skip broadcast
            NetworkManager.Instance.BroadcastToClients(msg);
        }

        internal static PlayerOrderMessage SensorMsg(ObjectBase u, int group, bool enable) =>
            new PlayerOrderMessage
            {
                SourceEntityId = u.UniqueID,
                Order          = OrderType.SensorToggle,
                Speed          = enable ? 1f : 0f,
                Heading        = group,
            };

        internal static PlayerOrderMessage MastMsg(ObjectBase u, int mastId) =>
            new PlayerOrderMessage
            {
                SourceEntityId = u.UniqueID,
                Order          = OrderType.SubmarineMast,
                Heading        = mastId,
            };

        /// <summary>
        /// Returns the sensor group for a SensorSystem, or -1 if not a synced type.
        /// 0 = air search radar, 1 = surface search radar.
        /// Sonar active/passive is handled separately via the IsActive subscription.
        /// </summary>
        internal static int GetRadarGroup(SensorSystem sensor, ObjectBase unit)
        {
            if (!(sensor is SensorSystemRadar radar)) return -1;
            var obp = unit._obp;
            if (obp == null) return -1;
            if (obp._airSearchRadars.Contains(radar)) return 0;
            if (obp._surfaceSearchRadars.Contains(radar)) return 1;
            return -1; // FCR, targeting radar — not player-toggled
        }
    }

    // ── Order deduplication ─────────────────────────────────────────────────
    //
    // The game engine calls patched methods (setTelegraph, UpdateSimulation, etc.)
    // every frame as part of normal autopilot/simulation. Without dedup, identical
    // orders flood the network at tick rate. This cache tracks last-sent values
    // per (entity, orderType, subKey) and suppresses sends when nothing changed.

    static class OrderDeduplicator
    {
        private struct Fingerprint
        {
            public float V1, V2, V3, V4;

            public bool Matches(Fingerprint other, float eps = 0.001f) =>
                Math.Abs(V1 - other.V1) < eps && Math.Abs(V2 - other.V2) < eps &&
                Math.Abs(V3 - other.V3) < eps && Math.Abs(V4 - other.V4) < eps;
        }

        private static readonly Dictionary<(int, OrderType, int), Fingerprint> _cache = new();
        private static readonly Dictionary<(int, OrderType, int), float> _lastSendTime = new();

        private static float GetMinInterval(OrderType order) => order switch
        {
            OrderType.SensorToggle    => 10f,
            OrderType.RemoveWaypoints => 2f,
            OrderType.DeleteWaypoint  => 1f,
            OrderType.SetSpeed        => 0.5f,
            OrderType.SetEMCON        => 10f,
            _                         => 0f,
        };

        /// <summary>
        /// Returns true if the order differs from the last-sent value (should send).
        /// Returns false if it's a duplicate (suppress). One-shot orders always return true.
        /// </summary>
        internal static bool ShouldSend(PlayerOrderMessage msg)
        {
            switch (msg.Order)
            {
                case OrderType.FireWeapon:
                case OrderType.AutoFireWeapon:
                case OrderType.CeaseFire:
                case OrderType.DropSonobuoy:
                case OrderType.SubmarineMast:
                case OrderType.SetAltitude:
                case OrderType.ReturnToBase:
                case OrderType.ClassifyContact:
                    return true;
            }

            var key = MakeKey(msg);
            var fp  = MakeFingerprint(msg);

            if (_cache.TryGetValue(key, out var last) && last.Matches(fp))
                return false;

            float minInterval = GetMinInterval(msg.Order);
            if (minInterval > 0f && _lastSendTime.TryGetValue(key, out var lastTime) &&
                Time.unscaledTime - lastTime < minInterval)
                return false;

            _cache[key] = fp;
            _lastSendTime[key] = Time.unscaledTime;
            return true;
        }

        /// <summary>Update cache without checking (for network-received orders).</summary>
        internal static void UpdateCache(PlayerOrderMessage msg)
        {
            var key = MakeKey(msg);
            _cache[key] = MakeFingerprint(msg);
            _lastSendTime[key] = Time.unscaledTime;
        }

        internal static void Clear()
        {
            _cache.Clear();
            _lastSendTime.Clear();
        }

        private static (int, OrderType, int) MakeKey(PlayerOrderMessage msg)
        {
            int subKey = msg.Order switch
            {
                OrderType.EditWaypoint => (int)msg.Speed,   // waypoint index
                OrderType.SensorToggle => (int)msg.Heading, // sensor group
                _ => 0,
            };
            return (msg.SourceEntityId, msg.Order, subKey);
        }

        private static Fingerprint MakeFingerprint(PlayerOrderMessage msg) => msg.Order switch
        {
            OrderType.EditWaypoint => new Fingerprint { V1 = msg.DestX, V2 = msg.DestZ, V3 = msg.DestY },
            OrderType.MoveTo       => new Fingerprint { V1 = msg.DestX, V2 = msg.DestZ, V3 = msg.DestY },
            OrderType.SensorToggle => new Fingerprint { V1 = msg.Speed },
            _                      => new Fingerprint { V1 = msg.Speed, V2 = msg.Heading },
        };
    }


    // ── PvP: Block AI group-level sensor management on enemy puppets ─────────
    //
    // In PvP the game AI independently manages all units' sensors via
    // DisableAllActiveSensors, OnUpdate→DisableAirSearchRadars, etc.
    // Block these high-level group methods on enemy puppets so only the remote
    // player's network orders can change their sensor state.
    //
    // We patch at the group-level (Enable/DisableAirSearchRadars, etc.) rather than
    // SensorSystem.Enable/Disable because the SensorSystem level also fires during
    // initialization (LoadSensorSystemRadar, SetAdditionalParameters) and from
    // combat damage callbacks — both of which must be allowed through.

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.DisableAllActiveSensors))]
    public static class Patch_DisableAllActiveSensors
    {
        /// <summary>Shared guard: allow sensor changes only for own-side units (or network-applied).</summary>
        internal static bool AllowSensorChange(ObjectBase unit)
        {
            if (OrderHandler.ApplyingFromNetwork) return true;
            if (!Plugin.Instance.CfgPvP.Value && NetworkManager.Instance.IsConnected
                && UnitLockManager.IsLockedByRemote(unit.UniqueID)) return false;
            if (!Plugin.Instance.CfgPvP.Value || !NetworkManager.Instance.IsConnected) return true;
            return unit._taskforce == Globals._playerTaskforce;
        }

        static bool Prefix(ObjectBase __instance) => AllowSensorChange(__instance);
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.DisableAirSearchRadars))]
    public static class Patch_DisableAirSearchRadars
    {
        static bool Prefix(ObjectBase __instance) => Patch_DisableAllActiveSensors.AllowSensorChange(__instance);
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.DisableSurfaceSearchRadars))]
    public static class Patch_DisableSurfaceSearchRadars
    {
        static bool Prefix(ObjectBase __instance) => Patch_DisableAllActiveSensors.AllowSensorChange(__instance);
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.EnableAirSearchRadars))]
    public static class Patch_EnableAirSearchRadars
    {
        static bool Prefix(ObjectBase __instance) => Patch_DisableAllActiveSensors.AllowSensorChange(__instance);
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.EnableSurfaceSearchRadars))]
    public static class Patch_EnableSurfaceSearchRadars
    {
        static bool Prefix(ObjectBase __instance) => Patch_DisableAllActiveSensors.AllowSensorChange(__instance);
    }

    // ── Radar Enable/Disable (catches both context menu and per-sensor UI) ──
    //
    // The player toggles radars via either:
    //  - Formation context menu → EnableAirSearchRadars() → SensorSystem.Enable()
    //  - Per-sensor UI button → ToggleEnableCommand → SensorSystem.Enable()
    // Patching at the SensorSystem level catches both paths.

    [HarmonyPatch(typeof(SensorSystem), nameof(SensorSystem.Enable))]
    public static class Patch_SensorSystem_Enable
    {
        static bool Prefix(SensorSystem __instance)
        {
            if (OrderHandler.ApplyingFromNetwork) return true;
            if (OrderSyncHelper.SuppressSensorPatch) return true;

            var unit = __instance._baseObject;
            if (unit == null) return true;

            int group = OrderSyncHelper.GetRadarGroup(__instance, unit);
            if (group < 0) return true;

            return OrderSyncHelper.Prefix(unit, OrderSyncHelper.SensorMsg(unit, group, true));
        }

        static void Postfix(SensorSystem __instance)
        {
            if (OrderHandler.ApplyingFromNetwork) return;
            if (OrderSyncHelper.SuppressSensorPatch) return;
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;

            var unit = __instance._baseObject;
            if (unit == null) return;

            int group = OrderSyncHelper.GetRadarGroup(__instance, unit);
            if (group < 0) return;

            OrderSyncHelper.Postfix(unit, OrderSyncHelper.SensorMsg(unit, group, true));
        }
    }

    [HarmonyPatch(typeof(SensorSystem), nameof(SensorSystem.Disable))]
    public static class Patch_SensorSystem_Disable
    {
        static bool Prefix(SensorSystem __instance)
        {
            if (OrderHandler.ApplyingFromNetwork) return true;
            if (OrderSyncHelper.SuppressSensorPatch) return true;

            var unit = __instance._baseObject;
            if (unit == null) return true;

            int group = OrderSyncHelper.GetRadarGroup(__instance, unit);
            if (group < 0) return true;

            return OrderSyncHelper.Prefix(unit, OrderSyncHelper.SensorMsg(unit, group, false));
        }

        static void Postfix(SensorSystem __instance)
        {
            if (OrderHandler.ApplyingFromNetwork) return;
            if (OrderSyncHelper.SuppressSensorPatch) return;
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;

            var unit = __instance._baseObject;
            if (unit == null) return;

            int group = OrderSyncHelper.GetRadarGroup(__instance, unit);
            if (group < 0) return;

            OrderSyncHelper.Postfix(unit, OrderSyncHelper.SensorMsg(unit, group, false));
        }
    }

    // ── Active sonar (group 2) — subscription-based ────────────────────────
    //
    // The player toggles active sonar via SensorSystemSonar.ToggleActiveCommand
    // which directly sets _sonar.IsActive.Value, bypassing EnableActiveSonars().
    // We subscribe to IsActive changes after init() to catch ALL paths.

    [HarmonyPatch(typeof(SensorSystemSonar), nameof(SensorSystemSonar.init))]
    public static class Patch_SensorSystemSonar_Init
    {
        static void Postfix(SensorSystemSonar __instance)
        {
            var sonar = __instance._sonar;
            var unit  = __instance._baseObject;
            if (sonar == null || unit == null) return;

            sonar.IsActive.Subscribe(active =>
            {
                if (OrderHandler.ApplyingFromNetwork) return;
                if (unit.UniqueID == 0) return;
                if (SessionManager.SceneLoading) return;

                var msg = OrderSyncHelper.SensorMsg(unit, 2, active);

                if (Plugin.Instance.CfgIsHost.Value)
                {
                    if (NetworkManager.Instance != null && NetworkManager.Instance.IsConnected &&
                        OrderDeduplicator.ShouldSend(msg))
                        NetworkManager.Instance.BroadcastToClients(msg);
                }
                else
                {
                    if (TaskforceAssignmentManager.ClientMayControl(unit) &&
                        NetworkManager.Instance != null &&
                        OrderDeduplicator.ShouldSend(msg))
                        NetworkManager.Instance.SendToServer(msg);
                }
            });
        }
    }

    // ── Submarine mast toggles ──────────────────────────────────────────────
    //
    // Mast toggles internally call SensorSystem.Enable/Disable.
    // SuppressSensorPatch prevents the SensorSystem patches from
    // double-sending — the mast patch handles the sync.

    [HarmonyPatch(typeof(Submarine), nameof(Submarine.toggleSnorkelMast))]
    public static class Patch_ToggleSnorkelMast
    {
        static bool Prefix(Submarine __instance)
        {
            OrderSyncHelper.SuppressSensorPatch = true;
            return OrderSyncHelper.Prefix(__instance, OrderSyncHelper.MastMsg(__instance, 0));
        }
        static void Postfix(Submarine __instance)
        {
            OrderSyncHelper.Postfix(__instance, OrderSyncHelper.MastMsg(__instance, 0));
            OrderSyncHelper.SuppressSensorPatch = false;
        }
    }

    [HarmonyPatch(typeof(Submarine), nameof(Submarine.togglePeriscopeMast))]
    public static class Patch_TogglePeriscopeMast
    {
        static bool Prefix(Submarine __instance)
        {
            OrderSyncHelper.SuppressSensorPatch = true;
            return OrderSyncHelper.Prefix(__instance, OrderSyncHelper.MastMsg(__instance, 1));
        }
        static void Postfix(Submarine __instance)
        {
            OrderSyncHelper.Postfix(__instance, OrderSyncHelper.MastMsg(__instance, 1));
            OrderSyncHelper.SuppressSensorPatch = false;
        }
    }

    [HarmonyPatch(typeof(Submarine), nameof(Submarine.toggleRadarMast))]
    public static class Patch_ToggleRadarMast
    {
        static bool Prefix(Submarine __instance)
        {
            OrderSyncHelper.SuppressSensorPatch = true;
            return OrderSyncHelper.Prefix(__instance, OrderSyncHelper.MastMsg(__instance, 2));
        }
        static void Postfix(Submarine __instance)
        {
            OrderSyncHelper.Postfix(__instance, OrderSyncHelper.MastMsg(__instance, 2));
            OrderSyncHelper.SuppressSensorPatch = false;
        }
    }

    [HarmonyPatch(typeof(Submarine), nameof(Submarine.toggleESMMast))]
    public static class Patch_ToggleESMMast
    {
        static bool Prefix(Submarine __instance)
        {
            OrderSyncHelper.SuppressSensorPatch = true;
            return OrderSyncHelper.Prefix(__instance, OrderSyncHelper.MastMsg(__instance, 3));
        }
        static void Postfix(Submarine __instance)
        {
            OrderSyncHelper.Postfix(__instance, OrderSyncHelper.MastMsg(__instance, 3));
            OrderSyncHelper.SuppressSensorPatch = false;
        }
    }

    // ── PvP death notification clearing ────────────────────────────────
    /// <summary>Clear PvP death notification tracking (call on disconnect/scene change).</summary>
    internal static class PvPDeathNotifications
    {
        internal static void Clear()
        {
            Patch_Missile_OnFixedUpdate_PvP._notifiedDeaths.Clear();
            Patch_Torpedo_OnFixedUpdate_PvP._notifiedDeaths.Clear();
            Patch_Vehicle_UpdateAllData_PvP.ClearCache();
        }
    }

    // ── Host-authoritative combat resolution ─────────────────────────────
    //
    // Combat outcomes (CIWS interception, SAM kills, damage, destruction)
    // are non-deterministic due to RNG divergence between host and client.
    // Host resolves all combat, sends CombatEventMessage to client.
    // Client suppresses local combat resolution and applies host events.

    static class CombatSyncHelper
    {
        private static readonly FieldInfo _launchPlatformField =
            AccessTools.Field(typeof(WeaponBase), "_launchPlatform");

        /// <summary>
        /// PvP: returns true if the target weapon (missile/torpedo) was launched
        /// by the enemy. Used to enforce defender authority — the defender
        /// resolves CIWS and SAM interception against incoming enemy missiles.
        /// Returns false for non-weapons, unknown launchers, or own weapons.
        /// </summary>
        internal static bool IsEnemyOwnedWeapon(ObjectBase target)
        {
            if (!(target is WeaponBase)) return false;
            var launcher = _launchPlatformField?.GetValue(target) as ObjectBase;
            if (launcher == null) return false;
            return launcher._taskforce != Globals._playerTaskforce;
        }

        internal static bool ShouldSuppress()
        {
            if (CombatEventHandler.ApplyingFromNetwork) return false; // allow network events
            if (!NetworkManager.Instance.IsConnected) return false;   // singleplayer

            if (Plugin.Instance.CfgPvP.Value)
                return false; // PvP: both sides resolve combat locally

            // Co-op: only host runs combat
            return !Plugin.Instance.CfgIsHost.Value;
        }

        internal static bool ShouldBroadcast()
        {
            if (!NetworkManager.Instance.IsConnected) return false;
            if (CombatEventHandler.ApplyingFromNetwork) return false;

            if (Plugin.Instance.CfgPvP.Value)
                return true; // PvP: both sides broadcast combat events

            // Co-op: only host broadcasts
            return Plugin.Instance.CfgIsHost.Value;
        }

        internal static void Send(CombatEventMessage msg)
        {
            NetworkManager.Instance.SendToOther(msg, DeliveryMethod.ReliableOrdered);
        }

        internal static void SendDamageState(DamageStateMessage msg)
        {
            NetworkManager.Instance.SendToOther(msg, DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>
    /// CIWS interception — defender is authoritative.
    /// If enemy missile is targeted by our CIWS, we resolve interception locally
    /// and broadcast the result. If our own missile is targeted by enemy CIWS,
    /// we suppress — the enemy (defender) decides the outcome.
    /// </summary>
    [HarmonyPatch(typeof(WeaponSystemCIWS), nameof(WeaponSystemCIWS.InterceptAirTarget))]
    public static class Patch_CIWS_InterceptAirTarget
    {
        static bool Prefix(WeaponSystemCIWS __instance, ObjectBase target)
        {
            // PvP: defender is authoritative for interception outcomes
            if (Plugin.Instance.CfgPvP.Value && NetworkManager.Instance.IsConnected
                && !CombatEventHandler.ApplyingFromNetwork)
            {
                if (!CombatSyncHelper.IsEnemyOwnedWeapon(target))
                    return false; // our missile — enemy defender is authoritative
                return true;     // enemy missile — we are the defender, we decide
            }

            // Co-op: only host runs combat
            if (CombatSyncHelper.ShouldSuppress())
            {
                Plugin.Log.LogInfo($"[Combat] Suppressed CIWS intercept of {target.UniqueID} on client");
                return false;
            }
            return true;
        }

        static void Postfix(WeaponSystemCIWS __instance, ObjectBase target)
        {
            if (!CombatSyncHelper.ShouldBroadcast()) return;
            // Only broadcast if the interception actually succeeded
            if (!target.IsDestroyed && !target._externalDestructionNotified) return;
            Plugin.Log.LogInfo($"[Combat] CIWS intercept: target={target.UniqueID} by={__instance._baseObject.UniqueID}");
            CombatSyncHelper.Send(new CombatEventMessage
            {
                EventType      = CombatEventType.ProjectileIntercepted,
                TargetEntityId = ProjectileIdMapper.TranslateForRemote(target.UniqueID),
                SourceEntityId = __instance._baseObject.UniqueID,
            });
        }
    }

    /// <summary>
    /// Weapon-vs-weapon interception (SAM blastzone hits incoming missile).
    /// Defender is authoritative — if our SAM hits an enemy missile, we resolve
    /// the interception and broadcast. If our missile is hit by enemy SAM,
    /// we suppress — the enemy (defender) decides.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_Blastzone_OnHitWeapon
    {
        private static readonly HashSet<int> _sentInterceptions = new();
        internal static void ClearInterceptions() => _sentInterceptions.Clear();

        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(Blastzone), "OnHitWeapon");

        static bool Prefix(WeaponBase hitObject, WeaponBase ____weapon)
        {
            // PvP: defender is authoritative for interception outcomes
            if (Plugin.Instance.CfgPvP.Value && NetworkManager.Instance.IsConnected
                && !CombatEventHandler.ApplyingFromNetwork)
            {
                if (!CombatSyncHelper.IsEnemyOwnedWeapon(hitObject))
                    return false; // our missile — enemy defender is authoritative
                return true;     // enemy missile — we are the defender, we decide
            }

            // Co-op: only host runs combat
            if (CombatSyncHelper.ShouldSuppress())
            {
                Plugin.Log.LogInfo($"[Combat] Suppressed OnHitWeapon for {hitObject.UniqueID} on client");
                return false;
            }
            return true;
        }

        static void Postfix(WeaponBase hitObject, WeaponBase ____weapon)
        {
            if (!CombatSyncHelper.ShouldBroadcast()) return;
            if (!hitObject._externalDestructionNotified) return; // interception RNG failed
            if (!_sentInterceptions.Add(hitObject.UniqueID)) return; // dedup

            int sourceId = ____weapon != null ? ProjectileIdMapper.TranslateForRemote(____weapon.UniqueID) : 0;
            Plugin.Log.LogInfo($"[Combat] SAM intercept: target={hitObject.UniqueID} by={sourceId}");
            CombatSyncHelper.Send(new CombatEventMessage
            {
                EventType      = CombatEventType.ProjectileIntercepted,
                TargetEntityId = ProjectileIdMapper.TranslateForRemote(hitObject.UniqueID),
                SourceEntityId = sourceId,
            });
        }
    }

    /// <summary>
    /// Damage to units — PvP: both sides resolve combat and send events.
    /// Co-op: client suppresses, host broadcasts.
    /// Detects both immediate destruction (IsDestroyed) and deferred destruction
    /// (_externalDestructionNotified — used by aircraft/helicopters which die
    /// on their next OnFixedUpdate, not immediately in OnHitUnit).
    /// </summary>
    [HarmonyPatch]
    public static class Patch_Blastzone_OnHitUnit
    {
        // Dedup: only send one MissileImpact per weapon (OnHitUnit fires every frame while overlapping)
        private static readonly HashSet<int> _sentMissileImpacts = new();
        internal static void ClearMissileImpacts() => _sentMissileImpacts.Clear();

        private static int _suppressedPvpHitCount;
        private static float _lastSuppressedPvpHitLogTime;
        private static int _suppressedHitCount;
        private static float _lastSuppressedHitLogTime;

        private static readonly FieldInfo _blastzoneWeaponField =
            AccessTools.Field(typeof(Blastzone), "_weapon");
        private static readonly FieldInfo _launchPlatformField =
            AccessTools.Field(typeof(WeaponBase), "_launchPlatform");

        /// <summary>Grace period (seconds) preventing projectiles from hitting their own launcher.</summary>
        private const float SelfHitGracePeriod = 10f;

        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(Blastzone), "OnHitUnit");

        static bool Prefix(ObjectBase hitObject, WeaponBase ____weapon, out bool __state)
        {
            // Capture pre-hit kill state: destroyed OR marked for deferred destruction
            __state = hitObject.IsDestroyed || hitObject._externalDestructionNotified;

            // Grace period: prevent projectiles from damaging their own launch platform
            if (____weapon != null)
            {
                var launcher = _launchPlatformField?.GetValue(____weapon) as ObjectBase;
                if (launcher != null && launcher.UniqueID == hitObject.UniqueID
                    && Patch_WeaponBase_CommonLaunchSettings.ProjectileSpawnTimes.TryGetValue(
                        ____weapon.UniqueID, out float spawnTime)
                    && Time.time - spawnTime < SelfHitGracePeriod)
                {
                    return false;
                }
            }

            // PvP: only the target owner resolves damage (defensive authority)
            if (Plugin.Instance.CfgPvP.Value && NetworkManager.Instance.IsConnected
                && !CombatEventHandler.ApplyingFromNetwork)
            {
                if (hitObject._taskforce != SeaPower.Globals._playerTaskforce)
                {
                    _suppressedPvpHitCount++;
                    if (Time.unscaledTime - _lastSuppressedPvpHitLogTime > 10f)
                    {
                        Plugin.Log.LogInfo($"[Combat] PvP: Suppressed {_suppressedPvpHitCount} OnHitUnit for enemy units");
                        _suppressedPvpHitCount = 0;
                        _lastSuppressedPvpHitLogTime = Time.unscaledTime;
                    }
                    return false;
                }
                return true;
            }

            // Co-op: only host runs combat
            if (CombatSyncHelper.ShouldSuppress())
            {
                _suppressedHitCount++;
                if (Time.unscaledTime - _lastSuppressedHitLogTime > 10f)
                {
                    Plugin.Log.LogInfo($"[Combat] Suppressed {_suppressedHitCount} OnHitUnit calls on client");
                    _suppressedHitCount = 0;
                    _lastSuppressedHitLogTime = Time.unscaledTime;
                }
                return false;
            }
            return true;
        }

        static void Postfix(ObjectBase hitObject, bool __state, WeaponBase ____weapon)
        {
            if (!CombatSyncHelper.ShouldBroadcast()) return;

            // Detect kill: immediate (IsDestroyed) or deferred (_externalDestructionNotified
            // — aircraft set this flag and actually die on next OnFixedUpdate)
            bool killedNow = hitObject.IsDestroyed || hitObject._externalDestructionNotified;
            if (!__state && killedNow)
            {
                int sourceId = ____weapon != null ? ProjectileIdMapper.TranslateForRemote(____weapon.UniqueID) : 0;
                CombatSyncHelper.Send(new CombatEventMessage
                {
                    EventType      = CombatEventType.UnitDestroyed,
                    TargetEntityId = hitObject.UniqueID,
                    SourceEntityId = sourceId,
                });
                Plugin.Log.LogInfo($"[Combat] Unit killed: target={hitObject.UniqueID} by={sourceId} " +
                    $"(IsDestroyed={hitObject.IsDestroyed} extNotified={hitObject._externalDestructionNotified})");
                return; // no point sending damage state for a destroyed unit
            }

            // Damage state snapshot (compartments + system integrity)
            var dmgMsg = DamageStateSerializer.Capture(hitObject);
            if (dmgMsg != null)
                CombatSyncHelper.SendDamageState(dmgMsg);

            // PvP: notify missile owner to destroy their authoritative copy (once per missile)
            if (Plugin.Instance.CfgPvP.Value && ____weapon != null
                && ____weapon._taskforce != SeaPower.Globals._playerTaskforce
                && _sentMissileImpacts.Add(____weapon.UniqueID))
            {
                int remoteWeaponId = ProjectileIdMapper.TranslateForRemote(____weapon.UniqueID);
                CombatSyncHelper.Send(new CombatEventMessage
                {
                    EventType      = CombatEventType.MissileImpact,
                    TargetEntityId = remoteWeaponId,
                    SourceEntityId = hitObject.UniqueID,
                });
                Plugin.Log.LogInfo($"[Combat] PvP: Sent MissileImpact for enemy missile {____weapon.UniqueID} (remote={remoteWeaponId}) hitting unit {hitObject.UniqueID}");
                // Monitor hit unit for deferred death (compartment integrity may not
                // kill it immediately; WatchForDeath polls IsDestroyed each frame)
                CombatEventHandler.WatchForDeath(hitObject.UniqueID);
            }
        }
    }

    // NOTE: Patch_NotifyOfExternalDestruction was previously a Prefix that BLOCKED the call —
    // that blocked legitimate compartment-death on client. The Postfix below only observes.
    //
    // In PvP mode the CLIENT is authoritative for damage to its own units. When our unit
    // dies via the compartment-integrity path (Compartments.OnFixedUpdate → notifyOfExternalDestruction),
    // OnHitUnit's Postfix is NOT re-invoked, so no UnitDestroyed was ever sent to the host.
    // This Postfix closes that gap by broadcasting our unit's death whenever it dies locally.
    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.notifyOfExternalDestruction))]
    public static class Patch_ObjectBase_NotifyDestroyed_PvP
    {
        // Dedup: one broadcast per unit death (notifyOfExternalDestruction can fire multiple times)
        private static readonly HashSet<int> _sent = new();
        internal static void Clear() => _sent.Clear();

        static void Postfix(ObjectBase __instance)
        {
            if (!CombatSyncHelper.ShouldBroadcast()) return;
            if (SessionManager.SceneLoading) return;
            if (__instance is WeaponBase) return; // missiles handled by Patch_Missile_OnFixedUpdate_PvP with ProjectileDestroyed (3s delay)
            if (__instance._taskforce != Globals._playerTaskforce) return;
            if (!_sent.Add(__instance.UniqueID)) return; // already sent for this unit

            CombatSyncHelper.Send(new CombatEventMessage
            {
                EventType      = CombatEventType.UnitDestroyed,
                TargetEntityId = __instance.UniqueID,
                SourceEntityId = 0,
            });
            Plugin.Log.LogInfo($"[Combat] PvP: sent UnitDestroyed for own unit {__instance.UniqueID} ({__instance.name}) via notifyOfExternalDestruction");
        }
    }

    // ── Damage decal replication ────────────────────────────────────────────
    //
    // OnHitUnit is suppressed on the client, so no combat decals are created.
    // Intercept DecalsManager.createDecalFromClass on the host — when a decal
    // is parented to a unit (ship/sub), capture position/normal/class and
    // send to client for recreation.

    [HarmonyPatch]
    public static class Patch_DecalsManager_CreateDecalFromClass
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(DecalsManager), "createDecalFromClass",
                new[] { typeof(string), typeof(Vector3), typeof(Vector3),
                        typeof(float), typeof(Transform), typeof(bool) });

        static void Postfix(string decalClass, Vector3 position, Vector3 normal,
                            float scale, Transform parent)
        {
            if (!CombatSyncHelper.ShouldBroadcast()) return;
            if (parent == null) return;

            var unit = parent.GetComponent<ObjectBase>();
            if (unit == null) return;

            var localPos  = parent.InverseTransformPoint(position);
            var localNorm = parent.InverseTransformDirection(normal);

            var msg = new DamageDecalMessage
            {
                TargetEntityId = unit.UniqueID,
                LocalX  = localPos.x,  LocalY  = localPos.y,  LocalZ  = localPos.z,
                NormalX = localNorm.x, NormalY = localNorm.y, NormalZ = localNorm.z,
                DecalClass = decalClass,
                Scale = scale,
            };
            NetworkManager.Instance.SendToOther(msg, DeliveryMethod.ReliableOrdered);
        }
    }

    // ── Projectile spawn ID sync ──────────────────────────────────────────
    //
    // Fired immediately when any projectile (missile, torpedo, gun shell, etc.)
    // spawns via WeaponBase.CommonLaunchSettings — the final init step.
    // Host broadcasts {hostProjectileId, sourceUnitId} so the client can match
    // its local projectile by source unit in FIFO order instead of fragile
    // proximity matching.

    [HarmonyPatch(typeof(WeaponBase), nameof(WeaponBase.CommonLaunchSettings))]
    public static class Patch_WeaponBase_CommonLaunchSettings
    {
        private static readonly System.Reflection.FieldInfo _launchPlatformField =
            AccessTools.Field(typeof(WeaponBase), "_launchPlatform");

        /// <summary>
        /// Tracks projectile spawn times so OnHitUnit can enforce a grace period
        /// preventing projectiles from damaging their own launch platform.
        /// </summary>
        internal static readonly Dictionary<int, float> ProjectileSpawnTimes = new();
        internal static void ClearSpawnTimes() => ProjectileSpawnTimes.Clear();

        static void Postfix(WeaponBase __instance, ObjectBase targetObject, Vector3 targetPosition)
        {
            // Record spawn time for self-hit grace period (always, even if not connected)
            ProjectileSpawnTimes[__instance.UniqueID] = Time.time;

            if (!NetworkManager.Instance.IsConnected) return;
            if (SessionManager.SceneLoading) return;

            // Skip projectile tracking for chaff and countermeasures.
            // These spawn in bursts, don't align between host/client,
            // and have no gameplay sync value.
            var ammoType = __instance._ap?._type ?? Ammunition.Type.Unknown;
            if (ammoType == Ammunition.Type.Chaff || ammoType == Ammunition.Type.Noisemaker)
                return;

            int projectileId = __instance.UniqueID;
            var launcher = _launchPlatformField.GetValue(__instance) as ObjectBase;
            int sourceUnitId = launcher?.UniqueID ?? 0;

            if (Plugin.Instance.CfgVerboseDebug.Value)
                Plugin.Log.LogDebug($"[AutoFire DIAG] t={AIAutoFireState.DiagMs}ms PROJECTILE_SPAWN " +
                    $"projId={projectileId} projName={__instance.name} " +
                    $"launcherId={sourceUnitId} launcherName={launcher?.name ?? "?"} " +
                    $"isHost={Plugin.Instance.CfgIsHost.Value} " +
                    $"projType={__instance.GetType().Name}");

            bool isPvP = Plugin.Instance.CfgPvP.Value;

            string ammoName = __instance.name;

            if (isPvP)
            {
                if (launcher != null && launcher._taskforce != Globals._playerTaskforce)
                {
                    // Enemy ship launched a weapon — check authorization
                    if (!PvPFireAuth.ConsumeAuth(sourceUnitId))
                    {
                        // Unauthorized: local AI fired without network order — destroy
                        __instance._externalDestructionNotified = true;
                        PvPFireAuth.RecordSuppression();
                        Plugin.Log.LogWarning(
                            $"[Spawn] PvP: Suppressed unauthorized enemy launch: id={projectileId} source={sourceUnitId}");
                        return;
                    }

                    // FIFO ID matching per (sourceUnit, ammoName)
                    ProjectileIdMapper.OnLocalSpawn(projectileId, sourceUnitId, ammoName);
                    Plugin.Log.LogInfo($"[Spawn] PvP authorized enemy projectile (FIFO): id={projectileId} source={sourceUnitId} ammo={ammoName}");
                }
                else if (launcher != null)
                {
                    // Own projectile — send spawn ID + ammo name + target to other player.
                    // The receiver uses target info so TryForceSpawn fires at the correct target.
                    // Coordinates are geo (floating-origin safe).
                    var geo = Utils.worldPositionFromUnityToLongLat(targetPosition, Globals._currentCenterTile);
                    var fwd = __instance.transform.forward;
                    var spawnMsg = new ProjectileSpawnMessage
                    {
                        HostProjectileId = projectileId,
                        SourceUnitId     = sourceUnitId,
                        AmmoName         = ammoName,
                        TargetEntityId   = targetObject != null
                                             ? ProjectileIdMapper.TranslateForRemote(targetObject.UniqueID)
                                             : 0,
                        TargetX          = (float)geo._longitude,
                        TargetY          = (float)geo._height,
                        TargetZ          = (float)geo._latitude,
                        LaunchDirX       = fwd.x,
                        LaunchDirY       = fwd.y,
                        LaunchDirZ       = fwd.z,
                    };
                    NetworkManager.Instance.SendToOther(spawnMsg, DeliveryMethod.ReliableOrdered);
                    Plugin.Log.LogInfo($"[Spawn] PvP own projectile: id={projectileId} source={sourceUnitId} ammo={ammoName}");
                }
            }
            else if (Plugin.Instance.CfgIsHost.Value)
            {
                // Co-op host: broadcast spawn ID + target to client
                var msg = new ProjectileSpawnMessage
                {
                    HostProjectileId = projectileId,
                    SourceUnitId     = sourceUnitId,
                    AmmoName         = ammoName,
                    TargetEntityId   = targetObject?.UniqueID ?? 0,
                    TargetX          = targetPosition.x,
                    TargetY          = targetPosition.y,
                    TargetZ          = targetPosition.z,
                };
                NetworkManager.Instance.BroadcastToClients(msg, DeliveryMethod.ReliableOrdered);
                Plugin.Log.LogInfo($"[Spawn] Host projectile: id={projectileId} source={sourceUnitId} ammo={ammoName}");
            }
            else
            {
                // Co-op client: register local spawn for matching with host message
                ProjectileIdMapper.OnLocalSpawn(projectileId, sourceUnitId, ammoName);
                Plugin.Log.LogInfo($"[Spawn] Client projectile: id={projectileId} source={sourceUnitId} ammo={ammoName}");
            }
        }
    }

    // ── Manual chaff deployment sync (PvP) ────────────────────────────────
    //
    // Shift+C → ObjectBase.LaunchChaff(true) — sync to other player.
    // Auto-chaff (WeaponSystemChaff.OnUpdate) calls launchChaff() directly,
    // bypassing ObjectBase.LaunchChaff, so it runs independently on both sides.

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.LaunchChaff))]
    public static class Patch_ObjectBase_LaunchChaff
    {
        internal static bool ApplyingFromNetwork;
        private static bool _suppressPostfix;

        static bool Prefix(ObjectBase __instance)
        {
            if (ApplyingFromNetwork) return true;
            if (!Plugin.Instance.CfgPvP.Value) return true;
            if (!NetworkManager.Instance.IsConnected) return true;
            if (__instance._taskforce != Globals._playerTaskforce) return true;

            // Send immediately, delay local execution
            var msg = new ChaffLaunchMessage { UnitId = __instance.UniqueID };
            NetworkManager.Instance.SendToOther(msg, DeliveryMethod.ReliableOrdered);
            Plugin.Log.LogInfo($"[Chaff] Sent chaff launch for unit {__instance.UniqueID}");

            var unit = __instance;
            OrderDelayQueue.Enqueue(() =>
            {
                if (unit == null || unit.IsDestroyed) return;
                ApplyingFromNetwork = true;
                try { unit.LaunchChaff(false); }
                finally { ApplyingFromNetwork = false; }
            });

            _suppressPostfix = true;
            return false;
        }

        static void Postfix(ObjectBase __instance)
        {
            if (_suppressPostfix) { _suppressPostfix = false; return; }
            if (ApplyingFromNetwork) return;
            if (!Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (__instance._taskforce != Globals._playerTaskforce) return;

            var msg = new ChaffLaunchMessage { UnitId = __instance.UniqueID };
            NetworkManager.Instance.SendToOther(msg, DeliveryMethod.ReliableOrdered);
            Plugin.Log.LogInfo($"[Chaff] Sent chaff launch for unit {__instance.UniqueID}");
        }

        public static void ApplyFromNetwork(ChaffLaunchMessage msg)
        {
            if (SessionManager.SceneLoading || SimSyncManager.CurrentState != SimState.Synchronized)
                return;

            var unit = StateSerializer.FindById(msg.UnitId);
            if (unit == null || unit.IsDestroyed) return;

            ApplyingFromNetwork = true;
            try
            {
                unit.LaunchChaff(false); // false = no voice message
            }
            finally
            {
                ApplyingFromNetwork = false;
            }
            Plugin.Log.LogInfo($"[Chaff] Applied remote chaff launch for unit {msg.UnitId}");
        }
    }

    // ── PvP: fix map colors and formation markers ────────────────────────
    //
    // After side swap, the ECS DetectedSide component still references the
    // pre-swap taskforce entities. Vehicle.UpdateFromECS() reads UnitTaskforce
    // from DetectedSide, causing inverted map colors (player ships = red,
    // enemy ships = blue) and enemy formation markers appearing.
    //
    // Fix: override UnitTaskforce with the object's actual _taskforce.
    //
    // IMPORTANT: We must NOT simply set UnitTaskforce.Value in the Postfix —
    // that causes UnitTaskforce to oscillate every frame between the wrong ECS
    // value and our correction. Each change fires the Taskforce subscription
    // that queues "track identified as hostile" voice callouts, producing
    // endless repeated callout spam.
    //
    // Instead: Prefix pre-sets the backing field to the wrong (ECS) value
    // that UpdateFromECS will write, making its assignment a no-op (same
    // value → no subscription fire). Postfix then silently corrects via
    // backing field reflection (also no subscription fire). Net result:
    // subscription fires at most once per contact (initial classification).

    [HarmonyPatch(typeof(Vehicle), "UpdateFromECS")]
    public static class Patch_Vehicle_UpdateAllData_PvP
    {
        // Cache: what UpdateFromECS sets UnitTaskforce to (the wrong ECS value)
        private static readonly Dictionary<Vehicle, Taskforce> _ecsTaskforce = new();

        // ReactiveProperty<Taskforce> backing field — set directly to bypass subscriptions
        private static readonly FieldInfo RpValueField =
            AccessTools.Field(typeof(ReactiveProperty<Taskforce>), "value");

        internal static void ClearCache() => _ecsTaskforce.Clear();

        static void Prefix(Vehicle __instance)
        {
            if (!Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (RpValueField == null) return;

            // Pre-set backing field to what UpdateFromECS will write.
            // This makes the base method's UnitTaskforce.Value = wrongTF
            // a no-op (wrongTF == wrongTF → ReactiveProperty skips subscription).
            if (_ecsTaskforce.TryGetValue(__instance, out var cachedWrongTf))
                RpValueField.SetValue(__instance.UnitTaskforce, cachedWrongTf);
        }

        static void Postfix(Vehicle __instance)
        {
            if (!Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (__instance.Object == null || __instance.Object._taskforce == null) return;
            if (RpValueField == null) return;

            var actualTf = __instance.Object._taskforce;

            // First detection: UpdateFromECS fired the subscription with the wrong
            // taskforce and we have no cached value to suppress it. Correct via
            // Value setter so the UI gets a second notification with the RIGHT value.
            if (!_ecsTaskforce.ContainsKey(__instance))
            {
                _ecsTaskforce[__instance] = __instance.UnitTaskforce.Value;
                __instance.UnitTaskforce.Value = actualTf;
                return;
            }

            // Subsequent frames: cache and silently correct via backing field
            // (Prefix already suppressed the subscription, no UI spam)
            _ecsTaskforce[__instance] = __instance.UnitTaskforce.Value;
            RpValueField.SetValue(__instance.UnitTaskforce, actualTf);
        }
    }

    // ── PvP: hide enemy formation markers on tactical map ──────────────────
    //
    // After side swap, enemy units can end up in the Formations collection
    // (due to stale ECS DetectedSide taskforce references). Even with the
    // UpdateFromECS correction, the delegate-based ObservableComputations
    // filter doesn't re-evaluate. Instead of fighting the filter, directly
    // hide enemy MapFormationViewModels by overriding their display properties.
    //
    // UnitFormation._taskforce identifies which side owns the formation.

    internal static class FormationHelper
    {
        internal static bool IsEnemyFormation(UnitFormation formation)
        {
            if (!Plugin.Instance.CfgPvP.Value) return false;
            if (!NetworkManager.Instance.IsConnected) return false;
            return formation?._taskforce != null
                && formation._taskforce != Globals._playerTaskforce;
        }
    }

    [HarmonyPatch(typeof(MapFormationViewModel), nameof(MapFormationViewModel.FormationInfoLine1), MethodType.Getter)]
    public static class Patch_MapFormationViewModel_InfoLine_PvP
    {
        static void Postfix(MapFormationViewModel __instance, ref string __result)
        {
            if (FormationHelper.IsEnemyFormation(__instance.Formation))
                __result = "";
        }
    }

    [HarmonyPatch(typeof(MapFormationViewModel), nameof(MapFormationViewModel.IsValid), MethodType.Getter)]
    public static class Patch_MapFormationViewModel_IsValid_PvP
    {
        static void Postfix(MapFormationViewModel __instance, ref bool __result)
        {
            if (FormationHelper.IsEnemyFormation(__instance.Formation))
                __result = false;
        }
    }

    [HarmonyPatch(typeof(MapFormationViewModel), nameof(MapFormationViewModel.Longitude), MethodType.Getter)]
    public static class Patch_MapFormationViewModel_Longitude_PvP
    {
        static void Postfix(MapFormationViewModel __instance, ref double __result)
        {
            if (FormationHelper.IsEnemyFormation(__instance.Formation))
                __result = double.NaN;
        }
    }

    [HarmonyPatch(typeof(MapFormationViewModel), nameof(MapFormationViewModel.Latitude), MethodType.Getter)]
    public static class Patch_MapFormationViewModel_Latitude_PvP
    {
        static void Postfix(MapFormationViewModel __instance, ref double __result)
        {
            if (FormationHelper.IsEnemyFormation(__instance.Formation))
                __result = double.NaN;
        }
    }

    // UnitMembershipViewModel constructor patch removed:
    // Clearing ConnectionToFormation caused ArgumentOutOfRangeException in
    // PositionChanged() (called every frame via position subscription).
    // The formation is already hidden via IsValid=false and Lat/Lng=NaN,
    // so connection lines don't render even without clearing the collection.

    // ═══════════════════════════════════════════════════════════════════════
    // PvP: Missile/Torpedo death notification (Postfix-only, no suppression)
    // Both sides simulate all units. These Postfixes notify the remote side
    // when our own missiles/torpedoes die (fuel, guidance, self-destruct).
    // ═══════════════════════════════════════════════════════════════════════

    [HarmonyPatch(typeof(Missile), "OnFixedUpdate")]
    public static class Patch_Missile_OnFixedUpdate_PvP
    {
        private static readonly FieldInfo _launchPlatformField =
            AccessTools.Field(typeof(WeaponBase), "_launchPlatform");
        internal static readonly HashSet<int> _notifiedDeaths = new();

        // Jamming state save/restore for non-owned missiles (attacker authority)
        [System.ThreadStatic] private static bool _restoreJam;
        [System.ThreadStatic] private static bool _savedJammed;
        [System.ThreadStatic] private static float _savedDeviationMag;

        static void Prefix(Missile __instance)
        {
            _restoreJam = false;
            if (!Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;

            var launcher = _launchPlatformField?.GetValue(__instance) as ObjectBase;
            if (launcher == null || launcher._taskforce == Globals._playerTaskforce) return;

            // Non-owned missile: preserve synced jamming state so local ECM/chaff
            // computation doesn't overwrite the owner's authoritative values
            _restoreJam = true;
            _savedJammed = __instance._jammed;
            _savedDeviationMag = __instance._deviationMagnitudeWithJam;
        }

        static void Postfix(Missile __instance)
        {
            // Restore synced jamming state for non-owned missiles
            if (_restoreJam)
            {
                __instance._jammed = _savedJammed;
                __instance._deviationMagnitudeWithJam = _savedDeviationMag;
                _restoreJam = false;
            }

            // Death notification for owned missiles
            if (!Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (!__instance.IsDestroyed && !__instance._externalDestructionNotified) return;

            int id = __instance.UniqueID;
            if (!_notifiedDeaths.Add(id)) return;

            var launcher = _launchPlatformField?.GetValue(__instance) as ObjectBase;
            if (launcher == null || launcher._taskforce != Globals._playerTaskforce) return;

            CombatSyncHelper.Send(new CombatEventMessage
            {
                EventType      = CombatEventType.ProjectileDestroyed,
                TargetEntityId = ProjectileIdMapper.TranslateForRemote(id),
                SourceEntityId = 0,
            });
            Plugin.Log.LogDebug($"[Combat] PvP missile death notification: id={id}");
        }
    }

    [HarmonyPatch(typeof(Torpedo), "OnFixedUpdate")]
    public static class Patch_Torpedo_OnFixedUpdate_PvP
    {
        private static readonly FieldInfo _launchPlatformField =
            AccessTools.Field(typeof(WeaponBase), "_launchPlatform");
        internal static readonly HashSet<int> _notifiedDeaths = new();

        static void Postfix(Torpedo __instance)
        {
            if (!Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (!__instance.IsDestroyed && !__instance._externalDestructionNotified) return;

            int id = __instance.UniqueID;
            if (!_notifiedDeaths.Add(id)) return;

            var launcher = _launchPlatformField?.GetValue(__instance) as ObjectBase;
            if (launcher == null || launcher._taskforce != Globals._playerTaskforce) return;

            CombatSyncHelper.Send(new CombatEventMessage
            {
                EventType      = CombatEventType.ProjectileDestroyed,
                TargetEntityId = ProjectileIdMapper.TranslateForRemote(id),
                SourceEntityId = 0,
            });
            Plugin.Log.LogDebug($"[Combat] PvP torpedo death notification: id={id}");
        }
    }

    // ── Unit Selection Lock (Co-op) ──────────────────────────────────────────

    [HarmonyPatch(typeof(RenderPosition), nameof(RenderPosition.switchToObject))]
    public static class Patch_RenderPosition_SwitchToObject
    {
        static bool Prefix(ObjectBase objectToAttach)
        {
            // Only active in co-op when connected
            if (Plugin.Instance.CfgPvP.Value) return true;
            if (!NetworkManager.Instance.IsConnected) return true;
            if (objectToAttach == null) return true;

            // Block selection if the remote player has this unit selected
            if (UnitLockManager.IsLockedByRemote(objectToAttach.UniqueID))
            {
                Plugin.Log.LogDebug(
                    $"[UnitLock] Selection of unit {objectToAttach.UniqueID} blocked — held by remote player.");
                UnitLockManager.NotifySelectionBlocked();
                return false; // prevent switchToObject from running
            }

            return true;
        }

        static void Postfix(ObjectBase objectToAttach)
        {
            // Only broadcast in co-op when connected
            if (Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (objectToAttach == null) return;

            // Verify the selection actually took effect
            var current = Singleton<RenderPosition>.Instance.SelectedObject;
            if (current == null || current.UniqueID != objectToAttach.UniqueID) return;

            // Broadcast our selection to the remote player
            NetworkManager.Instance.SendToOther(new GameEventMessage
            {
                EventType = GameEventType.UnitSelected,
                Param     = (float)objectToAttach.UniqueID,
            });
        }
    }

    [HarmonyPatch(typeof(RenderPosition), nameof(RenderPosition.deselectObjectAndDetachCamera))]
    public static class Patch_RenderPosition_DeselectObjectAndDetachCamera
    {
        static void Prefix()
        {
            // Only broadcast in co-op when connected
            if (Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;

            // Read SelectedObject BEFORE deselection clears it
            var current = Singleton<RenderPosition>.Instance.SelectedObject;
            if (current == null) return;

            NetworkManager.Instance.SendToOther(new GameEventMessage
            {
                EventType = GameEventType.UnitDeselected,
                Param     = (float)current.UniqueID,
            });
        }
    }

    // ── MapUnitViewModel registry for unit lock map indicator ────────────────

    [HarmonyPatch(typeof(MapUnitViewModel), MethodType.Constructor,
        new[] { typeof(Taskforce), typeof(Vehicle), typeof(ReactiveProperty<ISelectableObject>), typeof(bool) })]
    public static class Patch_MapUnitViewModel_Ctor
    {
        static void Postfix(MapUnitViewModel __instance)
        {
            MapUnitViewModelRegistry.Register(__instance);
        }
    }

    [HarmonyPatch(typeof(MapUnitViewModel), nameof(MapUnitViewModel.Dispose))]
    public static class Patch_MapUnitViewModel_Dispose
    {
        static void Prefix(MapUnitViewModel __instance)
        {
            MapUnitViewModelRegistry.Unregister(__instance);
        }
    }

    [HarmonyPatch(typeof(MapUnitViewModel), "get_ContactInfoLine2")]
    public static class Patch_MapUnitViewModel_ContactInfoLine2
    {
        static void Postfix(MapUnitViewModel __instance, ref string __result)
        {
            if (Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            var obj = __instance.Unit?.BaseObject as ObjectBase;
            if (obj == null) return;
            // Only friendly units are co-controlled — enemy units can't be "locked by the other player"
            if (obj._taskforce != Globals._playerTaskforce) return;
            if (UnitLockManager.IsLockedByRemote(obj.UniqueID))
                __result = "[BUSY]";
        }
    }

    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.setPresetHeight))]
    public static class Patch_Aircraft_SetPresetHeight
    {
        static void Postfix(Aircraft __instance, int preset, bool updateAltForWaypoints)
        {
            if (OrderHandler.ApplyingFromNetwork) return;
            if (SessionManager.SceneLoading) return;

            var msg = new PlayerOrderMessage
            {
                SourceEntityId = __instance.UniqueID,
                Order          = OrderType.SetAltitude,
                Speed          = (float)preset,
                Heading        = updateAltForWaypoints ? 1f : 0f,
            };

            OrderSyncHelper.Postfix(__instance, msg);
        }
    }

    [HarmonyPatch(typeof(Helicopter), nameof(Helicopter.setPresetHeight))]
    public static class Patch_Helicopter_SetPresetHeight
    {
        static void Postfix(Helicopter __instance, int preset, bool updateAltForWaypoints)
        {
            if (OrderHandler.ApplyingFromNetwork) return;
            if (SessionManager.SceneLoading) return;

            var msg = new PlayerOrderMessage
            {
                SourceEntityId = __instance.UniqueID,
                Order          = OrderType.SetAltitude,
                Speed          = (float)preset,
                Heading        = updateAltForWaypoints ? 1f : 0f,
            };

            OrderSyncHelper.Postfix(__instance, msg);
        }
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.setOrder),
        new[] { typeof(Order.Type), typeof(ObjectBase), typeof(bool) })]
    public static class Patch_ObjectBase_SetOrder_RTB
    {
        static void Postfix(ObjectBase __instance, Order.Type type, ObjectBase targetObject, bool displayOrderText)
        {
            if (OrderHandler.ApplyingFromNetwork) return;
            if (SessionManager.SceneLoading) return;
            if (type != Order.Type.ReturnToBase) return;

            var msg = new PlayerOrderMessage
            {
                SourceEntityId = __instance.UniqueID,
                Order          = OrderType.ReturnToBase,
                TargetEntityId = targetObject?.UniqueID ?? 0,
            };

            OrderSyncHelper.Postfix(__instance, msg);
        }
    }

    [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.OverrideRelationship))]
    public static class Patch_Vehicle_OverrideRelationship
    {
        static void Postfix(Vehicle __instance, RelationsState forcedState)
        {
            if (OrderHandler.ApplyingFromNetwork) return;
            if (SessionManager.SceneLoading) return;

            ObjectBase baseObj = __instance.BaseObject;
            if (baseObj == null) return;

            var msg = new PlayerOrderMessage
            {
                SourceEntityId = baseObj.UniqueID,
                Order          = OrderType.ClassifyContact,
                Speed          = (float)forcedState,
            };

            OrderSyncHelper.Postfix(baseObj, msg);
        }
    }

    /// <summary>
    /// Fix #53: Correct priority inversion in Vehicle.CurrentRelationship().
    /// The original method checks UnitTaskforce (auto-detection) BEFORE ForcedRelationState
    /// (manual classification), meaning manual classifications are always shadowed.
    /// This prefix checks ForcedRelationState first, returning it if present.
    ///
    /// Uses reflection to access Unity.Entities types (EntityManager, Entity,
    /// ForcedRelationState) because the mod does not reference Unity.Entities.dll.
    /// </summary>
    [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.CurrentRelationship))]
    public static class Patch_Vehicle_CurrentRelationship
    {
        // Cached reflection handles — resolved once on first call.
        private static bool _reflectionResolved;
        private static bool _reflectionFailed;
        private static FieldInfo _vehicleEntityField;       // Vehicle.Entity
        private static PropertyInfo _defaultWorldProp;      // World.DefaultGameObjectInjectionWorld
        private static PropertyInfo _entityManagerProp;      // World.EntityManager
        private static MethodInfo _hasComponentMethod;       // EntityManager.HasComponent<ForcedRelationState>(Entity)
        private static MethodInfo _getComponentDataMethod;   // EntityManager.GetComponentData<ForcedRelationState>(Entity)
        private static FieldInfo _forcedStateField;          // ForcedRelationState.ForcedState

        private static void ResolveReflection()
        {
            if (_reflectionResolved) return;
            _reflectionResolved = true;

            try
            {
                // Vehicle.Entity field (type is Unity.Entities.Entity)
                _vehicleEntityField = typeof(Vehicle).GetField("Entity",
                    BindingFlags.Public | BindingFlags.Instance);

                // Unity.Entities.World type
                var worldType = Type.GetType("Unity.Entities.World, Unity.Entities");
                if (worldType == null)
                {
                    // Try scanning loaded assemblies
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        worldType = asm.GetType("Unity.Entities.World");
                        if (worldType != null) break;
                    }
                }

                // ForcedRelationState type (in Seapower-Scripts)
                var forcedRelationType = typeof(Vehicle).Assembly.GetType("SeaPower.ForcedRelationState");

                if (worldType == null || forcedRelationType == null || _vehicleEntityField == null)
                {
                    _reflectionFailed = true;
                    return;
                }

                // World.DefaultGameObjectInjectionWorld (static property)
                _defaultWorldProp = worldType.GetProperty("DefaultGameObjectInjectionWorld",
                    BindingFlags.Public | BindingFlags.Static);

                // World.EntityManager (instance property)
                _entityManagerProp = worldType.GetProperty("EntityManager",
                    BindingFlags.Public | BindingFlags.Instance);

                if (_defaultWorldProp == null || _entityManagerProp == null)
                {
                    _reflectionFailed = true;
                    return;
                }

                // EntityManager is a struct type
                var entityManagerType = _entityManagerProp.PropertyType;

                // EntityManager.HasComponent<T>(Entity) — generic method
                var hasComponentOpen = entityManagerType.GetMethod("HasComponent",
                    new[] { _vehicleEntityField.FieldType });
                if (hasComponentOpen != null && hasComponentOpen.IsGenericMethodDefinition)
                {
                    _hasComponentMethod = hasComponentOpen.MakeGenericMethod(forcedRelationType);
                }
                else
                {
                    // Search among all HasComponent methods for the right generic overload
                    foreach (var m in entityManagerType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name != "HasComponent" || !m.IsGenericMethodDefinition) continue;
                        var pars = m.GetParameters();
                        if (pars.Length == 1 && pars[0].ParameterType == _vehicleEntityField.FieldType)
                        {
                            _hasComponentMethod = m.MakeGenericMethod(forcedRelationType);
                            break;
                        }
                    }
                }

                // EntityManager.GetComponentData<T>(Entity) — generic method
                foreach (var m in entityManagerType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "GetComponentData" || !m.IsGenericMethodDefinition) continue;
                    var pars = m.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == _vehicleEntityField.FieldType)
                    {
                        _getComponentDataMethod = m.MakeGenericMethod(forcedRelationType);
                        break;
                    }
                }

                // ForcedRelationState.ForcedState field
                _forcedStateField = forcedRelationType.GetField("ForcedState",
                    BindingFlags.Public | BindingFlags.Instance);

                if (_hasComponentMethod == null || _getComponentDataMethod == null || _forcedStateField == null)
                {
                    _reflectionFailed = true;
                }
            }
            catch (Exception)
            {
                _reflectionFailed = true;
            }
        }

        static bool Prefix(Vehicle __instance, ref RelationsState __result)
        {
            // Destroyed objects -> Unknown
            if (__instance.BaseObject != null && __instance.BaseObject.IsDestroyed)
            {
                __result = RelationsState.Unknown;
                return false;
            }

            ResolveReflection();

            if (_reflectionFailed)
            {
                // Cannot check ECS — fall through to original method
                return true;
            }

            try
            {
                // Get the Entity value from the Vehicle instance
                object entity = _vehicleEntityField.GetValue(__instance);

                // Get World.DefaultGameObjectInjectionWorld
                object world = _defaultWorldProp.GetValue(null);
                if (world == null) return true;

                // Get the EntityManager from the world
                object entityManager = _entityManagerProp.GetValue(world);

                // PRIORITY 1 (FIXED): Check manual classification first
                bool hasForced = (bool)_hasComponentMethod.Invoke(entityManager, new[] { entity });
                if (hasForced)
                {
                    object forcedComponent = _getComponentDataMethod.Invoke(entityManager, new[] { entity });
                    __result = (RelationsState)_forcedStateField.GetValue(forcedComponent);
                    return false;  // Skip original — manual classification takes priority
                }
            }
            catch (Exception)
            {
                // If reflection fails at runtime, fall through to original
            }

            // PRIORITY 2: Fall through to original method for auto-detection
            return true;
        }
    }
}
