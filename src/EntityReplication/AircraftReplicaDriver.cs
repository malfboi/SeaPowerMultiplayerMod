using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Steers replica aircraft/helicopters between snapshots by feeding the native
    /// flight model a chase point derived from the host stream, using the game's
    /// own external-steering mechanism: with <c>commandPositionOverride = true</c>,
    /// FixedWingFlightPhysics/VTOLFlightPhysics skip their waypoint steering and fly
    /// toward our <c>commandPosition</c> (the same hook carrier Approach and
    /// VTOLTakeOff use). Attitude/banking stay native - no robotic transform lerps.
    /// Altitude is NOT overridden: DesiredAltitude is synced by UnitReplicaDriver
    /// and the native controller tracks it.
    ///
    /// The injection happens in a prefix on the concrete OnFixedUpdate overrides,
    /// which run inside Aircraft.OnFixedUpdate right after the aircraft wrote its
    /// own CommandVelocity - so our values win for the integration step.
    /// Deck phases use other MotionController subclasses (taxi/catapult/takeoff),
    /// which we deliberately leave untouched.
    /// </summary>
    public static class AircraftReplicaDriver
    {
        private struct AirTarget
        {
            public Vector3 PosUnity;
            public float   SpeedKts;
            public float   HeadingDeg;
            public float   RecvRealtime;
        }

        // Unity units per (knot · game-second) - the game's own conversion
        // (see Missile: _velocityInUnity = _velocityInKnots * 0.0076554087f).
        private const float UnityPerKnotSecond = 0.0076554087f;

        // Chase point this many game-seconds ahead along the streamed track.
        private const float ChaseHorizonSec = 2.5f;

        // Forget targets that stop receiving updates (unit despawned/landed).
        private const float StaleAfterSec = 5f;

        private static readonly Dictionary<int, AirTarget> _targets = new();
        private static readonly Dictionary<MotionController, ObjectBase?> _ownerCache = new();

        /// <summary>Called by UnitReplicaDriver for every applied air-unit entry.</summary>
        public static void Report(ObjectBase unit, Vector3 streamPosUnity, float speedKts, float headingDeg)
        {
            _targets[unit.UniqueID] = new AirTarget
            {
                PosUnity     = streamPosUnity,
                SpeedKts     = speedKts,
                HeadingDeg   = headingDeg,
                RecvRealtime = Time.realtimeSinceStartup,
            };
        }

        public static void Forget(int unitId) => _targets.Remove(unitId);

        public static void Reset()
        {
            _targets.Clear();
            _ownerCache.Clear();
        }

        public static int ActiveTargets => _targets.Count;

        private static ObjectBase? ResolveOwner(MotionController mc)
        {
            if (!_ownerCache.TryGetValue(mc, out var owner))
            {
                owner = mc.GetComponentInParent<ObjectBase>();
                _ownerCache[mc] = owner;
                if (_ownerCache.Count > 256) _ownerCache.Clear(); // controllers are recreated; avoid leak
            }
            return owner;
        }

        /// <summary>True when the stream is currently driving this controller's
        /// aircraft: client side, session established, and a fresh host sample in
        /// hand. Falls false when the target goes stale, so native behaviour is
        /// the fallback rather than a frozen aircraft.</summary>
        internal static bool IsStreamDriven(MotionController mc)
        {
            if (Plugin.Instance.CfgIsHost.Value) return false;
            if (!NetworkManager.Instance.IsEstablished) return false;
            var owner = ResolveOwner(mc);
            if (owner == null) return false;
            return _targets.TryGetValue(owner.UniqueID, out var t)
                && Time.realtimeSinceStartup - t.RecvRealtime <= StaleAfterSec;
        }

        private static readonly System.Type? FormationPhysicsType =
            AccessTools.TypeByName("SeaPower.FormationFlightPhysics");

        /// <summary>Wingman whose formation station-keeper is suppressed by
        /// <see cref="Patch_FormationFlightPhysics_OnFixedUpdate"/> - nothing else
        /// moves it, so UnitReplicaDriver must drive it every frame.</summary>
        internal static bool IsFormationPuppet(ObjectBase unit) =>
            FormationPhysicsType != null
            && unit is Aircraft a
            && a.Motioncontroller != null
            && a.Motioncontroller.GetType() == FormationPhysicsType;

        internal static void Steer(MotionController mc)
        {
            // Client-only, post-handshake; host aircraft fly natively
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (!NetworkManager.Instance.IsEstablished) return;

            var owner = ResolveOwner(mc);
            if (owner == null) return;

            if (!_targets.TryGetValue(owner.UniqueID, out var t))
            {
                if (mc.commandPositionOverride) mc.commandPositionOverride = false;
                return;
            }

            if (Time.realtimeSinceStartup - t.RecvRealtime > StaleAfterSec)
            {
                _targets.Remove(owner.UniqueID);
                if (mc.commandPositionOverride) mc.commandPositionOverride = false;
                return;
            }

            Vector3 dir = Quaternion.Euler(0f, t.HeadingDeg, 0f) * Vector3.forward;
            Vector3 chase = t.PosUnity + dir * (t.SpeedKts * UnityPerKnotSecond * ChaseHorizonSec);

            mc.commandPositionOverride = true;
            mc.commandPosition = chase;
            mc.CommandVelocity = t.SpeedKts * 0.514444f; // knots → m/s (overwrites SpeedCommand feed)
        }
    }

    [HarmonyPatch(typeof(FixedWingFlightPhysics), nameof(FixedWingFlightPhysics.OnFixedUpdate))]
    public static class Patch_FixedWingFlightPhysics_OnFixedUpdate
    {
        static void Prefix(FixedWingFlightPhysics __instance) => AircraftReplicaDriver.Steer(__instance);
    }

    [HarmonyPatch(typeof(VTOLFlightPhysics), nameof(VTOLFlightPhysics.OnFixedUpdate))]
    public static class Patch_VTOLFlightPhysics_OnFixedUpdate
    {
        static void Prefix(VTOLFlightPhysics __instance) => AircraftReplicaDriver.Steer(__instance);
    }

    /// <summary>
    /// CLIENT: formation wingmen. FormationFlightPhysics is not a steering model
    /// like FixedWing/VTOL - it is a station-keeper that writes transform.position
    /// and copies the leader's rotation DIRECTLY every physics tick, computed from
    /// the LOCAL leader replica, and it ignores commandPosition entirely, so the
    /// chase-point injection cannot reach it. Left running, it and
    /// UnitReplicaDriver's corrections fight over the transform frame by frame -
    /// the tug of war behind the violent wingman jitter, at its worst when the
    /// host wingman breaks station (missile evasion) and the local formation slot
    /// stops matching the stream at all. While a fresh stream target exists the
    /// original is skipped wholesale and UnitReplicaDriver drives the wingman as a
    /// pure kinematic puppet (no dead band - see DriveAircraft).
    ///
    /// The type is internal, hence TargetMethod. MovingInFormation swapping
    /// controllers back and forth stays harmless: both controller types are
    /// stream-driven on the client.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_FormationFlightPhysics_OnFixedUpdate
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("SeaPower.FormationFlightPhysics"), "OnFixedUpdate");

        static bool Prefix(object __instance) =>
            !AircraftReplicaDriver.IsStreamDriven((MotionController)__instance);
    }
}
