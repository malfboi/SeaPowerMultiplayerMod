using System.Reflection;
using HarmonyLib;
using SeaPower;
using SeaPowerAI;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// CLIENT-side v2 suppression: under unified host authority the client never
    /// makes gameplay decisions. All AI, all auto-defence, all weapon collision /
    /// fuse / damage logic is host-only; the client renders replicas and forwards
    /// orders. Everything here is gated on (!IsHost && IsEstablished) so offline
    /// play stays fully vanilla.
    /// </summary>
    public static class Suppression
    {
        internal static bool ClientActive =>
            !Plugin.Instance.CfgIsHost.Value && NetworkManager.Instance.IsEstablished;

        /// <summary>HOST-side PvP: true when the unit belongs to the remote player's
        /// taskforce (the host's EnemyTaskforce after the client's side swap). Used
        /// to suppress carrier AUTONOMY for those units (the remote player commands
        /// their own flight ops). Per-unit AI otherwise stays alive - it runs their
        /// auto-defence, governed by the weapon status the remote player sets.
        /// Gated on IsHostRunning (not IsEstablished): from the moment hosting
        /// starts the remote side is player-owned, so its AI must not launch
        /// attacks while the host waits for the player to connect.</summary>
        internal static bool HostSuppressesRemoteTfAi(ObjectBase? unit) =>
            Plugin.Instance.CfgIsHost.Value
            && Plugin.Instance.CfgPvP.Value
            && NetworkManager.Instance.IsHostRunning
            && unit != null
            && unit._taskforce != null
            && unit._taskforce == Globals._enemyTaskforce;

        /// <summary>CLIENT-side: true for a unit the local player does not own -
        /// the opposing side in PvP, the AI sides in co-op. The per-unit AI class
        /// is suppressed on the client, but Vessel/Submarine tick their OWN state
        /// machines outside it, so those units still make local decisions. Those
        /// decisions must neither execute locally nor travel upstream as if the
        /// player had made them. (Without this the client's submarine AI drove
        /// the Sprint state on the host player's sub replica - preset depth 5 +
        /// AI transit telegraph - and the order patches forwarded both to the
        /// host, which applied them: depth pinned deep, speed pinned to ~20 kts,
        /// course still free because rudder is not an order.)</summary>
        internal static bool ClientForeignUnit(ObjectBase? unit) =>
            ClientActive
            && !OrderHandler.ApplyingFromNetwork
            && !Authority.IsAllowed
            && unit != null
            && unit._taskforce != null
            && unit._taskforce != Globals._playerTaskforce;

        private static bool _defenseFlagForced;

        /// <summary>Master auto-defence kill switch: gates CIWS acquisition,
        /// auto-chaff, counterlaunch and torpedo-evasion state transitions.
        /// Re-asserted periodically; restored on disconnect.</summary>
        internal static void EnforceDefenseFlag()
        {
            if (ClientActive)
            {
                if (Globals._testIsUnitDefenseActive)
                {
                    Globals._testIsUnitDefenseActive = false;
                    if (!_defenseFlagForced)
                        Plugin.Log.LogInfo("[Suppression] Client auto-defence disabled (host-authoritative)");
                    _defenseFlagForced = true;
                }

                // Debug weapon trails (solid red root-level TrailRenderer lines) ride
                // in via the synced save's [Debug] WeaponTestMode - force them off.
                if (DM._showWeaponTrails) DM._showWeaponTrails = false;
                if (DM._weaponVisualisationMode) DM._weaponVisualisationMode = false;
            }
            else if (_defenseFlagForced)
            {
                Globals._testIsUnitDefenseActive = true;
                _defenseFlagForced = false;
                Plugin.Log.LogInfo("[Suppression] Client auto-defence restored");
            }
        }
    }

    /// <summary>Client carriers are puppets - the host owns all flight-deck ops.
    /// Suppress the deck task pump so the pending-launch queue the client mirrors
    /// from the host's FlightDeckState snapshot (FlightDeckStateApplier) stays
    /// display-only and never advances or spawns aircraft locally.</summary>
    [HarmonyPatch]
    public static class Patch_V2_FlightDeckTasks_Suppress
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(FlightDeck), "handleFlightDeckTasks");
        static bool Prefix() => !Suppression.ClientActive;
    }

    /// <summary>Companion kill-switch: handleFlightDeckTasks only drives onUpdate;
    /// state-machine TRANSITIONS are evaluated in FlightDeckTask.fixedTickStateMachine,
    /// called from FlightDeck.OnFixedUpdate which still runs client-side. Without this
    /// the mirrored queue rows advance locally (in default deck-timings mode a pending
    /// task walks into HandleReadyUpTask, whose onEnter adds its own commands - breaking
    /// FlightDeckStateApplier's LAUNCH-command index assumption - and relabels the row).</summary>
    [HarmonyPatch(typeof(FlightDeckTask), nameof(FlightDeckTask.fixedTickStateMachine))]
    public static class Patch_V2_FlightDeckTaskFsm_Suppress
    {
        static bool Prefix() => !Suppression.ClientActive;
    }

    /// <summary>Master per-unit AI kill on the client (auto-engage, carrier ops,
    /// evasion decisions, contact responses). Propulsion and sensors live in the
    /// separate _obp systems loop and keep running. NOTE: on the HOST the remote
    /// player's units keep their per-unit AI - that's what runs their SAM/PD
    /// auto-defence and contact processing, exactly like the host player's own
    /// ships; the remote player governs it with weapon status (Hold/Tight/Free).
    /// However the AI must never issue ORDERS of its own to those units (Identify/
    /// Investigate course changes, attack focus, preset attacks) - the queues that
    /// drive them are cleared before the body reads them.</summary>
    [HarmonyPatch(typeof(AI), nameof(AI.OnFixedUpdate))]
    public static class Patch_V2_AI_OnFixedUpdate_Suppress
    {
        static bool Prefix(AI __instance, ObjectBase ____baseObject)
        {
            if (Suppression.ClientActive) return false;

            if (Suppression.HostSuppressesRemoteTfAi(____baseObject))
            {
                __instance._objectsToIdentifyList.Clear();
                __instance._objectsToDestroyList.Clear();
                __instance._contactsToInvestigate.Clear();
                __instance._presetAttacks.Clear();
                __instance._objectToIdentify = null;
                __instance._objectToClassify = null;
                __instance._objectToDestroy = null;
            }
            return true;
        }
    }

    /// <summary>HOST-side PvP: carrier AUTONOMY (auto-CAP/AEW/MPA/interceptor and
    /// AI airstrike scheduling) is suppressed for the remote player's taskforce -
    /// the remote player commands their own flight ops. Manual launches are
    /// unaffected (they go through createLaunchTask, not this).</summary>
    [HarmonyPatch]
    public static class Patch_V2_RemoteTf_CarrierAi_Suppress
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(AI), "HandleCarrierFunctions");
        static bool Prefix(ObjectBase ____baseObject) =>
            !Suppression.HostSuppressesRemoteTfAi(____baseObject);
    }

    /// <summary>HOST-side PvP: the remote player's units keep their per-unit AI for
    /// DEFENCE (incoming missiles/torpedoes, hostile aircraft) but must not pick
    /// surface/sub fights on their own - the remote player decides those. Strip
    /// every non-weapon, non-air contact from the auto-engage candidate list
    /// before the AI's attack routines read it.</summary>
    public static class RemoteTfAutoEngageFilter
    {
        private static readonly System.Collections.Generic.List<ObjectBase> _strip = new();

        internal static void Apply(ObjectBase? unit,
            System.Collections.Generic.Dictionary<ObjectBase, int>? targets)
        {
            if (targets == null || targets.Count == 0) return;
            if (!Suppression.HostSuppressesRemoteTfAi(unit)) return;

            _strip.Clear();
            foreach (var kv in targets)
            {
                var t = kv.Key;
                if (t is WeaponBase) continue;          // incoming missiles/torpedoes
                if (t != null && t.IsAirUnit) continue; // aircraft/helicopters
                _strip.Add(t);
            }
            for (int i = 0; i < _strip.Count; i++)
                targets.Remove(_strip[i]);
        }
    }

    [HarmonyPatch]
    public static class Patch_V2_RemoteTf_AutoAttack_Filter
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(AI), "AutoAttackOpponentInRange");
        static void Prefix(ObjectBase ____baseObject,
            System.Collections.Generic.Dictionary<ObjectBase, int> ____possibleTargetsWithPriorities)
            => RemoteTfAutoEngageFilter.Apply(____baseObject, ____possibleTargetsWithPriorities);
    }

    [HarmonyPatch]
    public static class Patch_V2_RemoteTf_AutoGuns_Filter
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(AI), "AutoFireGunsInRange");
        static void Prefix(ObjectBase ____baseObject,
            System.Collections.Generic.Dictionary<ObjectBase, int> ____possibleTargetsWithPriorities)
            => RemoteTfAutoEngageFilter.Apply(____baseObject, ____possibleTargetsWithPriorities);
    }

    /// <summary>Mission-level AI (behaviour-tree pump: scripted spawns, third-party
    /// taskforce orders, airstrike scheduling) - host-only.</summary>
    [HarmonyPatch(typeof(AIController), nameof(AIController.OnUpdate))]
    public static class Patch_V2_AIController_Suppress
    {
        static bool Prefix() => !Suppression.ClientActive;
    }

    /// <summary>Mission end is host-decided - the client only ends when the
    /// host's MissionEnd event arrives (applied under Authority).</summary>
    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.CalculateEndMissionData))]
    public static class Patch_V2_MissionEnd_Suppress
    {
        static bool Prefix()
        {
            if (!Suppression.ClientActive) return true;
            return Authority.IsAllowed;
        }
    }

    /// <summary>No weapon collision/fuse raycasts on the client, for ANY weapon -
    /// impacts arrive as host events.</summary>
    [HarmonyPatch(typeof(WeaponBase), "CheckCollision")]
    public static class Patch_V2_CheckCollision_Suppress
    {
        static bool Prefix() => !Suppression.ClientActive;
    }

    /// <summary>Collision outcomes are host-decided. Aircraft.OnTriggerEnter is a
    /// Unity physics callback that locally explodes and destroys an aircraft whose
    /// colliders overlap a ship - on the client that's a replica-placement artifact
    /// (e.g. a freshly spawned replica near its carrier), not a real collision.
    /// Real collision kills arrive as DestroyEvents from the host.</summary>
    [HarmonyPatch(typeof(Aircraft), "OnTriggerEnter")]
    public static class Patch_V2_AircraftTrigger_Suppress
    {
        static bool Prefix() => !Suppression.ClientActive;
    }

    /// <summary>Replica weapons stay fully "launched" - the map, radar and threat
    /// lists all skip un-launched weapons - but they must not think or act:
    /// OnFixedUpdate carries motion integration, guidance, seeker, fuse and the
    /// state machine; OnUpdateEveryFrame carries the global weapon TC clamp
    /// (which on the client would spam time requests/proposals upstream) and a
    /// water-dip destruction path. WeaponReplicaDriver owns movement, effects,
    /// geo position and audio for these.</summary>
    public static class WeaponReplicaSuppression
    {
        internal static bool Skip(WeaponBase wb) =>
            Suppression.ClientActive
            && ReplicaRegistry.PolicyOf(wb) == ReplicaPolicy.KinematicWeapon;
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.OnFixedUpdate))]
    public static class Patch_V2_MissileFixedUpdate_Suppress
    {
        static bool Prefix(Missile __instance) => !WeaponReplicaSuppression.Skip(__instance);
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.OnUpdateEveryFrame))]
    public static class Patch_V2_MissileEveryFrame_Suppress
    {
        static bool Prefix(Missile __instance) => !WeaponReplicaSuppression.Skip(__instance);
    }

    [HarmonyPatch(typeof(Torpedo), nameof(Torpedo.OnFixedUpdate))]
    public static class Patch_V2_TorpedoFixedUpdate_Suppress
    {
        static bool Prefix(Torpedo __instance) => !WeaponReplicaSuppression.Skip(__instance);
    }

    [HarmonyPatch(typeof(Torpedo), nameof(Torpedo.OnUpdateEveryFrame))]
    public static class Patch_V2_TorpedoEveryFrame_Suppress
    {
        static bool Prefix(Torpedo __instance) => !WeaponReplicaSuppression.Skip(__instance);
    }

    // Bombs: KinematicWeapon replicas only - LiveLocal sonobuoys keep their sim
    [HarmonyPatch(typeof(Bomb), nameof(Bomb.OnFixedUpdate))]
    public static class Patch_V2_BombFixedUpdate_Suppress
    {
        static bool Prefix(Bomb __instance) => !WeaponReplicaSuppression.Skip(__instance);
    }

    [HarmonyPatch(typeof(Bomb), nameof(Bomb.OnUpdateEveryFrame))]
    public static class Patch_V2_BombEveryFrame_Suppress
    {
        static bool Prefix(Bomb __instance) => !WeaponReplicaSuppression.Skip(__instance);
    }

    /// <summary>Zero local damage on the client - DamageState/DestroyEvent carry
    /// the host's authoritative outcomes. (Explosion VFX are played directly by
    /// the impact handler, never through Blastzone.)</summary>
    [HarmonyPatch(typeof(Blastzone), nameof(Blastzone.CreateExplosion))]
    public static class Patch_V2_Blastzone_Suppress
    {
        static bool Prefix() => !Suppression.ClientActive;
    }

    /// <summary>CIWS never acquires or rolls intercepts on the client.</summary>
    [HarmonyPatch]
    public static class Patch_V2_CIWS_AquireTarget_Suppress
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(WeaponSystemCIWS), "AquireTarget");
        static bool Prefix() => !Suppression.ClientActive;
    }

    [HarmonyPatch]
    public static class Patch_V2_CIWS_Intercept_Suppress
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(WeaponSystemCIWS), "InterceptAirTarget");
        static bool Prefix() => !Suppression.ClientActive;
    }

    /// <summary>No client auto-chaff decisions (clouds replicate in P5; the
    /// defence-flag switch already gates most of this - belt and braces).</summary>
    [HarmonyPatch]
    public static class Patch_V2_Chaff_Suppress
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(WeaponSystemChaff), "OnUpdate");
        static bool Prefix() => !Suppression.ClientActive;
    }

    /// <summary>Replica weapons may only be destroyed by network authority -
    /// blocks any leftover local autodestruct path (water-dip, fuel, stall,
    /// self-destruct states). Non-replica weapons (live-local sonobuoys, legacy
    /// cosmetics) destroy natively.</summary>
    [HarmonyPatch(typeof(WeaponBase), nameof(WeaponBase.destroyObject))]
    public static class Patch_V2_WeaponDestroy_Guard
    {
        static bool Prefix(WeaponBase __instance)
        {
            if (!Suppression.ClientActive) return true;
            if (Authority.IsAllowed) return true;
            if (ReplicaRegistry.PolicyOf(__instance) != ReplicaPolicy.KinematicWeapon) return true;
            Telemetry.Count("v2.blockedLocalDestroy");
            return false;
        }
    }

    public static class Patch_V2_WeaponDestruction_Guard
    {
        internal static bool Allow(WeaponBase wb)
        {
            if (!Suppression.ClientActive) return true;
            if (Authority.IsAllowed) return true;
            if (ReplicaRegistry.PolicyOf(wb) != ReplicaPolicy.KinematicWeapon) return true;
            Telemetry.Count("v2.blockedLocalDestruction");
            return false;
        }
    }

    [HarmonyPatch(typeof(WeaponBase), nameof(WeaponBase.Destruction))]
    public static class Patch_V2_WeaponBaseDestruction_Guard
    {
        static bool Prefix(WeaponBase __instance) => Patch_V2_WeaponDestruction_Guard.Allow(__instance);
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.Destruction))]
    public static class Patch_V2_MissileDestruction_Guard
    {
        static bool Prefix(Missile __instance) => Patch_V2_WeaponDestruction_Guard.Allow(__instance);
    }

    /// <summary>Client gun trigger → upstream order; the host fires the real gun
    /// and the burst comes back as a cosmetic event.</summary>
    [HarmonyPatch(typeof(WeaponSystemGun), nameof(WeaponSystemGun.fire))]
    public static class Patch_V2_GunFire_Upstream
    {
        static bool Prefix(WeaponSystemGun __instance)
        {
            if (!Suppression.ClientActive) return true;
            if (Authority.IsAllowed) return true; // cosmetic playback path

            var unit = __instance._baseObject;
            if (unit == null) return false;
            int mountIdx = CaptureState.MountIndexOf(unit, __instance);
            if (mountIdx < 0) return false;

            var dir = __instance._solutionVector;
            NetworkManager.Instance.SendToServer(new Messages.PlayerOrderMessage
            {
                SourceEntityId = unit.UniqueID,
                Order          = Messages.OrderType.ManualGunFire,
                Heading        = mountIdx,
                TargetX        = dir.x,
                TargetY        = dir.y,
                TargetZ        = dir.z,
                AmmoId         = __instance._ammoForEngage?._ap?._ammunitionFileName ?? "",
            });
            Telemetry.Count("v2.clientGunFireUpstream");
            return false;
        }
    }

    /// <summary>The client never creates units on its own - aircraft arrive as
    /// replicas via EntitySpawn. Scene/save loads still build the world.</summary>
    [HarmonyPatch(typeof(ObjectsManager), nameof(ObjectsManager.createAircraft))]
    public static class Patch_V2_CreateAircraft_Guard
    {
        static bool Prefix()
        {
            if (!Suppression.ClientActive) return true;
            if (Authority.IsAllowed) return true;
            if (SessionManager.SceneLoading) return true;
            Telemetry.Count("v2.blockedClientUnitSpawn");
            Plugin.Log.LogWarning("[Suppression] Blocked client-local aircraft creation (host-authoritative)");
            return false;
        }
    }

    [HarmonyPatch(typeof(ObjectsManager), nameof(ObjectsManager.createHelicopter))]
    public static class Patch_V2_CreateHelicopter_Guard
    {
        static bool Prefix()
        {
            if (!Suppression.ClientActive) return true;
            if (Authority.IsAllowed) return true;
            if (SessionManager.SceneLoading) return true;
            Telemetry.Count("v2.blockedClientUnitSpawn");
            Plugin.Log.LogWarning("[Suppression] Blocked client-local helicopter creation (host-authoritative)");
            return false;
        }
    }

    /// <summary>Canary: any weapon launch on the client outside network authority
    /// is a suppression leak - block it, kill the object, count it loudly.</summary>
    [HarmonyPatch(typeof(WeaponBase), nameof(WeaponBase.CommonLaunchSettings))]
    public static class Patch_V2_LaunchCanary
    {
        static bool Prefix(WeaponBase __instance)
        {
            if (!Suppression.ClientActive) return true;
            if (Authority.IsAllowed) return true;
            if (CaptureState.WeaponClassOf(__instance) == null) return true; // chaff/noisemaker etc. stay local for now

            Telemetry.Count("v2.canaryBlockedLaunch");
            Plugin.Log.LogError($"[Canary] Blocked un-authorized client weapon launch: " +
                $"{__instance.name} ({__instance._ap?._ammunitionFileName}) — suppression leak, report this");
            __instance.gameObject.SetActive(false);
            return false;
        }
    }

    /// <summary>CLIENT-side: formation flight physics ends every FixedUpdate by
    /// copying the LEADER's commanded altitude and speed onto the wingman
    /// (FormationFlightPhysics.OnFixedUpdate). On a replica that fights the host
    /// stream, which carries each wingman's OWN DesiredAltitude: the stream sets
    /// the wingman's value, local formation physics overwrites it with the
    /// leader's on the next physics tick, and the stream only re-asserts on change
    /// or on the idle heartbeat - so a wingman ordered low under a leader at 20k
    /// visibly oscillates between the two, and after rejoining sits at whatever
    /// the leader commanded rather than what the host has it doing.
    ///
    /// The host is authoritative for both values, so they are preserved across the
    /// call. Everything else formation physics does (station keeping, g-load,
    /// thrust) is left alone - it only drives local motion, which the replica
    /// driver corrects anyway.
    ///
    /// Note the speed restore has two cases: the game REPLACES SpeedCommand when it
    /// is not already a ConstantMach, but MUTATES it in place when it is - so
    /// holding the reference alone does not preserve the value.</summary>
    [HarmonyPatch]
    public static class Patch_V2_Client_FormationFlight_KeepOwnCommand
    {
        static MethodBase? TargetMethod()
        {
            var type = AccessTools.TypeByName("SeaPower.FormationFlightPhysics");
            var method = type == null ? null : AccessTools.Method(type, "OnFixedUpdate");
            if (method == null)
                Plugin.Log.LogWarning("[Suppression] FormationFlightPhysics.OnFixedUpdate not found - " +
                    "client wingmen will follow the leader's altitude/speed instead of the host's");
            return method;
        }

        static void Prefix(ObjectBase ____ac, out (bool held, float alt, ISpeedCommand? cmd, float mach) __state)
        {
            __state = default;
            if (!Suppression.ClientActive || ____ac == null) return;
            var cmd = ____ac.SpeedCommand?.Value;
            __state = (true, ____ac.DesiredAltitude.Value, cmd, cmd?.SpeedInMach ?? 0f);
        }

        static void Postfix(ObjectBase ____ac, (bool held, float alt, ISpeedCommand? cmd, float mach) __state)
        {
            if (!__state.held || ____ac == null) return;

            ____ac.DesiredAltitude.Value = __state.alt;

            if (__state.cmd == null) return;
            if (!ReferenceEquals(____ac.SpeedCommand.Value, __state.cmd))
                ____ac.SpeedCommand.Value = __state.cmd;          // ours was swapped out
            else if (__state.cmd is ConstantMach cm)
                cm.SetSpeedInMach(__state.mach);                  // ours was mutated in place
        }
    }
}
