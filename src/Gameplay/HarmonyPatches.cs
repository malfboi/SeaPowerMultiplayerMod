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
    // The overlay's settings fields used to need a prefix here to mute the game's
    // hotkey pump while typing. The Noesis overlay sets InputHandler.typingActive
    // instead - the game's own flag, which OnUpdate already honours - so the
    // patch is gone rather than duplicated.

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

    /// <summary>Pooled weapons are reused without a fresh Awake, and the periodic
    /// Clear()+PopulateFromScene() (active objects only) drops parked pool
    /// instances - a relaunched weapon would otherwise be missing from the
    /// registry and never enter the host state stream. Every launch funnels
    /// through CommonLaunchSettings, so re-register here (Register is idempotent).</summary>
    [HarmonyPatch(typeof(WeaponBase), nameof(WeaponBase.CommonLaunchSettings))]
    public static class Patch_WeaponBase_RegisterOnLaunch
    {
        static void Postfix(WeaponBase __instance) => UnitRegistry.Register(__instance);
    }

    // ── Client physics: targeted null-guard patches ────────────────────────
    //
    // After save-file load, SpeedCommand.Value is null (only set when
    // setTelegraph() is called) and Formation can be null. These targeted
    // guards let physics run normally once the values are initialised.
    // NO blanket host-only suppressions - the client runs full local physics.

    [HarmonyPatch(typeof(Compartments), "UpdateWantedVelocityInKnots")]
    public static class Patch_Compartments_UpdateWantedVelocityInKnots
    {
        private static readonly HashSet<int> _loggedIds = new();
        internal static void ClearLogCache() => _loggedIds.Clear();

        // WantedVelocityInKnots has a private setter; skipping the method would
        // otherwise leave the previous frame's value in place.
        private static readonly MethodInfo _wantedVelocitySetter =
            AccessTools.PropertySetter(typeof(Compartments), nameof(Compartments.WantedVelocityInKnots));

        static bool Prefix(Compartments __instance)
        {
            if (__instance._baseObject?.SpeedCommand?.Value == null)
            {
                int id = __instance._baseObject?.UniqueID ?? -1;
                if (_loggedIds.Add(id))
                    Plugin.Log.LogWarning($"[Physics] SpeedCommand.Value is NULL for entity {id} — returning speed=0 (this blocks movement)");
                _wantedVelocitySetter.Invoke(__instance, new object[] { 0f });
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

    // Guard MovingInFormation.setRudderBasedOnCourse - Formation is null after save load
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

    // Guard VesselPropulsionSystem.OnUpdate - SpeedCommand null after save load
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

    // Guard EnvironmentAudioManager.OnStart - _mixer (AudioMixer) is null during save-file load.
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

    // Guard CIWS weapon constructor - the effect prefab can be null (tracer audio set
    // up against a bare EnvironmentAudioManager), and the throw kills the mission-load
    // coroutine that is building the unit.
    //
    // This used to also require SessionManager.SceneLoading, which is set only around
    // OUR load paths - so a host pressing Play Mission through the game's own menu,
    // outside a session, still lost the load to the same NRE. The gate bought nothing:
    // this is a constructor finalizer that only ever swallows NREs, and letting one
    // through has no upside at any point in the mod's lifecycle.
    [HarmonyPatch(typeof(WeaponSystemCIWS),
        MethodType.Constructor,
        new[] { typeof(ObjectBase), typeof(WeaponParameters), typeof(UnityEngine.GameObject), typeof(ObjectBaseParameters) })]
    public static class Patch_WeaponSystemCIWS_Ctor
    {
        static Exception? Finalizer(Exception __exception)
        {
            if (__exception is NullReferenceException)
            {
                Plugin.Log.LogWarning("[Patch] WeaponSystemCIWS NRE suppressed");
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
        // Host: last telegraph the remote player commanded, per vessel. The
        // submarine override has had this since its own logic (snorkel/cavitation/
        // evasion) was found re-speeding remote-owned boats; surface ships never
        // did, so anything host-side that calls setTelegraph - morale, formation
        // speed matching, state machines - could re-speed the remote player's
        // ships at will, and it stuck, because the host is authoritative.
        private static readonly Dictionary<int, int> _remoteCommanded = new();
        internal static void Reset() => _remoteCommanded.Clear();

        /// <summary>A custom (slider) speed matches no telegraph, so record a value
        /// no telegraph can equal - local callers are then locked out of the speed
        /// entirely, exactly as they are once the remote player picks a preset.</summary>
        internal static void NoteRemoteCustomSpeed(Vessel v)
        {
            if (Suppression.HostSuppressesRemoteTfAi(v))
                _remoteCommanded[v.UniqueID] = int.MinValue;
        }

        static PlayerOrderMessage Msg(Vessel v, int telegraph) => new PlayerOrderMessage
        {
            SourceEntityId = v.UniqueID,
            Order          = OrderType.SetSpeed,
            Speed          = telegraph,
        };

        static bool Prefix(Vessel __instance, int telegraph, out bool __state)
        {
            __state = true; // executed (Postfix may broadcast)

            if (OrderHandler.ApplyingFromNetwork)
            {
                if (Suppression.HostSuppressesRemoteTfAi(__instance))
                    _remoteCommanded[__instance.UniqueID] = telegraph;
                return true;
            }

            if (Suppression.HostSuppressesRemoteTfAi(__instance)
                && _remoteCommanded.TryGetValue(__instance.UniqueID, out int cmd)
                && telegraph != cmd)
            {
                __state = false;
                return false;
            }

            bool run = OrderSyncHelper.Prefix(__instance, Msg(__instance, telegraph));
            __state = run;
            return run;
        }

        static void Postfix(Vessel __instance, int telegraph, bool __state)
        {
            if (!__state) return;
            OrderSyncHelper.Postfix(__instance, Msg(__instance, telegraph));
        }
    }

    /// <summary>Submarine has its OWN setTelegraph override (it is NOT a Vessel) -
    /// without this patch client sub speed orders were never forwarded, and the
    /// 10 Hz state stream stomped the local change within a tick. Host side: the
    /// sub's internal logic (snorkel/cavitation/evasion) calls setTelegraph on its
    /// own - once the remote player has commanded a telegraph, local callers may
    /// not change it.</summary>
    [HarmonyPatch(typeof(Submarine), nameof(Submarine.setTelegraph))]
    public static class Patch_Submarine_SetTelegraph
    {
        // Host: last telegraph the remote player commanded, per sub
        private static readonly Dictionary<int, int> _remoteCommanded = new();
        internal static void Reset() => _remoteCommanded.Clear();

        /// <summary>A custom (slider) speed matches no telegraph, so record a value no
        /// telegraph can equal - the sub's own logic is then locked out of the speed
        /// entirely, exactly as it is once the remote player commands a preset.</summary>
        internal static void NoteRemoteCustomSpeed(Submarine s)
        {
            if (Suppression.HostSuppressesRemoteTfAi(s))
                _remoteCommanded[s.UniqueID] = int.MinValue;
        }

        static PlayerOrderMessage Msg(Submarine s, int telegraph) => new PlayerOrderMessage
        {
            SourceEntityId = s.UniqueID,
            Order          = OrderType.SetSpeed,
            Speed          = telegraph,
        };

        static bool Prefix(Submarine __instance, int telegraph, out bool __state)
        {
            __state = true; // executed (Postfix may broadcast)

            if (OrderHandler.ApplyingFromNetwork)
            {
                if (Suppression.HostSuppressesRemoteTfAi(__instance))
                    _remoteCommanded[__instance.UniqueID] = telegraph;
                return true;
            }

            // Host: the remote player owns the telegraph - the sub's own AI/state
            // logic may re-assert it but never change it.
            if (Suppression.HostSuppressesRemoteTfAi(__instance)
                && _remoteCommanded.TryGetValue(__instance.UniqueID, out int cmd)
                && telegraph != cmd)
            {
                __state = false;
                return false;
            }

            bool run = OrderSyncHelper.Prefix(__instance, Msg(__instance, telegraph));
            __state = run;
            return run;
        }

        static void Postfix(Submarine __instance, int telegraph, bool __state)
        {
            if (!__state) return;
            OrderSyncHelper.Postfix(__instance, Msg(__instance, telegraph));
        }
    }

    /// <summary>setPresetDepth writes DesiredAltitude DIRECTLY before calling
    /// setDepth, so AI preset-depth calls bypass the setDepth owner-guard. Block
    /// local preset changes outright on subs the local player doesn't command:
    /// host-side the remote player's subs (their depth orders arrive as raw
    /// SetDepth and don't go through presets), client-side every host-driven
    /// replica (whose depth comes down the state stream).</summary>
    [HarmonyPatch(typeof(Submarine), nameof(Submarine.setPresetDepth))]
    public static class Patch_Submarine_SetPresetDepth_Guard
    {
        static bool Prefix(Submarine __instance, ref int __result)
        {
            if (OrderHandler.ApplyingFromNetwork) return true;
            if (!Suppression.HostSuppressesRemoteTfAi(__instance)
                && !Suppression.ClientForeignUnit(__instance)) return true;
            __result = __instance._currentPresetDepth;
            return false;
        }
    }

    // NOTE: Patch_Vessel_SetRudderAngle removed.
    // setRudderAngle() takes a PHYSICAL rudder angle (-25..+25), but the receiver
    // interpreted it as a target heading (0-360). This caused ships to turn North
    // after session sync (Drift state calls setRudderAngle with small values →
    // misinterpreted as heading near 0°). Heading is synced indirectly through
    // waypoints + StateApplier position/heading corrections.
    // Also: SetRudderToHeading() writes _setRudderAngle directly, bypassing
    // setRudderAngle(), so the patch never caught normal autopilot steering anyway.


    // ── Manual rudder (A/D "hard left / hard right") ────────────────────────
    //
    // The autopilot path above is deliberately NOT synced, but the player's manual
    // rudder is a discrete order and has to be: under host authority the client's
    // ship is driven entirely by the host's stream (UnitReplicaDriver mirrors the
    // host's rudder and lerps heading 10x/s), so a client-side rudder keypress was
    // overwritten within ~100 ms and never reached the host at all - the client
    // simply could not steer, and the host saw none of their turns.
    //
    // Hooked at turnRudderLeft/Right (the only callers are InputHandler's A/D keys)
    // rather than setRudderAngle, so autopilot steering stays local. Vessel and
    // Submarine each declare their own copies of these methods.

    static class RudderSync
    {
        internal static PlayerOrderMessage Msg(ObjectBase u) => new PlayerOrderMessage
        {
            SourceEntityId = u.UniqueID,
            Order          = OrderType.SetRudder,
            Speed          = StateSerializer.GetRudderAngle(u),
        };

        /// <summary>Postfix, not prefix: the angle is a step relative to the current
        /// one, so it's only known after the method body has run.</summary>
        internal static void Sync(ObjectBase unit)
        {
            var msg = Msg(unit);
            if (OrderSyncHelper.Prefix(unit, msg))
                OrderSyncHelper.Postfix(unit, msg);
        }
    }

    [HarmonyPatch(typeof(Vessel), nameof(Vessel.turnRudderLeft))]
    public static class Patch_Vessel_TurnRudderLeft
    {
        static void Postfix(Vessel __instance) => RudderSync.Sync(__instance);
    }

    [HarmonyPatch(typeof(Vessel), nameof(Vessel.turnRudderRight))]
    public static class Patch_Vessel_TurnRudderRight
    {
        static void Postfix(Vessel __instance) => RudderSync.Sync(__instance);
    }

    [HarmonyPatch(typeof(Submarine), nameof(Submarine.turnRudderLeft))]
    public static class Patch_Submarine_TurnRudderLeft
    {
        static void Postfix(Submarine __instance) => RudderSync.Sync(__instance);
    }

    [HarmonyPatch(typeof(Submarine), nameof(Submarine.turnRudderRight))]
    public static class Patch_Submarine_TurnRudderRight
    {
        static void Postfix(Submarine __instance) => RudderSync.Sync(__instance);
    }


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

    // ── Attack / sonobuoy-drop waypoint intercept (bidirectional) ───────────
    //
    // EVERY player-issued sonobuoy drop lands here, not on the engage-task paths:
    // AttackingState (single, shift-chained and Ctrl/Alt pattern drops) and
    // SonobuoyLineState (line drops) all funnel through OffsetAttack →
    // SetAttackAtWaypointTask, which builds the AttackAtWaypoint task itself - it
    // never calls setWaypointTask, InsertEngageTask, AttackTask or DropSonobuoyTask,
    // so none of the existing hooks saw it. The client's drops therefore stayed
    // local: the host's helicopter never got the waypoints and never dropped, while
    // the RemoveWaypoints call the drop UI makes first (which IS synced) wiped the
    // helo's real route host-side. Air-dropped torpedoes and waypoint-edit attacks
    // take the same path and were broken the same way.
    //
    // Geo coordinates go on the wire unconverted (same as MoveTo) - mode-independent
    // and floating-origin safe. The client keeps its local copy for map display; its
    // execution is suppressed by Patch_V2_AttackAtWaypoint_Suppress.
    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.SetAttackAtWaypointTask),
        new[] { typeof(string), typeof(ObjectBase), typeof(GeoPosition), typeof(GeoPosition),
                typeof(int), typeof(VisualActionTask), typeof(EngageTask.SalvoType),
                typeof(float), typeof(bool), typeof(bool) })]
    public static class Patch_ObjectBase_SetAttackAtWaypointTask
    {
        static PlayerOrderMessage Msg(ObjectBase u, string ammunitionName, ObjectBase targetObject,
            GeoPosition targetGeoPosition, GeoPosition waypointGeoPosition, int salvo,
            EngageTask.SalvoType salvoType, float areaRadius, bool formationAttack, bool attackOnlyDetected)
            => new PlayerOrderMessage
            {
                SourceEntityId = u.UniqueID,
                Order          = OrderType.AttackAtWaypoint,
                AmmoId         = ammunitionName ?? "",
                ShotsToFire    = salvo,
                TargetEntityId = targetObject != null ? targetObject.UniqueID : 0,
                DestX          = (float)waypointGeoPosition._longitude,
                DestY          = (float)waypointGeoPosition._height,
                DestZ          = (float)waypointGeoPosition._latitude,
                TargetX        = (float)targetGeoPosition._longitude,
                TargetY        = (float)targetGeoPosition._height,
                TargetZ        = (float)targetGeoPosition._latitude,
                // The message is out of float fields, so the two attack flags ride in
                // the high bits of the salvo type. They are only ever non-default on
                // the WaypointData (mission/save import) overload, but dropping them
                // would silently change what the host's task does.
                Speed          = (int)salvoType
                                 | (formationAttack    ? 0x100 : 0)
                                 | (attackOnlyDetected ? 0x200 : 0),
                Heading        = areaRadius,
            };

        static bool Prefix(ObjectBase __instance, ref AttackAtWaypoint __result,
                           string ammunitionName, ObjectBase targetObject,
                           GeoPosition targetGeoPosition, GeoPosition waypointGeoPosition,
                           int salvo, EngageTask.SalvoType salvoType, float areaRadius,
                           bool formationAttack, bool attackOnlyDetected)
        {
            if (OrderSyncHelper.Prefix(__instance, Msg(__instance, ammunitionName, targetObject,
                    targetGeoPosition, waypointGeoPosition, salvo, salvoType, areaRadius,
                    formationAttack, attackOnlyDetected)))
                return true;

            __result = null; // refused (ally lock / not ours) - callers null-check
            return false;
        }

        static void Postfix(ObjectBase __instance,
                            string ammunitionName, ObjectBase targetObject,
                            GeoPosition targetGeoPosition, GeoPosition waypointGeoPosition,
                            int salvo, EngageTask.SalvoType salvoType, float areaRadius,
                            bool formationAttack, bool attackOnlyDetected)
            => OrderSyncHelper.Postfix(__instance, Msg(__instance, ammunitionName, targetObject,
                targetGeoPosition, waypointGeoPosition, salvo, salvoType, areaRadius,
                formationAttack, attackOnlyDetected));
    }


    // ── Formation control mode (bidirectional) ─────────────────────────────
    //
    // SelectedControlMode decides whether an attack order reaches the whole flight
    // or only the unit clicked. It is read in two places that matter here: the UI
    // distributes the order across the formation's stations at click time
    // (AttackingState.NormalAttack), and the host re-reads it when it runs the
    // formation attack distribution itself (AttackAtWaypoint.AttackCalculations).
    // Nothing synced it, so a flight the client set to "Follow Leader" was still
    // whatever the host's copy happened to hold, and the wingmen were left out of
    // orders given to their leader.
    //
    // Keyed on the leader unit: formations carry no id of their own, and the leader
    // is already replicated with the formation at spawn. The send verdict is
    // deliberately ignored - a refused write still happens locally (blocking it
    // would leave a foreign formation with no mode at all while it forms up), it
    // just does not travel. OrderSyncHelper's own refusal tag is consumed by the
    // paired Postfix, so nothing is broadcast that was refused.
    [HarmonyPatch(typeof(UnitFormation), "set_SelectedControlMode")]
    public static class Patch_UnitFormation_SelectedControlMode
    {
        static ObjectBase? Leader(UnitFormation f) => f?.LeaderStation?.UnitObject;

        static PlayerOrderMessage Msg(ObjectBase leader, UnitFormation.ControlMode mode) =>
            new PlayerOrderMessage
            {
                SourceEntityId = leader.UniqueID,
                Order          = OrderType.SetFormationMode,
                Speed          = (int)mode,
            };

        static void Prefix(UnitFormation __instance, UnitFormation.ControlMode value)
        {
            var leader = Leader(__instance);
            if (leader == null || leader.UniqueID == 0) return;
            OrderSyncHelper.Prefix(leader, Msg(leader, value));
        }

        static void Postfix(UnitFormation __instance, UnitFormation.ControlMode value)
        {
            var leader = Leader(__instance);
            if (leader == null || leader.UniqueID == 0) return;
            OrderSyncHelper.Postfix(leader, Msg(leader, value));
        }
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

            // 20Hz throttle per unit - mark pending if too soon
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


    // ── Flight deck: host-only pipeline, client launch intents upstream ─────

    /// <summary>In multiplayer, human-controlled taskforces launch aircraft only on
    /// explicit player orders - all autonomous air ops are suppressed for them.
    /// Co-op: both players share _playerTaskforce (the enemy stays AI). PvP: the
    /// "enemy" taskforce is the remote player, not an AI. Weapon self-defence
    /// (auto SAM/CIWS engagement) is a separate path and stays active.</summary>
    internal static class AutoAirOps
    {
        internal static bool IsHumanTaskforce(Taskforce tf)
        {
            if (tf == null) return false;
            if (tf == Globals._playerTaskforce) return true;
            return Plugin.Instance.CfgPvP.Value && tf == Globals._enemyTaskforce;
        }
    }

    // The game autonomously launches aircraft - formation/taskforce ASW station
    // keeping, AI CAP/AEW/MPA upkeep, scripted airstrikes, load-time auto-ready -
    // and ALL of it funnels through ObjectBase.FlightDeckLaunchUnit/-CAP. Player
    // clicks call FlightDeck.createLaunchTask directly and are unaffected.
    [HarmonyPatch]
    public static class Patch_FlightDeck_AutoLaunch_Suppress
    {
        // All overloads by name - exact signatures vary between game builds, and a
        // null target method would abort patching for the whole mod.
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(ObjectBase)))
                if ((m.Name == nameof(ObjectBase.FlightDeckLaunchUnit)
                     || m.Name == nameof(ObjectBase.FlightDeckLaunchCAP))
                    && m.ReturnType == typeof(bool))
                    yield return m;
        }

        static bool Prefix(ObjectBase __instance, ref bool __result)
        {
            if (!NetworkManager.Instance.IsEstablished) return true;
            if (!AutoAirOps.IsHumanTaskforce(__instance._taskforce)) return true;
            __result = false; // "nothing launched" - callers stop or retry harmlessly
            return false;
        }
    }

    [HarmonyPatch]
    public static class Patch_AI_HandleCarrierFunctions
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(AI), "HandleCarrierFunctions");

        static bool Prefix(ObjectBase ____baseObject)
        {
            if (!NetworkManager.Instance.IsConnected) return true;
            if (SessionManager.SceneLoading) return true;
            // v2 unified host authority: carrier flight ops run host-only,
            // for ALL carriers in both modes.
            if (!Plugin.Instance.CfgIsHost.Value) return false;
            // PvP: vanilla only skips _playerTaskforce, so the host AI would run
            // the remote player's carriers (formations, threat maneuvers, CAP).
            return !AutoAirOps.IsHumanTaskforce(____baseObject?._taskforce);
        }
    }

    [HarmonyPatch]
    public static class Patch_AI_LaunchAirstrike
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(AI), "LaunchAirstrike");

        static bool Prefix(ObjectBase ____baseObject)
        {
            if (!NetworkManager.Instance.IsConnected) return true;
            // v2 unified host authority: airstrike decisions are host-only.
            if (!Plugin.Instance.CfgIsHost.Value) return false;
            // Human-controlled taskforces plan their own strikes.
            return !AutoAirOps.IsHumanTaskforce(____baseObject?._taskforce);
        }
    }

    // ── FlightDeck.createLaunchTask: block + sync ───────────────────────────

    // FlightDeckViewModel.Launch/Ready are the only createLaunchTask callers that
    // represent a player action. Everything else - the AirStrike Launch state,
    // UnitFormation ASW station keeping, Taskforce/AI CAP helpers, save-load
    // restore - is automation the host already runs itself, and it RETRIES every
    // tick when the blocked call returns null, so forwarding those upstream turned
    // each retry into another real launch on the host (mass aircraft spawns).
    [HarmonyPatch]
    public static class Patch_FlightDeckViewModel_PlayerLaunch
    {
        public static bool InPlayerLaunch;

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SeapowerUI.ViewModels.FlightDeckViewModel), "Launch");
            yield return AccessTools.Method(typeof(SeapowerUI.ViewModels.FlightDeckViewModel), "Ready");
        }

        static void Prefix() => InPlayerLaunch = true;
        static void Finalizer() => InPlayerLaunch = false;
    }

    [HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.createLaunchTask))]
    public static class Patch_FlightDeck_CreateLaunchTask
    {
        static bool Prefix(FlightDeck __instance, VehicleTypeOnBoard vehicle, Loadout loadout,
            Squadron squadron, string callsign, LaunchTaskParameters ltp, bool allowLaunch)
        {
            // v2 unified host authority: the deck pipeline runs host-only; a client
            // launch click becomes an upstream intent order the host executes.
            if (NetworkManager.Instance.IsEstablished)
            {
                if (OrderHandler.ApplyingFromNetwork) return true;
                if (Plugin.Instance.CfgIsHost.Value) return true;

                // Client: the local deck stays inert either way; only a player click
                // in the Flight Ops window is forwarded upstream (see above).
                if (!Patch_FlightDeckViewModel_PlayerLaunch.InPlayerLaunch) return false;

                var v2vessel = __instance._baseObject;
                if (v2vessel == null || vehicle == null) return false;
                int vehicleIdx  = __instance._vehiclesOnBoard.IndexOf(vehicle);
                int loadoutIdx  = vehicle.Loadouts.IndexOf(loadout);
                int squadronIdx = vehicle.Squadrons.IndexOf(squadron);
                int callsignIdx = squadron != null ? squadron.Callsigns.IndexOf(callsign) : -1;
                if (vehicleIdx < 0) return false;

                NetworkManager.Instance.SendToServer(new PlayerOrderMessage
                {
                    SourceEntityId = v2vessel.UniqueID,
                    Order          = OrderType.LaunchAircraft,
                    Speed          = vehicleIdx,
                    Heading        = loadoutIdx,
                    DestX          = squadronIdx,
                    DestY          = callsignIdx,
                    DestZ          = ltp?._launchCount ?? 1,
                    ShotsToFire    = ltp != null ? (int)ltp._missionType : 0,
                    TargetEntityId = allowLaunch ? 1 : 0,
                });
                Telemetry.Count("v2.clientLaunchUpstream");
                Plugin.Log.LogInfo($"[FlightOps] Upstream launch intent: carrier={v2vessel.UniqueID} vehicle={vehicleIdx} count={ltp?._launchCount ?? 1}");
                return false;
            }

            return true;
        }
    }

    // ── FlightDeck.launchVehicle: host-only under v2 ────────────────────────

    [HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.launchVehicle))]
    public static class Patch_FlightDeck_LaunchVehicle
    {
        static bool Prefix(FlightDeck __instance)
        {
            // v2: deck launches happen on the host only; the spawned aircraft
            // replicates to the client via the createAircraft capture.
            if (NetworkManager.Instance.IsEstablished)
                return Plugin.Instance.CfgIsHost.Value;

            return true;
        }
    }

    // ── FlightDeck.abortLaunchTask: client → host ───────────────────────────
    //
    // The client's Flight Ops queue mirrors the host's (FlightDeckStateApplier), and
    // each display task carries the host's task Guid. Its Abort button calls
    // abortLaunchTask with that Guid - forward it upstream so the host actually
    // cancels the pending launch. The cancellation (task removed, stores/availability
    // restored) returns through the next FlightDeckState snapshot.
    [HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.abortLaunchTask))]
    public static class Patch_FlightDeck_AbortLaunchTask
    {
        static bool Prefix(FlightDeck __instance, System.Guid uid)
        {
            if (!Suppression.ClientActive) return true;        // host / offline: native
            if (OrderHandler.ApplyingFromNetwork) return true; // (safety - not used client-side)

            var carrier = __instance._baseObject;
            if (carrier == null) return false;

            NetworkManager.Instance.SendToServer(new PlayerOrderMessage
            {
                SourceEntityId = carrier.UniqueID,
                Order          = OrderType.AbortLaunch,
                AmmoId         = uid.ToString(),
            });
            Telemetry.Count("v2.clientAbortLaunchUpstream");
            Plugin.Log.LogInfo($"[FlightOps] Upstream abort launch: carrier={carrier.UniqueID} uid={uid}");
            return false;
        }
    }


    // ── Host-authoritative AI weapon fire ──────────────────────────────────

    static class AIAutoFireState
    {
        // Cached reflection for AI._baseObject (private field)
        // Internal: also used by Patch_AI_HandleCarrierFunctions and Patch_AI_LaunchAirstrike
        internal static readonly System.Reflection.FieldInfo _aiBaseObjectField =
            AccessTools.Field(typeof(AI), "_baseObject");

        /// <summary>Shared prefix for AI auto-fire/auto-attack patches.
        /// v2 unified host authority: the HOST runs auto-fire AI for ALL units
        /// (both taskforces, both modes); the client never fires locally -
        /// weapon spawns arrive as replicas via EntitySpawn.</summary>
        internal static bool Prefix(AI instance)
        {
            if (!NetworkManager.Instance.IsConnected) return true;
            return Plugin.Instance.CfgIsHost.Value;
        }
    }

    [HarmonyPatch]
    public static class Patch_AI_AutoFireGunsInRange
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(AI), "AutoFireGunsInRange");

        static bool Prefix(AI __instance) => AIAutoFireState.Prefix(__instance);
    }

    [HarmonyPatch]
    public static class Patch_AI_AutoAttackOpponentInRange
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(AI), "AutoAttackOpponentInRange");

        static bool Prefix(AI __instance) => AIAutoFireState.Prefix(__instance);
    }

    /// <summary>
    /// Prefix: PvP guard - track which enemy puppet units have received a network
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

        /// <summary>(unit, target) pairs from network fire orders. The remote
        /// player's explicit attack decision is authoritative: the host's crew
        /// contact-processing gate (AI.IsProcessed) must not veto it. Contacts
        /// are per-machine - a submerged sub only consults its OWN sonar picture,
        /// which on the host may never hold the target the ordering player could
        /// see; the engage task then sits queued forever and ripple-fires whenever
        /// the gate finally flips (observed: all 20 ASMs launching when the sub
        /// was torpedoed and its submerged/alert state changed).</summary>
        private static readonly HashSet<long> _networkOrderedPairs = new();

        private static long PairKey(int unitId, int targetId) => ((long)unitId << 32) | (uint)targetId;

        internal static void MarkNetworkOrderedTarget(int unitId, int targetId)
        {
            if (targetId != 0) _networkOrderedPairs.Add(PairKey(unitId, targetId));
        }

        internal static bool IsNetworkOrderedPair(int unitId, int targetId)
            => _networkOrderedPairs.Contains(PairKey(unitId, targetId));

        /// <summary>Clear tracking on disconnect/scene change.</summary>
        internal static void Reset()
        {
            _networkOrderedUnits.Clear();
            _networkOrderedPairs.Clear();
        }

        static bool Prefix(ObjectBase __instance)
        {
            if (!NetworkManager.Instance.IsConnected) return true;
            // v2 unified host authority: engage tasks execute on the host only.
            // The client never runs the firing pipeline - replica weapons arrive
            // via EntitySpawn. This also neutralizes save-file residual tasks.
            return Plugin.Instance.CfgIsHost.Value;
        }
    }

    /// <summary>HOST: bypass the crew contact-processing gate in HandleEngageTasks
    /// for fire orders that came from the remote player (see MarkNetworkOrderedTarget).
    /// Scoped to exact (unit, target) pairs so the unit's own auto-engage and
    /// auto-defence decisions keep the vanilla crew-processing behavior.</summary>
    [HarmonyPatch(typeof(AI), nameof(AI.IsProcessed))]
    public static class Patch_AI_IsProcessed_NetworkOrder
    {
        static void Postfix(ObjectBase ____baseObject, ObjectBase targetObject, ref bool __result)
        {
            if (__result) return;
            if (____baseObject == null || targetObject == null) return;
            if (!Plugin.Instance.CfgIsHost.Value || !NetworkManager.Instance.IsEstablished) return;
            if (Patch_ObjectBase_HandleEngageTasks.IsNetworkOrderedPair(
                    ____baseObject.UniqueID, targetObject.UniqueID))
                __result = true;
        }
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.InsertEngageTask))]
    public static class Patch_ObjectBase_InsertEngageTask
    {
        // v2 unified host authority: the host fires natively (every spawn
        // replicates to the client via EntitySpawn from CommonLaunchSettings);
        // the client never fires locally. Player fires route upstream from THIS
        // prefix - NOT from a patch on AddEngageTask: that is a one-line method
        // the JIT inlines into its callers, so a Harmony prefix on it never runs
        // (verified live - fire orders died silently there).
        //
        // AttackTask bearing-only attacks also call AddEngageTask directly (inlined)
        // and are forwarded separately via Patch_V2_AttackTask_BearingFire below.
        // Known remaining gap (same inlining reason): DropSonobuoyTask bypasses this.

        static bool Prefix(ObjectBase __instance, ref EngageTask __result,
                           string ammoId, ObjectBase targetObject, Vector3 targetPosition,
                           int shotsToFire, bool autoAttack, int priority)
        {
            if (OrderHandler.ApplyingFromNetwork) return true;
            if (!NetworkManager.Instance.IsEstablished) return true;
            if (SessionManager.SceneLoading) return true;

            // Ally lock, BOTH sides. This used to sit below the host early-return,
            // so it only ever gated the client: the host could pick a target with a
            // unit its partner held and simply fire, because under v2 the host fires
            // natively and the weapon reaches the client as a replicated spawn - no
            // order has to travel for it to happen, so the send-side guards cannot
            // see it. The refusal has to be here, at task creation.
            //
            // autoAttack is deliberately exempt: that is CIWS, SAM and auto-engage,
            // and a ship that stops defending itself because a partner clicked on it
            // would be far worse than the problem being fixed.
            if (!autoAttack && UnitLockManager.BlocksOrdersFor(__instance))
            {
                UnitLockManager.NoteOrderRefused(__instance);
                Plugin.Log.LogInfo($"[Fire] Engage rejected: unit {__instance.UniqueID} locked by remote");
                __result = null;
                return false;
            }

            if (Plugin.Instance.CfgIsHost.Value) return true;

            // AI/auto insertions die here (client AI is suppressed - belt and braces).
            if (autoAttack)
            {
                __result = null;
                return false;
            }

            if (!TaskforceAssignmentManager.ClientMayControl(__instance))
            {
                Plugin.Log.LogInfo($"[Fire] Engage rejected: unit {__instance.UniqueID} not controllable (TF assignment)");
                __result = null;
                return false;
            }

            SendClientFireOrder(__instance, ammoId, targetObject, targetPosition, shotsToFire);

            // Let the native method run: the caller (AttackTask) dereferences the
            // returned task. The postfix removes the local enqueue - the host owns
            // execution; the weapon returns as a replica via EntitySpawn.
            return true;
        }

        static void Postfix(ObjectBase __instance, EngageTask __result)
        {
            if (__result == null) return;
            if (OrderHandler.ApplyingFromNetwork) return;
            if (!NetworkManager.Instance.IsEstablished) return;
            if (SessionManager.SceneLoading) return;
            if (Plugin.Instance.CfgIsHost.Value) return;
            __instance._currentEngageTasks.Remove(__result);
        }

        /// <summary>Client → host fire order (pure upstream - the replica weapon
        /// returns via EntitySpawn ~RTT later, masked by launch sequencing).</summary>
        internal static void SendClientFireOrder(ObjectBase unit, string ammoId,
            ObjectBase targetObject, Vector3 targetPosition, int shotsToFire)
        {
            bool isSonobuoy = ammoId != null
                && ammoId.IndexOf("ssq", StringComparison.OrdinalIgnoreCase) >= 0;

            var msg = new PlayerOrderMessage
            {
                SourceEntityId = unit.UniqueID,
                Order          = isSonobuoy ? OrderType.DropSonobuoy : OrderType.FireWeapon,
                AmmoId         = ammoId,
                ShotsToFire    = shotsToFire,
                TargetEntityId = targetObject != null ? targetObject.UniqueID : 0,
            };

            // Position payload: the host resolves the target by id and only falls
            // back to the position if it dies mid-flight - send the target's
            // current position for that case, or the raw aim point for bearing fire.
            Vector3 aim = targetObject != null ? targetObject.transform.position : targetPosition;

            // Mode-faithful coordinate encoding (matches the host's decode):
            // PvP = GeoPosition (floating-origin safe), co-op = shared local coords.
            float x, y, z;
            if (Plugin.Instance.CfgPvP.Value)
            {
                var geo = Utils.worldPositionFromUnityToLongLat(aim, Globals._currentCenterTile);
                x = (float)geo._longitude; y = (float)geo._height; z = (float)geo._latitude;
            }
            else
            {
                x = aim.x; y = aim.y; z = aim.z;
            }

            if (isSonobuoy)
            {
                msg.DestX = x; msg.DestY = y; msg.DestZ = z;
            }
            else
            {
                msg.TargetX = x; msg.TargetY = y; msg.TargetZ = z;
            }

            NetworkManager.Instance.SendToServer(msg);
            Telemetry.Count("v2.clientFireUpstream");
            Plugin.Log.LogInfo($"[Fire] Upstream {msg.Order}: unit={unit.UniqueID} ammo={ammoId} " +
                $"target={msg.TargetEntityId} shots={msg.ShotsToFire}");
        }
    }

    // ── Bearing-only manual fire (client → host) ───────────────────────────
    //
    // AttackTask.OnExecute is the player's manual attack order. A TARGETED attack
    // calls InsertEngageTask (hooked above), but a BEARING-only attack (no target -
    // torpedo/missile/bomb fired down a bearing) calls AddEngageTask directly, which
    // the JIT inlines, so it slips past the InsertEngageTask hook and never reaches
    // the host (fires for neither side). Catch it at the un-inlined OnExecute: forward
    // the shot upstream and drop the local engage task. The host owns the launch; the
    // weapon returns as a replica via EntitySpawn. Targeted attacks are skipped here
    // (already handled by InsertEngageTask) to avoid double-firing.
    [HarmonyPatch(typeof(AttackTask), "OnExecute")]
    public static class Patch_V2_AttackTask_BearingFire
    {
        static void Prefix(AttackTask __instance, ObjectBase ____baseObject, ref int __state)
        {
            __state = -1;
            if (OrderHandler.ApplyingFromNetwork) return;
            if (!NetworkManager.Instance.IsEstablished) return;
            if (SessionManager.SceneLoading) return;
            if (Plugin.Instance.CfgIsHost.Value) return;

            // Targeted attacks route through InsertEngageTask; only bearing-only
            // fire reaches the inlined AddEngageTask path below in OnExecute.
            if (__instance.targetObject?.value != null) return;

            var unit = ____baseObject;
            if (unit == null) return;
            if (!TaskforceAssignmentManager.ClientMayControl(unit)) return;
            if (!Plugin.Instance.CfgPvP.Value && UnitLockManager.IsLockedByRemote(unit.UniqueID)) return;

            string ammo = __instance.ammunitionForEngage?.value;
            int salvo   = __instance.salvo?.value ?? 1;
            Vector3 targetPos = Utils.longLatToLocalV3(__instance.bearingPosition.value, Globals._currentCenterTile);

            Patch_ObjectBase_InsertEngageTask.SendClientFireOrder(unit, ammo, null, targetPos, salvo);

            // Let OnExecute run for the voice/order-log + finish() (so the behaviour
            // tree node completes), then strip the task it appended in the postfix.
            __state = unit._currentEngageTasks.Count;
        }

        static void Postfix(ObjectBase ____baseObject, int __state)
        {
            if (__state < 0 || ____baseObject == null) return;
            var tasks = ____baseObject._currentEngageTasks;
            for (int i = tasks.Count - 1; i >= __state; i--)
                tasks.RemoveAt(i);
        }
    }

    // ── Sonobuoy drop via DropSonobuoyTask (client → host) ──────────────────
    //
    // DropSonobuoyTask.OnExecute also calls the inlined AddEngageTask directly, but
    // on a DIFFERENT method than AttackTask.OnExecute, so the bearing-fire hook above
    // never covers it (flagged gap). The player's own drops do NOT come through here -
    // they are AttackAtWaypoint tasks (see Patch_ObjectBase_SetAttackAtWaypointTask);
    // this closes the Order.Type.DropSonobuoy path, which is how scripted and AI
    // sonobuoy orders arrive. Same shape as the AttackTask hook
    // - forward the drop, let OnExecute run for the order-log/finish(), then strip the
    // locally-appended task. Unlike AttackTask, this path never uses InsertEngageTask
    // for targeted drops either, so we forward both targeted and bearing cases. The
    // host owns the real drop, which replicates back as a LiveLocal sonobuoy.
    [HarmonyPatch(typeof(DropSonobuoyTask), "OnExecute")]
    public static class Patch_V2_DropSonobuoyTask_Fire
    {
        static void Prefix(DropSonobuoyTask __instance, ObjectBase ____baseObject, ref int __state)
        {
            __state = -1;
            if (OrderHandler.ApplyingFromNetwork) return;
            if (!NetworkManager.Instance.IsEstablished) return;
            if (SessionManager.SceneLoading) return;
            if (Plugin.Instance.CfgIsHost.Value) return;

            var unit = ____baseObject;
            if (unit == null) return;
            if (!TaskforceAssignmentManager.ClientMayControl(unit)) return;
            if (!Plugin.Instance.CfgPvP.Value && UnitLockManager.IsLockedByRemote(unit.UniqueID)) return;

            string ammo = __instance.AmmunitionType?.value;
            var target  = __instance._targetObject?.value;
            Vector3 pos = target != null
                ? target.transform.position
                : Utils.longLatToLocalV3(__instance._bearingPosition.value, Globals._currentCenterTile);

            Patch_ObjectBase_InsertEngageTask.SendClientFireOrder(unit, ammo, target, pos, 1);

            __state = unit._currentEngageTasks.Count;
        }

        static void Postfix(ObjectBase ____baseObject, int __state)
        {
            if (__state < 0 || ____baseObject == null) return;
            var tasks = ____baseObject._currentEngageTasks;
            for (int i = tasks.Count - 1; i >= __state; i--)
                tasks.RemoveAt(i);
        }
    }

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

        /// <summary>Register a depth commanded outside setDepth (the depth slider writes
        /// DesiredAltitude directly). Without this the lock holds a stale value: internal
        /// reverts are no longer suppressed, and a later preset that happens to match the
        /// stale entry is treated as "already applied" and never sent.
        /// <paramref name="depth"/> is positive-down Unity units, i.e. -DesiredAltitude.</summary>
        internal static void NoteCommandedDepth(Submarine s, float depth)
        {
            _lockedDepth[s.UniqueID] = depth;
            _lockTime[s.UniqueID] = Time.unscaledTime;
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

            // Client: never let our local sim re-depth a host-driven replica, and
            // never forward that decision to the host as a player order.
            if (Suppression.ClientForeignUnit(__instance)) return false;

            // Co-op: block UI depth changes on units locked by remote player
            if (!Plugin.Instance.CfgPvP.Value && UnitLockManager.IsLockedByRemote(__instance.UniqueID))
                return false;

            // Host: the remote player owns the depth - the sub's own AI/state logic
            // may maintain the commanded depth, but never change it. (Without this,
            // the AI re-commanded its old depth as soon as the 1 s grace expired -
            // client depth orders held for "a split second" and reverted.)
            if (Suppression.HostSuppressesRemoteTfAi(__instance)
                && _lockedDepth.TryGetValue(__instance.UniqueID, out float remoteDepth))
            {
                return Mathf.Abs(depth - remoteDepth) < 1f;
            }

            int id = __instance.UniqueID;
            float now = Time.unscaledTime;

            // Check if we have an active lock
            if (_lockTime.TryGetValue(id, out float setAt) && _lockedDepth.TryGetValue(id, out float locked))
            {
                bool sameDepth = Mathf.Abs(depth - locked) < 1f;
                bool inGrace = (now - setAt) < GracePeriod;

                if (sameDepth)
                    return true; // Maintenance of current depth - execute locally, don't send

                if (inGrace)
                    return false; // Internal call trying to revert during grace - suppress entirely
            }

            // Ally lock: this patch does its own send rather than going through
            // OrderSyncHelper, so it has to ask as well - otherwise a depth change
            // on a unit the partner holds executes here and travels to them.
            if (UnitLockManager.BlocksOrdersFor(__instance))
            {
                UnitLockManager.NoteOrderRefused(__instance);
                return false;
            }

            // Genuine depth change (player command or AI after grace period)
            _lockedDepth[id] = depth;
            _lockTime[id] = now;
            __state = true; // Signal Postfix to broadcast

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

            return OrderSyncHelper.Prefix(__instance, Msg(__instance));
        }

        static void Postfix(ObjectBase __instance, bool report)
        {
            if (!report) return;
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

        /// <summary>
        /// True when the Prefix that just ran refused the order. Harmony runs a
        /// postfix even when its prefix returned false, so without this the host
        /// broadcast an order it had just declined to execute locally: the client
        /// applied it and the host did not. That is exactly how ally-locked
        /// waypoints ended up visible on one screen only.
        ///
        /// The refusal is tagged with the order it belongs to, not just a bare
        /// flag. Some patches call Prefix with no paired Postfix, so a refusal can
        /// outlive its own call and be sitting there when an unrelated Postfix
        /// runs - which would silently drop that order's broadcast instead. Only a
        /// Postfix for the same unit and order type honours it.
        /// </summary>
        private static bool      _refusalPending;
        private static int       _refusedEntityId;
        private static OrderType _refusedOrder;

        internal static bool Prefix(ObjectBase unit, PlayerOrderMessage msg)
        {
            _refusalPending = false;
            if (OrderHandler.ApplyingFromNetwork) return true;
            if (SessionManager.SceneLoading) return true; // don't send during scene load
            // Weapons never produce orders worth sending, in EITHER mode. Under v2 the
            // host simulates every missile, torpedo, decoy and chaff round and streams
            // the result; anything a weapon does to itself is internal mechanics whose
            // waypoints and ids mean nothing on the other machine.
            //
            // This used to be scoped to PvP, which left co-op broadcasting a
            // RemoveWaypoints order for every round that DIED: setDestroyedFlag clears
            // a unit's waypoints as part of teardown (ObjectBase.cs:5514), so each
            // expiring chaff round, Harpoon, Mk46 and ASROC sent one. A single session
            // log showed 6,889 of them - two thirds of every line in the file.
            //
            // Asked before the ownership gates, not after: a weapon must never be
            // REFUSED either. The call site is its own destruction, so refusing left a
            // dying weapon holding its task list, and each refusal logged under a fresh
            // entity id (3,663 lines in that same log) because every round is new.
            if (unit is WeaponBase) return true;
            // Client: units we don't own are host-driven replicas. An order reaching
            // here for one came from our own local sim (unit state machines tick
            // outside the suppressed AI class) - block it locally AND upstream.
            if (Suppression.ClientForeignUnit(unit)) return Refuse(msg, "clientForeignUnit");
            // Co-op: block UI orders for units the remote player has selected (ally lock).
            if (UnitLockManager.BlocksOrdersFor(unit))
            {
                // The only refusal a player can actually cause, so the only one
                // worth telling them about. The others below are internal
                // suppression of orders the player never issued.
                UnitLockManager.NoteOrderRefused(unit);
                return Refuse(msg, "allyLock");
            }
            if (Plugin.Instance.CfgIsHost.Value) return true;
            if (!TaskforceAssignmentManager.ClientMayControl(unit)) return Refuse(msg, "notMyTaskforce");
            if (!OrderDeduplicator.ShouldSend(msg)) return true; // duplicate - skip send, still execute locally
            NetworkManager.Instance.SendToServer(msg);
            return true;
        }

        /// <summary>Refuse the order locally and tag it, so the paired Postfix
        /// does not broadcast what this machine just declined to do.
        ///
        /// The reason is logged, throttled per (unit, order, gate). Every gate here
        /// used to refuse silently, which is indistinguishable from a broken order
        /// path - the client executed nothing, sent nothing, and said nothing.
        /// Internal sim calls are refused constantly, hence the throttle.</summary>
        private static readonly Dictionary<(int, OrderType, string), float> _refusalLogThrottle = new();
        private const float RefusalLogInterval = 5f;

        internal static void ClearRefusalLogThrottle() => _refusalLogThrottle.Clear();

        private static bool Refuse(PlayerOrderMessage msg, string gate)
        {
            _refusalPending  = true;
            _refusedEntityId = msg.SourceEntityId;
            _refusedOrder    = msg.Order;

            var key = (msg.SourceEntityId, msg.Order, gate);
            if (!_refusalLogThrottle.TryGetValue(key, out var next) || Time.unscaledTime >= next)
            {
                _refusalLogThrottle[key] = Time.unscaledTime + RefusalLogInterval;
                Plugin.Log.LogInfo($"[Order] refused by {gate}: entity={msg.SourceEntityId} " +
                    $"order={msg.Order} value={msg.Speed}");
            }
            return false;
        }

        internal static void Postfix(ObjectBase unit, PlayerOrderMessage msg)
        {
            // Check before any other exit: this postfix runs on every path, and a
            // refusal left standing would suppress a later order's broadcast.
            if (_refusalPending
                && _refusedEntityId == msg.SourceEntityId
                && _refusedOrder == msg.Order)
            {
                _refusalPending = false;
                return;
            }

            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (OrderHandler.ApplyingFromNetwork) return;
            // Weapons are host-simulated and streamed in both modes - see Prefix.
            if (unit is WeaponBase) return;
            if (SessionManager.SceneLoading) return; // don't broadcast during scene load
            if (!OrderDeduplicator.ShouldSend(msg)) return; // duplicate - skip broadcast
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
            return -1; // FCR, targeting radar - not player-toggled
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
                case OrderType.CeaseFire:
                case OrderType.DropSonobuoy:
                case OrderType.SubmarineMast:
                case OrderType.SetAltitude:
                case OrderType.ReturnToBase:
                case OrderType.ClassifyContact:
                // RemoveWaypoints is a discrete, repeatable clear-all command. Its
                // fingerprint is constant (Speed=Heading=0), so value-dedup would
                // permanently suppress every clear after the first per unit - leaving
                // stale waypoints on the host when the player right-clicks to replace
                // a route. Always forward it (player-driven on the client; only fired
                // on AI transitions host-side, so no per-frame flood).
                case OrderType.RemoveWaypoints:
                // SetRudder is a discrete keypress step, already rate-limited by the
                // game to one per 0.5 s. Value-dedup would suppress a repeat of an
                // angle the other side's autopilot has since moved off.
                case OrderType.SetRudder:
                // A depth/altitude slider commit is one deliberate player action, never
                // a per-frame call - and it has no shared cache slot to keep current.
                case OrderType.SetHeightCustom:
                // Formation ops are discrete commands whose meaning is in the opcode,
                // not in Speed/Heading - the default fingerprint cannot tell two
                // different ops apart, and repeating one (rejoin, recall) is normal.
                case OrderType.FormationCommand:
                // Each attack/drop waypoint is its own discrete task. A pattern or
                // line drop issues several in one click whose only differing fields
                // are the positions, which the default fingerprint ignores - dedup
                // would collapse the whole pattern down to its first buoy.
                case OrderType.AttackAtWaypoint:
                    return true;
            }

            var key = MakeKey(msg);
            var fp  = MakeFingerprint(msg);

            if (_cache.TryGetValue(key, out var last) && last.Matches(fp))
                return false;

            float minInterval = GetMinInterval(msg.Order);
            if (minInterval > 0f && _lastSendTime.TryGetValue(key, out var lastTime) &&
                Time.unscaledTime - lastTime < minInterval)
            {
                // The value CHANGED and we are still dropping it. For a player-issued
                // order that is a lost command, not flood control - say so, because a
                // silent drop here is indistinguishable from a broken order path.
                Plugin.Log.LogWarning($"[Order] rate-limited: entity={msg.SourceEntityId} " +
                    $"order={msg.Order} value={msg.Speed} suppressed " +
                    $"({Time.unscaledTime - lastTime:F2}s < {minInterval:F2}s since last send)");
                return false;
            }

            _cache[key] = fp;
            _lastSendTime[key] = Time.unscaledTime;
            return true;
        }

        /// <summary>
        /// Record a network-RECEIVED order's value so local engine calls that merely
        /// re-apply it are recognised as duplicates and not echoed back.
        ///
        /// Deliberately does NOT touch <see cref="_lastSendTime"/>. That is the
        /// send-rate limiter, and a received order is not a send. Stamping it here
        /// permanently locked out the orders that have a min interval: the host
        /// rebroadcasts every setTelegraph it applies (including its own autopilot's,
        /// which run at tick rate), so the echo kept the client's timestamp fresh and
        /// every speed order the player then issued was executed locally, never sent,
        /// and reverted by the next state packet. SetSpeed (0.5 s) was the visible
        /// one; SensorToggle and SetEMCON (10 s) were affected the same way.
        /// </summary>
        internal static void UpdateCache(PlayerOrderMessage msg)
        {
            _cache[MakeKey(msg)] = MakeFingerprint(msg);
        }

        internal static void Clear()
        {
            OrderSyncHelper.ClearRefusalLogThrottle();
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
            // Preset and custom speed are the same setting reached two ways, so they
            // share one cache slot: switching between them always looks like a change
            // (the fingerprints differ in V3), while a true repeat is still suppressed.
            // On separate slots, re-picking the preset that was active before a custom
            // detour would fingerprint-match its own stale entry and never be sent.
            var order = msg.Order == OrderType.SetSpeedCustom ? OrderType.SetSpeed : msg.Order;
            return (msg.SourceEntityId, order, subKey);
        }

        private static Fingerprint MakeFingerprint(PlayerOrderMessage msg) => msg.Order switch
        {
            OrderType.EditWaypoint   => new Fingerprint { V1 = msg.DestX, V2 = msg.DestZ, V3 = msg.DestY },
            OrderType.MoveTo         => new Fingerprint { V1 = msg.DestX, V2 = msg.DestZ, V3 = msg.DestY },
            OrderType.SensorToggle   => new Fingerprint { V1 = msg.Speed },
            OrderType.SetSpeedCustom => new Fingerprint { V1 = msg.Speed, V3 = 1f },
            _                        => new Fingerprint { V1 = msg.Speed, V2 = msg.Heading },
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
    // combat damage callbacks - both of which must be allowed through.

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.DisableAllActiveSensors))]
    public static class Patch_DisableAllActiveSensors
    {
        /// <summary>Shared guard: allow sensor changes only for own-side units (or network-applied).</summary>
        internal static bool AllowSensorChange(ObjectBase unit)
        {
            if (OrderHandler.ApplyingFromNetwork) return true;
            if (!NetworkManager.Instance.IsConnected) return true;
            // Co-op: block sensor changes on units locked by remote player (ally)
            if (!Plugin.Instance.CfgPvP.Value)
                return !UnitLockManager.IsLockedByRemote(unit.UniqueID);
            // PvP: only own-side units can change sensors
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

    // Active sonar was the hole in this gate. EnableAllActiveSensors fans out to the
    // two radar methods above plus EnableActiveSonars, so crew AI acting on a unit the
    // other player owns had its radar half blocked and its sonar half go through -
    // straight onto the sonar, which writes _sonar.IsActive directly.

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.EnableActiveSonars))]
    public static class Patch_EnableActiveSonars
    {
        static bool Prefix(ObjectBase __instance) => Patch_DisableAllActiveSensors.AllowSensorChange(__instance);
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.DisableActiveSonars))]
    public static class Patch_DisableActiveSonars
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

    // ── Active sonar (group 2) - subscription-based ────────────────────────
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
                // Own send path, so the ally lock has to be asked here too.
                if (UnitLockManager.BlocksOrdersFor(unit)) return;
                // ...and the ownership test the radar path gets from OrderSyncHelper.
                // Without it the client relayed sonar flips its own local sim made on
                // the remote player's units, and the host applied them to its real
                // ships. ClientMayControl is no substitute: no task force is ever
                // assigned, so it returns true for everything.
                if (Suppression.ClientForeignUnit(unit)) return;

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
    // double-sending - the mast patch handles the sync.

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


    // ── Damage decal replication ────────────────────────────────────────────
    //
    // Combat (and so decal creation) runs host-only; capture decals parented
    // to a unit (ship/sub) on the host and send to the client for recreation.

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
            if (!CaptureState.HostCaptureActive) return;
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
            NetworkManager.Instance.BroadcastToClients(msg, DeliveryMethod.ReliableOrdered);
            Telemetry.Count("v2.capturedDecal");
        }
    }

    // ── Manual chaff deployment (Shift+C → ObjectBase.LaunchChaff) ──────────

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.LaunchChaff))]
    public static class Patch_ObjectBase_LaunchChaff
    {
        static bool Prefix(ObjectBase __instance, bool message)
        {
            if (OrderHandler.ApplyingFromNetwork) return true;

            // v2: chaff is host-authoritative - the host launches natively and the
            // resulting clouds replicate as decoy spawns; client clicks go upstream.
            if (NetworkManager.Instance.IsEstablished)
            {
                if (Plugin.Instance.CfgIsHost.Value) return true;

                // message=false is the missile-evasion states, not the player
                // (InputHandler and the weapons panel both pass true). Incoming
                // missile REPLICAS register as threats, so the client's aircraft
                // enter evasion locally and were originating chaff decisions of
                // their own. In co-op the host runs the same evasion AI for this
                // aircraft and chaffs natively - drop the duplicate. PvP keeps
                // auto-chaff by design, so it still forwards.
                if (!message && !Plugin.Instance.CfgPvP.Value) return false;

                NetworkManager.Instance.SendToServer(new PlayerOrderMessage
                {
                    SourceEntityId = __instance.UniqueID,
                    Order          = OrderType.LaunchChaff,
                });
                Telemetry.Count("v2.clientChaffUpstream");
                return false;
            }

            return true;
        }
    }

    // ── Manual noisemaker deployment (Shift+D) ──────────────────────────────
    //
    // Unlike chaff, the manual noisemaker has no clean ObjectBase method to hook:
    // InputHandler.OnUpdate queues a noisemaker EngageTask via AddEngageTask (a
    // one-line method the JIT inlines, so it can't be patched), which also bypasses
    // the InsertEngageTask fire-order sync. The client never runs the firing
    // pipeline either (HandleEngageTasks is host-only), so that queued task is inert
    // - the client's manual noisemaker never reached the host. Mirror chaff: detect
    // the keypress here and forward a discrete LaunchNoisemaker order. The host runs
    // it natively (launchNoisemaker → real decoy → captured → replicated back as a
    // spawn). The host's own Shift+D already fires natively, so it needs no forward.
    [HarmonyPatch(typeof(InputHandler), nameof(InputHandler.OnUpdate))]
    public static class Patch_InputHandler_NoisemakerUpstream
    {
        static void Postfix(InputHandler __instance)
        {
            if (OrderHandler.ApplyingFromNetwork) return;
            if (!NetworkManager.Instance.IsEstablished) return;
            if (Plugin.Instance.CfgIsHost.Value) return; // host launches natively
            if (!__instance.getKeyDown(KeyAction.LaunchNoisemaker)) return;

            var unit = Singleton<RenderPosition>.InstanceExists()
                ? Singleton<RenderPosition>.Instance.getSelectedObject() : null;
            if (unit == null || !unit.isUnit() || !unit.AcceptsOrdersFromPlayer) return;
            if (!TaskforceAssignmentManager.ClientMayControl(unit)) return;
            if (!HasAvailableNoisemaker(unit)) return; // host launchNoisemaker would no-op

            NetworkManager.Instance.SendToServer(new PlayerOrderMessage
            {
                SourceEntityId = unit.UniqueID,
                Order          = OrderType.LaunchNoisemaker,
            });
            Telemetry.Count("v2.clientNoisemakerUpstream");
        }

        static bool HasAvailableNoisemaker(ObjectBase unit)
        {
            foreach (var kv in unit.AmmunitionAmountDictionary)
            {
                if (kv.Value < 1) continue;
                var ammo = unit.getAmmunitionByName(kv.Key);
                if (ammo != null && ammo._ap._type == Ammunition.Type.Noisemaker) return true;
            }
            return false;
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
    // IMPORTANT: We must NOT simply set UnitTaskforce.Value in the Postfix -
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

        // ReactiveProperty<Taskforce> backing field - set directly to bypass subscriptions
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

    // ── Unit Selection Broadcast (Co-op) ─────────────────────────────────────

    [HarmonyPatch(typeof(RenderPosition), nameof(RenderPosition.switchToObject))]
    public static class Patch_RenderPosition_SwitchToObject
    {
        static void Postfix(ObjectBase objectToAttach)
        {
            if (Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (objectToAttach == null) return;

            // Verify the selection actually took effect
            var current = Singleton<RenderPosition>.Instance.SelectedObject;
            if (current == null || current.UniqueID != objectToAttach.UniqueID) return;

            int newId = objectToAttach.UniqueID;
            int previousClaim = UnitLockManager.LocalControlledUnitId;

            // If the remote player already controls this unit, we're only spectating -
            // don't broadcast a claim (would cause both sides to see each other as remote-locked).
            if (UnitLockManager.IsLockedByRemote(newId))
            {
                // Release any prior claim so the remote knows we've let go.
                if (previousClaim != 0 && previousClaim != newId)
                {
                    NetworkManager.Instance.SendToOther(new GameEventMessage
                    {
                        EventType = GameEventType.UnitDeselected,
                        Param     = (float)previousClaim,
                    });
                    UnitLockManager.ClearLocalControlled();
                }
                return;
            }

            // Claim control of the new unit. UnitSelected overwrites the remote's
            // tracked ID, so we don't need a separate deselect for any prior claim.
            if (previousClaim == newId) return; // already claimed, skip redundant broadcast
            NetworkManager.Instance.SendToOther(new GameEventMessage
            {
                EventType = GameEventType.UnitSelected,
                Param     = (float)newId,
            });
            UnitLockManager.SetLocalControlled(newId);
        }
    }

    [HarmonyPatch(typeof(RenderPosition), nameof(RenderPosition.deselectObjectAndDetachCamera))]
    public static class Patch_RenderPosition_DeselectObjectAndDetachCamera
    {
        static void Prefix()
        {
            if (Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;

            // Only release a claim we actually made. If we were spectating a
            // remote-controlled unit, _localControlledUnitId is 0 and we stay silent.
            int claimed = UnitLockManager.LocalControlledUnitId;
            if (claimed == 0) return;

            NetworkManager.Instance.SendToOther(new GameEventMessage
            {
                EventType = GameEventType.UnitDeselected,
                Param     = (float)claimed,
            });
            UnitLockManager.ClearLocalControlled();
        }
    }

    // ── IsControllable override (Co-op) ──────────────────────────────────────

    /// <summary>
    /// In co-op, forces <see cref="ObjectBase.IsControllable"/> to false for any unit
    /// the remote player currently has selected. This delegates to the game's built-in
    /// ally handling: the local player can still select and spectate the unit, but the
    /// game's own code will reject order entry and render it as uncontrollable.
    /// </summary>
    [HarmonyPatch(typeof(ObjectBase), "get_IsControllable")]
    public static class Patch_ObjectBase_IsControllable
    {
        static void Postfix(ObjectBase __instance, ref bool __result)
        {
            if (Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (__instance == null) return;
            // Host is authoritative: it must execute client-originated orders to
            // completion, and engage tasks are processed asynchronously outside the
            // ApplyingFromNetwork scope. Forcing IsControllable=false on the host
            // would make the game's own fire logic drop queued fires mid-tick.
            // Only apply the override on the client side.
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (OrderHandler.ApplyingFromNetwork) return;
            // Never during a scene load. The game restores saved state by REPLAYING
            // orders through the same setOrder path a player uses, and Order.setOrder
            // drops the call outright when IsControllable is false. Whichever unit the
            // remote player happened to have selected then silently loses its restored
            // order - and FlightDeck.LoadStateFromFile immediately reads back the task
            // that order was supposed to create (`_flightDeckTasksToAdd[Count - 1]`,
            // unguarded), so an aircraft under a ReturnTask took the whole load down
            // with an IndexOutOfRange and left the client on a dead loading screen.
            // A lock is about live order entry; it has no business filtering a restore.
            if (SessionManager.SceneLoading) return;
            if (UnitLockManager.IsLockedByRemote(__instance.UniqueID))
                __result = false;
        }
    }

    // ── MapUnitViewModel lock indicator ──────────────────────────────────────
    //
    // When the remote player selects a unit, we tag its map label with "[ALLY]"
    // so the local player can see at a glance which contact their partner is
    // driving. The registry tracks live VMs so UnitLockManager can fire a
    // PropertyChanged and make Noesis re-read ContactInfoLine2.

    [HarmonyPatch(typeof(MapUnitViewModel), MethodType.Constructor,
        new[] { typeof(Taskforce), typeof(Vehicle), typeof(ReactiveProperty<ISelectableObject>), typeof(bool) })]
    public static class Patch_MapUnitViewModel_Ctor
    {
        static void Postfix(MapUnitViewModel __instance) =>
            MapUnitViewModelRegistry.Register(__instance);
    }

    [HarmonyPatch(typeof(MapUnitViewModel), nameof(MapUnitViewModel.Dispose))]
    public static class Patch_MapUnitViewModel_Dispose
    {
        static void Prefix(MapUnitViewModel __instance) =>
            MapUnitViewModelRegistry.Unregister(__instance);
    }

    [HarmonyPatch(typeof(MapUnitViewModel), "get_ContactInfoLine2")]
    public static class Patch_MapUnitViewModel_ContactInfoLine2
    {
        static void Postfix(MapUnitViewModel __instance, ref string __result)
        {
            if (Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            var obj = __instance.Unit?.BaseObject as ObjectBase;
            if (obj != null && UnitLockManager.IsLockedByRemote(obj.UniqueID))
                __result = "[ALLY]";
        }
    }

    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.setPresetHeight))]
    public static class Patch_Aircraft_SetPresetHeight
    {
        static PlayerOrderMessage Msg(ObjectBase u, int preset, bool updateAlt) => new PlayerOrderMessage
        {
            SourceEntityId = u.UniqueID,
            Order          = OrderType.SetAltitude,
            Speed          = (float)preset,
            Heading        = updateAlt ? 1f : 0f,
        };

        static bool Prefix(Aircraft __instance, int preset, bool updateAltForWaypoints) =>
            OrderSyncHelper.Prefix(__instance, Msg(__instance, preset, updateAltForWaypoints));

        static void Postfix(Aircraft __instance, int preset, bool updateAltForWaypoints) =>
            OrderSyncHelper.Postfix(__instance, Msg(__instance, preset, updateAltForWaypoints));
    }

    [HarmonyPatch(typeof(Helicopter), nameof(Helicopter.setPresetHeight))]
    public static class Patch_Helicopter_SetPresetHeight
    {
        static PlayerOrderMessage Msg(ObjectBase u, int preset, bool updateAlt) => new PlayerOrderMessage
        {
            SourceEntityId = u.UniqueID,
            Order          = OrderType.SetAltitude,
            Speed          = (float)preset,
            Heading        = updateAlt ? 1f : 0f,
        };

        static bool Prefix(Helicopter __instance, int preset, bool updateAltForWaypoints) =>
            OrderSyncHelper.Prefix(__instance, Msg(__instance, preset, updateAltForWaypoints));

        static void Postfix(Helicopter __instance, int preset, bool updateAltForWaypoints) =>
            OrderSyncHelper.Postfix(__instance, Msg(__instance, preset, updateAltForWaypoints));
    }

    // ── Fixed-wing aircraft speed (client → host) ────────────────────────────
    //
    // Aircraft is an ObjectBase, NOT a Vessel, so Patch_Vessel_SetTelegraph never
    // intercepts it. The fixed-wing speed context menu (ObjectSpeed) also bypasses
    // setTelegraph entirely - it calls SetSpeedCommand(new ConstantMach(...)) with a
    // value taken straight from Ap._speedValuesInMach. We hook SetSpeedCommand,
    // recover the preset index by matching that mach value, and forward it as a
    // SetSpeed order so the client can command its own aircraft's speed.
    //
    // Client-only: on the host the player's change flows back to the client through
    // the 10 Hz entity stream (Telegraph/velocity), so no host broadcast is needed -
    // and the host's AI calls SetSpeedCommand every tick, which we must not echo.
    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.SetSpeedCommand))]
    public static class Patch_Aircraft_SetSpeedCommand
    {
        static bool Prefix(ObjectBase __instance, ISpeedCommand speedCommand)
        {
            if (OrderHandler.ApplyingFromNetwork) return true;
            if (!NetworkManager.Instance.IsConnected) return true;
            if (Plugin.Instance.CfgIsHost.Value) return true; // host: stream carries aircraft speed
            if (SessionManager.SceneLoading) return true;
            if (!(__instance is Aircraft ac)) return true;
            if (!(speedCommand is ConstantMach cm)) return true;

            var speeds = ac.Ap?._speedValuesInMach;
            if (speeds == null) return true;

            int idx = Array.IndexOf(speeds, cm.SpeedInMach);
            if (idx < 0) return true; // not a player preset (AI/continuous value) - don't sync

            // Host applies via setTelegraph((int)Speed); Aircraft.setTelegraph does
            // _telegraph = telegraph-1, so send idx+1 to land on _telegraph == idx.
            var msg = new PlayerOrderMessage
            {
                SourceEntityId = ac.UniqueID,
                Order          = OrderType.SetSpeed,
                Speed          = idx + 1,
            };

            return OrderSyncHelper.Prefix(ac, msg);
        }
    }

    // ── Custom speed / depth / altitude (slider + typed entry) ──────────────
    //
    // The player can command an arbitrary speed, depth or altitude instead of a
    // preset. Every one of those commits bypasses the methods the preset patches
    // hook: speed goes straight to SetSpeedCommand(new ConstantSpeed/ConstantMach)
    // rather than setTelegraph, and depth/altitude write DesiredAltitude directly
    // rather than going through setDepth/setPresetHeight. Nothing reached the wire.
    //
    // All five commit sites are ISliderValueEntry.CommitSliderValue() on internal
    // SeapowerUI.ViewModels types, so they are resolved by name and share one
    // postfix - the commit has already applied locally, we read the result off the
    // unit and forward it. Patching the commit (rather than SetSpeedCommand or the
    // DesiredAltitude property) keeps this to the player's own deliberate action:
    // AI and autopilot drive the same engine calls every tick.

    [HarmonyPatch]
    public static class Patch_SliderEntry_Commit
    {
        private const string Ns = "SeapowerUI.ViewModels.";

        // Speed view models; ObjectAirVehicleVelocity's subclasses inherit its commit.
        private static readonly string[] SpeedViewModels =
        {
            "ObjectVesselVelocityViewModel",
            "ObjectSubmarineVelocityViewModel",
            "ObjectAirVehicleVelocity",
        };

        private static readonly string[] HeightViewModels =
        {
            "ObjectSubmarineDepthViewModel",
            "ObjectAirVehicleAltitudeViewModel",
        };

        // Each view model holds its unit in a differently named private field.
        private static readonly string[] UnitFields = { "_vessel", "_submarine", "_objectBase", "_airVehicle" };

        private static readonly HashSet<Type> _heightTypes = new();
        private static readonly Dictionary<Type, FieldInfo?> _unitFieldCache = new();

        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var m in Resolve(SpeedViewModels)) yield return m;
            foreach (var m in Resolve(HeightViewModels)) { _heightTypes.Add(m.DeclaringType); yield return m; }
        }

        private static IEnumerable<MethodBase> Resolve(string[] typeNames)
        {
            foreach (var name in typeNames)
            {
                var type = AccessTools.TypeByName(Ns + name);
                var method = type == null ? null : AccessTools.Method(type, "CommitSliderValue");
                if (method != null) yield return method;
                else Plugin.Log.LogWarning($"[Patch] slider commit not found: {Ns}{name} - custom values will not sync for it");
            }
        }

        private static ObjectBase? ResolveUnit(object vm)
        {
            var type = vm.GetType();
            if (!_unitFieldCache.TryGetValue(type, out var field))
            {
                foreach (var name in UnitFields)
                {
                    field = AccessTools.Field(type, name);
                    if (field != null) break;
                }
                _unitFieldCache[type] = field;
            }
            return field?.GetValue(vm) as ObjectBase;
        }

        static void Postfix(object __instance, MethodBase __originalMethod)
        {
            if (OrderHandler.ApplyingFromNetwork) return;
            if (SessionManager.SceneLoading) return;
            if (!NetworkManager.Instance.IsConnected) return;

            var unit = ResolveUnit(__instance);
            if (unit == null || unit.UniqueID == 0) return;

            PlayerOrderMessage msg;
            if (_heightTypes.Contains(__originalMethod.DeclaringType))
            {
                msg = new PlayerOrderMessage
                {
                    SourceEntityId = unit.UniqueID,
                    Order          = OrderType.SetHeightCustom,
                    Speed          = unit.DesiredAltitude.Value,
                };
            }
            else
            {
                float knots = StateSerializer.CustomCommandKnots(unit);
                if (float.IsNaN(knots)) return; // commit did not take - nothing to send
                msg = new PlayerOrderMessage
                {
                    SourceEntityId = unit.UniqueID,
                    Order          = OrderType.SetSpeedCustom,
                    Speed          = knots,
                };
            }

            // A false verdict means this unit does not take local commands (host-driven
            // replica, or ally-locked) - so don't record the depth either, or the lock
            // would hold a value the unit never took.
            if (!OrderSyncHelper.Prefix(unit, msg)) return;

            if (msg.Order == OrderType.SetHeightCustom && unit is Submarine sub)
                Patch_Submarine_SetDepth.NoteCommandedDepth(sub, -msg.Speed);

            OrderSyncHelper.Postfix(unit, msg);
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

            // NOT OrderSyncHelper: its client path gates on ClientForeignUnit and
            // ClientMayControl, both of which reject the object here - the target
            // of a classification is a CONTACT, i.e. by definition a unit the
            // player does not own. Routed through it, the client's own
            // Hostile/Neutral calls were dropped and only the host's ever
            // travelled, so classification appeared one-way.
            if (!NetworkManager.Instance.IsConnected) return;

            // Co-op only. In PvP a classification is about the OTHER player's unit,
            // and StateSerializer resolves the incoming id against the receiver's own
            // plotting table - so the host marking an enemy destroyer Hostile made
            // that player's own ship render as hostile on their own map, on top of
            // telling them they had been spotted and classified.
            if (Plugin.Instance.CfgPvP.Value) return;

            if (!OrderDeduplicator.ShouldSend(msg)) return;

            if (Plugin.Instance.CfgIsHost.Value) NetworkManager.Instance.BroadcastToClients(msg);
            else                                 NetworkManager.Instance.SendToServer(msg);

            BroadcastFormationMembers(baseObj, forcedState);
        }

        /// <summary>
        /// HOST: mirror the game's formation fan-out at the message level. For an
        /// air unit in a formation, OverrideRelationship writes ForcedRelationState
        /// onto every member's entity DIRECTLY - it never calls itself on them, so
        /// the postfix fires once, for the selected unit only. The client cannot
        /// expand the formation on its own end (its replicas do not carry the
        /// host's formation state), so the host - whose sim just did the expansion -
        /// sends one ClassifyContact per member. Called both when the host
        /// designates locally (postfix above) and when it applies a client's
        /// designation (OrderHandler), which echoes the members back to the client
        /// that originated it.
        /// </summary>
        internal static void BroadcastFormationMembers(ObjectBase baseObj, RelationsState forcedState)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;

            // Same gate the game's own fan-out uses: air unit, in a formation,
            // and every occupied station holds an air unit.
            if (!baseObj.IsAirUnit || !baseObj.InFormation.Value) return;
            var stations = baseObj.Formation?.Stations;
            if (stations == null) return;

            foreach (var station in stations)
                if (station?.UnitObject != null && !station.UnitObject.IsAirUnit) return;

            foreach (var station in stations)
            {
                var member = station?.UnitObject;
                if (member == null || member == baseObj || member.UniqueID == 0) continue;

                NetworkManager.Instance.BroadcastToClients(new PlayerOrderMessage
                {
                    SourceEntityId = member.UniqueID,
                    Order          = OrderType.ClassifyContact,
                    Speed          = (float)forcedState,
                });
            }
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
        // Cached reflection handles - resolved once on first call.
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

                // EntityManager.HasComponent<T>(Entity) - generic method
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

                // EntityManager.GetComponentData<T>(Entity) - generic method
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
                // Cannot check ECS - fall through to original method
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
                    return false;  // Skip original - manual classification takes priority
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
