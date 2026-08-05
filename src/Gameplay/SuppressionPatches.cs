using System.Reflection;
using HarmonyLib;
using SeaPower;
using SeaPowerAI;
using SeapowerMultiplayer.Messages;
using SubmarineStates;
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
            unit != null && HostSuppressesRemoteTfAi(unit._taskforce);

        /// <summary>Taskforce-level form of the same test, for AI that runs per
        /// TASKFORCE rather than per unit (Taskforce.CheckAI).</summary>
        internal static bool HostSuppressesRemoteTfAi(Taskforce? tf) =>
            Plugin.Instance.CfgIsHost.Value
            && Plugin.Instance.CfgPvP.Value
            && NetworkManager.Instance.IsHostRunning
            && tf != null
            && tf == Globals._enemyTaskforce;

        /// <summary>HOST-side PvP: the remote player's fleet, tested WITHOUT requiring
        /// the transport to be up. Spawn-time stamps run while the mission loads, which
        /// on the host can be before it starts listening - but from the host's own
        /// configuration the enemy taskforce is the other player's fleet either way.
        /// Use this ONLY for those load-time corrections; anything that acts during a
        /// live battle should ask <see cref="HostSuppressesRemoteTfAi(ObjectBase)"/>,
        /// which additionally requires a session.</summary>
        internal static bool RemotePlayerFleet(ObjectBase? unit) =>
            Plugin.Instance.CfgIsHost.Value
            && Plugin.Instance.CfgPvP.Value
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

        private static (float missileBonus, float missileReduction,
                        float gunsBonus, float gunsReduction,
                        float ciwsBonus, float ciwsReduction)? _interceptHandicap;

        /// <summary>HOST-side PvP: turn off the difficulty handicap on interception
        /// rolls, which is keyed on IsPlayerObject and therefore lands entirely on the
        /// host's side of the table.
        ///
        /// Every interception path applies the same pair: a BONUS when the shooter is a
        /// player object and a REDUCTION when the target is one. On the host in PvP the
        /// remote player's fleet is neither, so the host collects both halves - their
        /// shots are boosted AND incoming shots at them are cut, while the guest gets
        /// neither in either direction. It is invisible to both players, and it is not a
        /// desync: the host is authoritative, so this is simply the number both machines
        /// then agree on. OptionsManager.SetDifficulty scales it 0.5 / 0.3 / 0.2 / 0.1 /
        /// 0 across difficulties 0-4, so at anything but the hardest setting the host is
        /// playing a materially different game.
        ///
        /// Zeroing the six Globals rather than patching the call sites: these are read at
        /// seven points across Utils, Blastzone (x2), Projectile and WeaponSystemCIWS,
        /// every one of them mid-method behind a local - so per-site correction means
        /// four transpilers, while the values themselves are plain statics with a single
        /// writer. Zero is also the only setting that is symmetric in BOTH directions and
        /// against third-party AI as well, which granting the remote fleet player status
        /// would not be.
        ///
        /// The trade: a PvP session rolls interception at the difficulty-4 values whatever
        /// difficulty is selected. That is the handicap for playing against the computer,
        /// and in PvP there is no computer to be handicapped against.
        ///
        /// Re-asserted every frame and restored on teardown, like the defence flag above.
        /// The capture re-reads whenever anything non-zero is present so that a difficulty
        /// changed mid-session is what gets handed back, not the value at session start.</summary>
        internal static void EnforceInterceptSymmetry()
        {
            bool hostPvpSession = Plugin.Instance.CfgIsHost.Value
                && Plugin.Instance.CfgPvP.Value
                && NetworkManager.Instance.IsHostRunning;

            if (hostPvpSession)
            {
                if (Globals._missileInterceptChanceBonus != 0f || Globals._missileInterceptChanceReduction != 0f
                    || Globals._gunsInterceptChanceBonus != 0f || Globals._gunsInterceptChanceReduction != 0f
                    || Globals._ciwsInterceptChanceBonus != 0f || Globals._ciwsInterceptChanceReduction != 0f)
                {
                    _interceptHandicap = (
                        Globals._missileInterceptChanceBonus, Globals._missileInterceptChanceReduction,
                        Globals._gunsInterceptChanceBonus, Globals._gunsInterceptChanceReduction,
                        Globals._ciwsInterceptChanceBonus, Globals._ciwsInterceptChanceReduction);
                    Plugin.Log.LogInfo("[Suppression] PvP interception handicap cleared (missile " +
                        $"+{_interceptHandicap.Value.missileBonus:0.##}/-{_interceptHandicap.Value.missileReduction:0.##}, " +
                        $"guns +{_interceptHandicap.Value.gunsBonus:0.##}/-{_interceptHandicap.Value.gunsReduction:0.##}, " +
                        $"CIWS +{_interceptHandicap.Value.ciwsBonus:0.##}/-{_interceptHandicap.Value.ciwsReduction:0.##})");
                }

                Globals._missileInterceptChanceBonus = 0f;
                Globals._missileInterceptChanceReduction = 0f;
                Globals._gunsInterceptChanceBonus = 0f;
                Globals._gunsInterceptChanceReduction = 0f;
                Globals._ciwsInterceptChanceBonus = 0f;
                Globals._ciwsInterceptChanceReduction = 0f;
            }
            else if (_interceptHandicap.HasValue)
            {
                var saved = _interceptHandicap.Value;
                Globals._missileInterceptChanceBonus = saved.missileBonus;
                Globals._missileInterceptChanceReduction = saved.missileReduction;
                Globals._gunsInterceptChanceBonus = saved.gunsBonus;
                Globals._gunsInterceptChanceReduction = saved.gunsReduction;
                Globals._ciwsInterceptChanceBonus = saved.ciwsBonus;
                Globals._ciwsInterceptChanceReduction = saved.ciwsReduction;
                _interceptHandicap = null;
                Plugin.Log.LogInfo("[Suppression] PvP interception handicap restored");
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
    /// separate _obp systems loop and keep running.
    ///
    /// On the HOST the remote player's units keep the AI a PLAYER's own units have
    /// in single player: weapon-status-driven engagement (Hold/Tight/Free) and
    /// auto-defence. What they must not keep is opponent behaviour - piloting
    /// themselves around the map. See <see cref="RemoteTfSelfPiloting"/> for the
    /// routines that are cut.
    ///
    /// Only the queues that drive NAVIGATION are cleared here. Clearing
    /// _objectsToDestroyList used to be part of this and never worked: AI
    /// .OnFixedUpdate calls AIDetection() and ProcessContacts() further down the
    /// SAME call, both of which refill it, and then acts on it - so a prefix clear
    /// was a no-op for everything except _objectsToIdentifyList, which happens to
    /// be read before AIDetection. It is also no longer wanted: destroying targets
    /// per weapon status is exactly the AI the remote player is entitled to.</summary>
    [HarmonyPatch(typeof(AI), nameof(AI.OnFixedUpdate))]
    public static class Patch_V2_AI_OnFixedUpdate_Suppress
    {
        static bool Prefix(AI __instance, ObjectBase ____baseObject)
        {
            if (Suppression.ClientActive) return false;

            if (Suppression.HostSuppressesRemoteTfAi(____baseObject))
            {
                // Identify/Investigate/classify are course changes wearing a
                // contact-handling hat - the Identify branch at the top of the body
                // calls setOrder() outright. Preset attacks are mission-scripted,
                // not weapon-status engagement.
                __instance._objectsToIdentifyList.Clear();
                __instance._contactsToInvestigate.Clear();
                __instance._presetAttacks.Clear();
                __instance._objectToIdentify = null;
                __instance._objectToClassify = null;
            }
            return true;
        }
    }

    /// <summary>HOST-side PvP: the routines inside the remote player's per-unit AI
    /// that make it fly/steer itself. Each writes a waypoint, a retreat, or an
    /// evasion state the unit's own state machine then manoeuvres on - the "units
    /// setting their own speed and direction" the remote player never asked for.
    /// Engagement is deliberately untouched: target selection, launcher warm-up,
    /// gun and missile auto-fire all still run, governed by weapon status exactly
    /// as they do for the host's own ships.</summary>
    public static class RemoteTfSelfPiloting
    {
        internal static bool Allow(ObjectBase? baseObject) =>
            !Suppression.HostSuppressesRemoteTfAi(baseObject);

        /// <summary>These are private AI methods resolved by name. If a game update
        /// renames one, say so - otherwise the remote player's units quietly start
        /// piloting themselves again with nothing in the log to point at.</summary>
        internal static MethodBase? Target(string name)
        {
            var m = AccessTools.Method(typeof(AI), name);
            if (m == null)
                Plugin.Log.LogWarning($"[Suppression] AI.{name} not found - the remote " +
                    "player's units will keep this piece of self-piloting AI");
            return m;
        }
    }

    /// <summary>Submarine evade: sets _clearDatumFrom/_clearDatumUntil, which the
    /// sub's state machine runs away on. (UpdateClearDatum is left alone - it only
    /// EXPIRES the state, and blocking it would strand a sub in it.)</summary>
    [HarmonyPatch]
    public static class Patch_V2_RemoteTf_ASWEvasion_Suppress
    {
        static MethodBase? TargetMethod() => RemoteTfSelfPiloting.Target("UpdateASWEvasion");
        static bool Prefix(ObjectBase ____baseObject) => RemoteTfSelfPiloting.Allow(____baseObject);
    }

    /// <summary>Submarine hunt: picks a faded contact and manoeuvres to regain it.</summary>
    [HarmonyPatch]
    public static class Patch_V2_RemoteTf_ReacquireContacts_Suppress
    {
        static MethodBase? TargetMethod() => RemoteTfSelfPiloting.Target("UpdateReacquireFadedContacts");
        static bool Prefix(ObjectBase ____baseObject) => RemoteTfSelfPiloting.Allow(____baseObject);
    }

    /// <summary>Jammer station-keeping: writes "JammerIngress"/"JammerHold"
    /// waypoints straight onto the aircraft. (UpdateAutoJamming is left alone -
    /// it only switches emitters, which is auto-defence.)</summary>
    [HarmonyPatch]
    public static class Patch_V2_RemoteTf_JammerPositioning_Suppress
    {
        static MethodBase? TargetMethod() => RemoteTfSelfPiloting.Target("UpdateJammerPositioning");
        static bool Prefix(ObjectBase ____baseObject) => RemoteTfSelfPiloting.Allow(____baseObject);
    }

    /// <summary>Morale.AssignRetreatWaypoint - a missile boat that has shot its
    /// bolt turns and runs. The remote player decides when to withdraw.</summary>
    [HarmonyPatch]
    public static class Patch_V2_RemoteTf_RetreatWhenExpended_Suppress
    {
        static MethodBase? TargetMethod() => RemoteTfSelfPiloting.Target("CheckRetreatAfterWeaponsExpended");
        static bool Prefix(ObjectBase ____baseObject) => RemoteTfSelfPiloting.Allow(____baseObject);
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

    // NOTE: RemoteTfAutoEngageFilter (and its AutoAttackOpponentInRange /
    // AutoFireGunsInRange prefixes) removed. It stripped every non-weapon,
    // non-air contact from the remote player's auto-engage list, so their ships
    // would not shoot at surface or subsurface targets even at Weapons Free -
    // narrower than the AI a player's own units have in single player. Weapon
    // status is the intended control for this, not taskforce ownership.

    /// <summary>HOST-side PvP: the submarine cruise-missile pipeline.
    ///
    /// This is the one part of the offensive AI that weapon status cannot govern. Every
    /// pass in it opens with the same test - <c>if (!DM._subAIAppliesToPlayer &amp;&amp;
    /// _baseObject.IsPlayerObject) return;</c> - so the game NEVER runs it for a
    /// player's own boats, at any weapon status. On the host the remote player's
    /// submarines are not player objects, so it ran: their boats picked classified
    /// contacts off the taskforce plot and volleyed cruise missiles at full weapon
    /// range while their owner sat at weapons tight. Skipping these IS the player-fleet
    /// behaviour, not a restriction on top of it.
    ///
    /// The three target passes CLEAR their list and then conditionally refill it (the
    /// auto pass clears it in exactly the branch a player's own boat takes), so a bare
    /// prefix-skip would strand whatever the list already held and keep authorising
    /// fire from it. Each one clears before refusing.</summary>
    [HarmonyPatch(typeof(AI), nameof(AI.UpdateAutoCruiseMissileTargets))]
    public static class Patch_V2_RemoteTf_AutoCruiseTargets_Suppress
    {
        static bool Prefix(AI __instance, ObjectBase ____baseObject)
        {
            if (!Suppression.HostSuppressesRemoteTfAi(____baseObject)) return true;
            __instance._autoSelectedMissileTargets.Clear();
            return false;
        }
    }

    [HarmonyPatch(typeof(AI), nameof(AI.UpdateTaskforceCuedMissileTargets))]
    public static class Patch_V2_RemoteTf_CuedMissileTargets_Suppress
    {
        static bool Prefix(AI __instance, ObjectBase ____baseObject)
        {
            if (!Suppression.HostSuppressesRemoteTfAi(____baseObject)) return true;
            __instance._taskforceCuedMissileTargets.Clear();
            return false;
        }
    }

    /// <summary>A submarine that believes it has been spotted whitelists every closing
    /// ship for missile fire. Same shape as the two above - the list is cleared first,
    /// so refusing without clearing would leave the whitelist standing.</summary>
    [HarmonyPatch(typeof(AI), nameof(AI.UpdateDefensiveMissileTargets))]
    public static class Patch_V2_RemoteTf_DefensiveMissileTargets_Suppress
    {
        static bool Prefix(AI __instance, ObjectBase ____baseObject)
        {
            if (!Suppression.HostSuppressesRemoteTfAi(____baseObject)) return true;
            __instance._defensiveMissileTargets.Clear();
            return false;
        }
    }

    /// <summary>The two firing passes the lists above feed. Nothing to clear - they
    /// only act.</summary>
    [HarmonyPatch(typeof(AI), nameof(AI.FireTaskforceCuedMissiles))]
    public static class Patch_V2_RemoteTf_FireCuedMissiles_Suppress
    {
        static bool Prefix(ObjectBase ____baseObject) =>
            !Suppression.HostSuppressesRemoteTfAi(____baseObject);
    }

    [HarmonyPatch(typeof(AI), nameof(AI.FireSubmarineMissileVolley))]
    public static class Patch_V2_RemoteTf_FireMissileVolley_Suppress
    {
        static bool Prefix(ObjectBase ____baseObject) =>
            !Suppression.HostSuppressesRemoteTfAi(____baseObject);
    }

    /// <summary>HOST-side PvP: the two submarine crew behaviours that are NOT states.
    ///
    /// Both are plain calls in Submarine.OnFixedUpdate (:523 and :527), gated on
    /// <c>!IsPlayerObject</c> - so the game denies them to a player's own boat and runs
    /// them on the remote player's. Skipping them is parity, not a restriction.
    ///
    /// ApplyWirePreservationLimits is the one that hurts: it overwrites SpeedCommand
    /// with a torpedo-wire speed limit AND raises DesiredAltitude to a minimum safe
    /// depth, every fixed tick, for as long as the boat has torpedoes on the wire. A
    /// relayed speed or depth order lands, gets silently overwritten before the player
    /// sees it move, and reads as "my order did nothing" with no trace on either
    /// machine. HoldCuedFiringConnection is the same shape, holding the boat shallow to
    /// keep a cued firing connection.</summary>
    [HarmonyPatch(typeof(Submarine), "ApplyWirePreservationLimits")]
    public static class Patch_V2_RemoteTf_WireLimits_Suppress
    {
        static bool Prefix(Submarine __instance) =>
            !Suppression.HostSuppressesRemoteTfAi(__instance);
    }

    [HarmonyPatch(typeof(Submarine), "HoldCuedFiringConnection")]
    public static class Patch_V2_RemoteTf_CuedFiringHold_Suppress
    {
        static bool Prefix(Submarine __instance) =>
            !Suppression.HostSuppressesRemoteTfAi(__instance);
    }

    /// <summary>HOST-side PvP: give the remote player's aircraft the answer their owner
    /// gets. CanUseAIStates reads <c>IsPlayerObject ? Globals.useExperimentalAircraftAI
    /// : true</c>, and it is the sole gate on the two AirborneEarlyWarningLine
    /// transitions - so on the host the remote player's AEW aircraft fly a search
    /// pattern their owner's machine would never have started.</summary>
    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.CanUseAIStates), MethodType.Getter)]
    public static class Patch_V2_RemoteTf_AircraftAiStates
    {
        static void Postfix(Aircraft __instance, ref bool __result)
        {
            if (!__result) return;
            if (!Suppression.HostSuppressesRemoteTfAi(__instance)) return;
            __result = Globals.useExperimentalAircraftAI;
        }
    }

    /// <summary>HOST-side PvP: keep the remote player's units out of the crew's
    /// automatic TORPEDO EVASION.
    ///
    /// This is not a restriction, it is parity. The transition is registered as
    ///
    ///   AtAny(_state_torpedoEvasion, () =&gt; Globals._testIsUnitDefenseActive
    ///        &amp;&amp; ThreatingTorpedo != null
    ///        &amp;&amp; (DM._subAIAppliesToPlayer || !IsPlayerObject)
    ///        &amp;&amp; CurrentState.Priority &gt; _state_torpedoEvasion.Priority);
    ///
    /// (Submarine.cs:248), and DM._subAIAppliesToPlayer is off by default - so in single
    /// player a player's own boat NEVER enters this state and the player evades the
    /// torpedo themselves. On the host the remote player's submarines are not player
    /// objects, so the crew took the boat: depth, telegraph and heading all driven by
    /// TorpedoEvasion, with "Evading Torpedo" replicated to its owner through
    /// UnitStatusManager while their own controls did nothing.
    ///
    /// SURFACE SHIPS TOO. There are two unrelated classes with this name -
    /// SubmarineStates.TorpedoEvasion (field _submarine) and VesselStates.TorpedoEvasion
    /// (field _vessel) - and Vessel.cs:154 registers the vessel one through this same
    /// addAnyTransition path under an identical (DM._subAIAppliesToPlayer ||
    /// !IsPlayerObject) gate. Matching only the submarine type left the remote player's
    /// entire surface fleet still evading on its own. Both are handled here; a missing
    /// field on one does not disable the other.
    ///
    /// Wrapping the PREDICATE rather than blocking the state: a state that is entered
    /// and then refused has no way back out (TorpedoEvasion.IsFinished is set by its own
    /// update, which we would also have to skip), so the boat would strand in it. The
    /// registration runs once per submarine in initStates and the added test is
    /// evaluated per tick, so it costs nothing and follows the session in and out.
    ///
    /// Automatic noisemaker launches go with it - they are part of this state, and a
    /// player's own boat does not get them either. The separate auto-defence path that
    /// Globals._testIsUnitDefenseActive gates is untouched.</summary>
    [HarmonyPatch(typeof(StateMachine), nameof(StateMachine.addAnyTransition))]
    public static class Patch_V2_RemoteTf_TorpedoEvasion_Suppress
    {
        private static readonly AccessTools.FieldRef<SubmarineStates.TorpedoEvasion, Submarine>? _subRef =
            AccessTools.FieldRefAccess<SubmarineStates.TorpedoEvasion, Submarine>("_submarine");

        private static readonly AccessTools.FieldRef<VesselStates.TorpedoEvasion, Vessel>? _vesselRef =
            AccessTools.FieldRefAccess<VesselStates.TorpedoEvasion, Vessel>("_vessel");

        static void Prefix(IState state, ref System.Func<bool> predicate)
        {
            if (predicate == null) return;

            // Resolved per registration, evaluated per tick - the unit is read inside
            // the wrapped predicate so it follows the session in and out.
            System.Func<ObjectBase?> owner;
            if (state is SubmarineStates.TorpedoEvasion sub)
            {
                if (_subRef == null)
                {
                    Plugin.Log.LogWarning("[Suppression] SubmarineStates.TorpedoEvasion._submarine not found - the " +
                                          "remote player's submarines will keep evading torpedoes on their own");
                    return;
                }
                owner = () => _subRef(sub);
            }
            else if (state is VesselStates.TorpedoEvasion vessel)
            {
                if (_vesselRef == null)
                {
                    Plugin.Log.LogWarning("[Suppression] VesselStates.TorpedoEvasion._vessel not found - the " +
                                          "remote player's surface ships will keep evading torpedoes on their own");
                    return;
                }
                owner = () => _vesselRef(vessel);
            }
            else return;

            var inner = predicate;
            predicate = () => inner() && !Suppression.HostSuppressesRemoteTfAi(owner());
        }
    }

    /// <summary>HOST-side PvP: the remote player's SUBMARINE state machine.
    ///
    /// This is the boat piloting itself - sprinting, drifting, clearing baffles, going
    /// to periscope depth, snorkelling - and none of it goes through AI.OnFixedUpdate,
    /// so none of the AI suppression above ever touched it. Submarine.initStates
    /// registers the transitions directly on the StateMachine (Submarine.cs:197-271),
    /// and each one is gated on <c>(DM._subAIAppliesToPlayer || !IsPlayerObject)</c>
    /// with _subAIAppliesToPlayer off by default - so the game NEVER runs these for a
    /// player's own boat. On the host the remote player's submarine is not a player
    /// object, so all of it ran: their boat picked its own speed, depth and heading
    /// and raised masts, while its owner's controls fought it over the wire.
    ///
    /// Skipping them IS the player-boat behaviour, not a restriction on top of it.
    ///
    /// Two directions are needed, because the game expresses "this is a player's boat"
    /// twice with opposite polarity:
    ///
    ///  - ENTRY into an AI state is gated on !IsPlayerObject, so it must be BLOCKED.
    ///    SelectMovementType is the only door into the whole Sprint / Drift /
    ///    ClearBaffles / GoingToPeriscopeDepth cluster (:210-219, every internal
    ///    transition originates there), so gating that one closes all five.
    ///
    ///  - The ESCAPES from that cluster back to Default (:220-224) are gated on
    ///    IsPlayerObject being TRUE, so they never fire for the remote boat. A boat
    ///    already inside the cluster when hosting starts - the AI ran freely before
    ///    the session - would strand there once re-entry is blocked. Those are FORCED
    ///    instead, which is exactly the transition a player's own boat gets.
    ///
    /// Left alone deliberately: Loitering, PlayerOverride, PerformingAirOps,
    /// AvoidingCollision, Aligning and EmergencySurface are not gated on ownership -
    /// a player's own boat uses them too. CounterLaunch and TorpedoEvasion are
    /// auto-defence, handled by their own patch above.</summary>
    public static class RemoteTfSubStates
    {
        private static readonly System.Collections.Generic.HashSet<System.Type> AiEntry = new()
        {
            typeof(SubmarineStates.SelectMovementType),   // door to Sprint/Drift/ClearBaffles/PeriscopeDepth
            typeof(SubmarineStates.BuildContactSolution),
            typeof(SubmarineStates.IdentifyContact),
            typeof(SubmarineStates.ClassifyContact),
            typeof(SubmarineStates.Snorkelling),
            typeof(SubmarineStates.ClearingDatum),
            typeof(SubmarineStates.GuidingMissiles),
            typeof(SubmarineStates.OpeningToFiringRange),
            typeof(SubmarineStates.ReacquireContact),
        };

        private static readonly System.Collections.Generic.HashSet<System.Type> MovementCluster = new()
        {
            typeof(SubmarineStates.Sprint),
            typeof(SubmarineStates.Drift),
            typeof(SubmarineStates.ClearBafflesTurningBack),
            typeof(SubmarineStates.ClearBafflesListening),
            typeof(SubmarineStates.GoingToPeriscopeDepth),
        };

        /// <summary>Every one of these states holds its own <c>private Submarine
        /// _submarine</c>, assigned in the constructor before initStates registers any
        /// transition - so it is resolved ONCE here and captured by the predicate,
        /// keeping the per-tick path free of reflection.</summary>
        private static Submarine? OwnerOf(IState? state)
        {
            if (state == null) return null;
            var field = AccessTools.Field(state.GetType(), "_submarine");
            if (field == null)
            {
                Plugin.Log.LogWarning($"[Suppression] {state.GetType().Name}._submarine not found - " +
                    "the remote player's submarines will keep this piece of self-piloting AI");
                return null;
            }
            return field.GetValue(state) as Submarine;
        }

        internal static void GateEntry(IState? to, ref System.Func<bool> predicate)
        {
            if (predicate == null || to == null || !AiEntry.Contains(to.GetType())) return;
            var sub = OwnerOf(to);
            if (sub == null) return;

            var inner = predicate;
            predicate = () => inner() && !Suppression.HostSuppressesRemoteTfAi(sub);
        }

        internal static void ForceEscape(IState? from, IState? to, ref System.Func<bool> predicate)
        {
            if (predicate == null || from == null || !(to is SubmarineStates.Default)) return;
            if (!MovementCluster.Contains(from.GetType())) return;
            var sub = OwnerOf(from);
            if (sub == null) return;

            var inner = predicate;
            predicate = () => inner() || Suppression.HostSuppressesRemoteTfAi(sub);
        }
    }

    /// <summary>The AtAny half (Submarine.cs:254-268). Shares addAnyTransition with the
    /// torpedo-evasion patch above; Harmony runs both prefixes and each wraps the
    /// predicate it owns.</summary>
    [HarmonyPatch(typeof(StateMachine), nameof(StateMachine.addAnyTransition))]
    public static class Patch_V2_RemoteTf_SubStates_Any
    {
        static void Prefix(IState state, ref System.Func<bool> predicate)
            => RemoteTfSubStates.GateEntry(state, ref predicate);
    }

    /// <summary>The directed half: the door into the movement cluster (:210, plus the
    /// :214/:217/:219 loop-backs) and the escapes out of it (:220-224).</summary>
    [HarmonyPatch(typeof(StateMachine), nameof(StateMachine.addTransition))]
    public static class Patch_V2_RemoteTf_SubStates_Directed
    {
        static void Prefix(IState from, IState to, ref System.Func<bool> predicate)
        {
            RemoteTfSubStates.GateEntry(to, ref predicate);
            RemoteTfSubStates.ForceEscape(from, to, ref predicate);
        }
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

    /// <summary>Taskforce-level AI assignment sweep (10 s timer in Taskforce.OnUpdate).
    /// CheckVIDAssignment / CheckASWAssignment hand out investigate orders to every
    /// non-player taskforce - which in PvP includes the remote player's, so the host
    /// kept tasking the guest's units. The VID branch is the worst of it: for a
    /// helicopter it calls setOrder(Order.Type.Identify) WITHOUT registering in the
    /// contact's _enemiesInvestigatingThisUnit list, which is the only thing the sweep
    /// checks before re-assigning - so the order came back every 10 s and the guest
    /// could not call their helicopter off. Genuine AI sides are untouched: the gate
    /// is host + PvP + the remote player's taskforce.</summary>
    [HarmonyPatch(typeof(Taskforce), nameof(Taskforce.CheckAI))]
    public static class Patch_V2_RemoteTf_TaskforceAi_Suppress
    {
        static bool Prefix(Taskforce __instance) =>
            !Suppression.HostSuppressesRemoteTfAi(__instance);
    }

    /// <summary>Crew alert reaction. Reached from ObjectBase.CalculateIncomingThreats
    /// (via ObjectBase.OnUpdate, NOT AI.OnFixedUpdate - so the AI suppression above
    /// never covered it) for any taskforce that is not the local player's. It goes
    /// active on every sensor, forces _weaponStatus to Free by writing the field
    /// directly - bypassing SetWeaponStatus and therefore its sync, so the two sides
    /// silently disagree about the guest's weapon status - and force-alerts every
    /// taskforce unit within 10 nmi.
    ///
    /// Both sides need this, for mirrored reasons:
    ///  - HOST, PvP: the remote player's units are theirs to command.
    ///  - CLIENT: the remote player's fleet is a non-player taskforce here, so the
    ///    client's own copy of this ran on their replicas and flipped sonars on. The
    ///    IsActive subscription then relayed that upstream as a SensorToggle order and
    ///    the host's real ships started transmitting.
    ///
    /// Auto-defence is unaffected: _onAlert only gates the sensor auto-off timers and
    /// the alert expiry, nothing weapons-related.</summary>
    [HarmonyPatch(typeof(AI), nameof(AI.CheckForRaiseAlert))]
    public static class Patch_V2_RaiseAlert_Suppress
    {
        static bool Prefix(ObjectBase ____baseObject) =>
            !Suppression.HostSuppressesRemoteTfAi(____baseObject)
            && !Suppression.ClientForeignUnit(____baseObject);
    }

    /// <summary>HOST-side PvP: undo the game's "the other side is AI, so it is weapons
    /// free" stamp on the remote player's fleet.
    ///
    /// SceneCreator reads WeaponStatus from the mission ini - player side defaulting
    /// Tight, enemy side Free - and then ends unconditionally with
    /// <c>if (!pvKey.Contains(PlayerSideName)) _weaponStatus = Free;</c>. In single
    /// player that is just the enemy AI's posture. In save-swap PvP it means the HOST's
    /// copy of the other player's entire fleet comes up weapons free whatever its owner
    /// set, and everything downstream then behaves correctly on a false premise: the
    /// auto-engage passes we deliberately leave running read the forced Free and open
    /// fire at full weapon range while the owner is on weapons tight.
    ///
    /// Tight is the player-side default, and the player side is what this fleet is on
    /// its owner's machine (only <c>[Mission] PlayerTaskforce/EnemyTaskforce</c> are
    /// swapped in the client's save - unit sections keep their names and values - so the
    /// guest reads these same sections through the PLAYER-side path, whose default is
    /// Tight and which has no forced Free).
    ///
    /// Only a value of exactly Free is corrected. The force is the sole writer of Free
    /// here, so anything else - notably an authored OverrideWeaponStatus of Hold or
    /// Tight, which the reader applies after the force - is a deliberate choice and is
    /// left alone. A section that authored Free for that side is indistinguishable from
    /// a forced one without re-reading the ini, and re-reading it means referencing the
    /// game's UI assembly for one lookup; the miss errs toward not shooting, which is
    /// the safe direction and one click for the owner to undo.
    ///
    /// Gated on RemotePlayerFleet, not HostSuppressesRemoteTfAi: this runs during the
    /// mission load, which may precede the host starting to listen.</summary>
    [HarmonyPatch(typeof(SceneCreator), "SetAdditionalParameters")]
    public static class Patch_V2_RemoteTf_SpawnWeaponStatus
    {
        static void Postfix(ObjectBase unit)
        {
            if (unit == null || unit._weaponStatus != ObjectBase.WeaponStatus.Free) return;
            if (!Suppression.RemotePlayerFleet(unit)) return;

            unit._weaponStatus = ObjectBase.WeaponStatus.Tight;
            Plugin.Log.LogInfo($"[Suppression] Remote fleet weapon status: {unit.getUIDAndName()} " +
                               "Free -> Tight (undoing the enemy-side spawn stamp)");
        }
    }

    /// <summary>The same stamp, per launch: FlightDeck.launchVehicle writes Free into
    /// every aircraft it puts up under <c>if (!_baseObject.IsPlayerObject)</c>. A
    /// player's own deck launches tight; the deck this trips on is the other player's
    /// carrier as the host sees it.
    ///
    /// Tight rather than a recomputed value - a carrier-launched aircraft has no ini
    /// section of its own to read a status from, and tight is where a player's own
    /// launches start. If its owner wants them free they say so, and the SetWeaponStatus
    /// relay carries that across.</summary>
    [HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.launchVehicle))]
    public static class Patch_V2_RemoteTf_LaunchWeaponStatus
    {
        static void Postfix(FlightDeck __instance, ObjectBase __result)
        {
            if (__result == null) return;
            if (!Suppression.RemotePlayerFleet(__instance._baseObject)) return;
            if (__result._weaponStatus == ObjectBase.WeaponStatus.Tight) return;

            __result._weaponStatus = ObjectBase.WeaponStatus.Tight;
        }
    }

    /// <summary>The same stamp again, on going Winchester. AircraftStates.Winchester
    /// .onEnter splits on ownership twice:
    ///
    ///   if (IsPlayerObject)  _checkForWinchester = false;
    ///   if (!IsPlayerObject) _weaponStatus = Hold;
    ///   else if (_weaponStatus == Free) _weaponStatus = Tight;
    ///
    /// so a player's own out-of-ordnance aircraft drops Free to Tight and stops
    /// re-testing, while the host's copy of the remote player's aircraft is forced to
    /// HOLD - which will not fire even in self-defence - and keeps re-entering. Like
    /// the two stamps above it writes the field directly rather than going through
    /// SetWeaponStatus, so it never syncs and the two machines silently disagree about
    /// the guest's weapon status.
    ///
    /// Unlike those two this is not a fixed value: the player-side result depends on
    /// the status the aircraft had on entry, which onEnter has already overwritten by
    /// the time a postfix runs. Hence the prefix capture.
    ///
    /// The transition GATE (Aircraft.CheckForWinchester's
    /// <c>if (IsPlayerObject &amp;&amp; !Globals._playerPlanesWinchester) return false;</c>,
    /// and the equivalent "stays with AAM/guns" tests) is left alone - those read local
    /// options whose default already has a player's own aircraft behaving this way, and
    /// the guest's setting is not knowable here.</summary>
    [HarmonyPatch(typeof(AircraftStates.Winchester), nameof(AircraftStates.Winchester.onEnter))]
    public static class Patch_V2_RemoteTf_WinchesterStamp
    {
        static void Prefix(AircraftStates.Winchester __instance, out ObjectBase.WeaponStatus __state)
        {
            __state = __instance._aircraft?._weaponStatus ?? ObjectBase.WeaponStatus.Hold;
        }

        static void Postfix(AircraftStates.Winchester __instance, ObjectBase.WeaponStatus __state)
        {
            var aircraft = __instance._aircraft;
            if (aircraft == null) return;
            if (!Suppression.HostSuppressesRemoteTfAi(aircraft)) return;

            aircraft._checkForWinchester = false;
            aircraft._weaponStatus = __state == ObjectBase.WeaponStatus.Free
                ? ObjectBase.WeaponStatus.Tight
                : __state;
        }
    }

    /// <summary>The helicopter half of the same state. HelicopterStates.Winchester
    /// .onEnter has only the _checkForWinchester branch - no weapon-status stamp - so
    /// this just hands the remote player's helicopters the same "stop re-testing" the
    /// host's own get.</summary>
    [HarmonyPatch(typeof(HelicopterStates.Winchester), nameof(HelicopterStates.Winchester.onEnter))]
    public static class Patch_V2_RemoteTf_WinchesterStamp_Helo
    {
        static void Postfix(HelicopterStates.Winchester __instance)
        {
            var helicopter = __instance._helicopter;
            if (helicopter == null) return;
            if (!Suppression.HostSuppressesRemoteTfAi(helicopter)) return;

            helicopter._checkForWinchester = false;
        }
    }

    /// <summary>Attack/sonobuoy-drop waypoints exist on both sides (they sync as
    /// OrderType.AttackAtWaypoint), but only the host's copy may fire. The client's
    /// is for map display: when its replica reaches the waypoint the vanilla task
    /// would run the attack pipeline anyway - inert for an untargeted drop, since
    /// HandleEngageTasks is host-only, but a TARGETED one calls InsertEngageTask,
    /// whose client prefix forwards a fire order upstream and the host shoots twice.
    /// Only the calculation is skipped; the rest of OnUpdate still runs, so the
    /// local waypoint completes and clears off the map in step with the host's.</summary>
    [HarmonyPatch(typeof(AttackAtWaypoint), "AttackCalculations")]
    public static class Patch_V2_AttackAtWaypoint_Suppress
    {
        static bool Prefix() => !Suppression.ClientActive;
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

    /// <summary>CLIENT: the client never decides that a ship starts sinking.
    ///
    /// Blastzone suppression stops the client APPLYING damage, but Compartments
    /// .OnFixedUpdate was never suppressed, and it is not a renderer - it is a
    /// simulation that acts on hard thresholds every physics tick (:1290-1355):
    ///
    ///   if (FloodingPercentage > 40f) Sink(SinkFocus.All);       // 50f for submarines
    ///   if (num &gt; 70f || num &lt; -70f) { _capSized = true; Sink(...); }
    ///
    /// DamageState corrections arrive every CfgDamageSyncInterval (2 s default), so
    /// between them the client free-runs ~100 ticks of its own flooding spread and
    /// damage-control repair and can cross those thresholds on its own schedule.
    ///
    /// And it is a ONE-WAY door. Sink() opens with `if (_isSinking) return;` and
    /// nothing clears _isSinking outside DebugRessurect(), while DamageStateSerializer
    /// .Apply can only ever START a sink (msg.IsSinking &amp;&amp; !_isSinking) - it has no
    /// way to cancel one. So a client that tripped 40% early sinks permanently while
    /// the host is still afloat and repairing, and the two never reconverge. That is
    /// the "sinks straight away on one side, afloat for ages on the other" divergence.
    ///
    /// The host's decision reaches us three ways - EntityState.FlagSinking in the
    /// 10 Hz stream, DamageState.IsSinking, and DestroyEvent's ModeStartSinking - and
    /// all of them apply under Authority, which is what this lets through.</summary>
    [HarmonyPatch(typeof(Compartments), nameof(Compartments.Sink))]
    public static class Patch_V2_Compartments_Sink
    {
        static bool Prefix(Compartments __instance, out bool __state)
        {
            __state = __instance._isSinking;
            if (!Suppression.ClientActive || Authority.IsAllowed) return true;

            // Refused. The client's OnFixedUpdate retries every physics tick for as
            // long as it stays over the threshold (_isSinking never latches), so log
            // once per unit - this line paired against the absence of an "applied host
            // sink" line is what identifies a sink signal that never arrived.
            LogRefusalOnce(__instance);
            return false;
        }

        /// <summary>HOST: push the sink out immediately instead of waiting up to
        /// CfgDamageSyncInterval. FlagSinking rides the 10 Hz stream and would start
        /// the client's descent within ~100 ms, but _sinkTime only travels on
        /// DamageState - so without this the client descends off its own clock until
        /// the next correction and then visibly jumps onto the host's curve.</summary>
        static void Postfix(Compartments __instance, bool __state)
        {
            if (__state) return;                  // already sinking - Sink() no-opped
            if (!__instance._isSinking) return;   // prefix refused it (client)

            var unit = __instance._baseObject;
            Plugin.Log.LogInfo($"[Damage] Sink START {Describe(__instance)} " +
                               $"destroyed={unit?.IsDestroyed} " +
                               $"{(Plugin.Instance.CfgIsHost.Value ? "(host decision)" : "(applied from host)")}");
            StateBroadcaster.SendDamageStateNow(unit);
        }

        private static readonly System.Collections.Generic.HashSet<int> _refusalLogged = new();

        private static void LogRefusalOnce(Compartments comps)
        {
            int id = comps._baseObject?.UniqueID ?? -1;
            if (!_refusalLogged.Add(id)) return;
            Plugin.Log.LogInfo($"[Damage] Sink REFUSED locally {Describe(comps)} - " +
                               "waiting for the host's FlagSinking/DamageState");
        }

        internal static string Describe(Compartments comps)
        {
            var unit = comps._baseObject;
            return $"{unit?.UniqueID}-{unit?.name} flooding={comps.FloodingPercentage:0.#}% " +
                   $"integrity={comps.IntegrityPercentage:0.#}%";
        }

        /// <summary>Cleared on session teardown so a second session logs afresh.</summary>
        public static void ClearLogCache() => _refusalLogged.Clear();
    }

    /// <summary>CLIENT: same reasoning for the other terminal decision in
    /// Compartments.OnFixedUpdate - <c>TotalIntegrity / _maxTotalIntegrity &lt; 0.1f</c>
    /// and the _delayedSinkTime expiry both call DestroyByExplosion() (:1295-1304),
    /// so the client could kill a ship the host still has alive. Destruction arrives
    /// as DestroyEvent / FlagDestroyed and applies through
    /// CombatEventHandler.DestroyFromNetwork, under Authority.</summary>
    [HarmonyPatch(typeof(Compartments), nameof(Compartments.DestroyByExplosion))]
    public static class Patch_V2_Compartments_DestroyByExplosion_Suppress
    {
        static bool Prefix() => !Suppression.ClientActive || Authority.IsAllowed;
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

    /// <summary>Civilian air/sea traffic routes are host-only, like every other
    /// spawner. This is also what keeps the guard below from being fatal: the guard
    /// correctly refuses a client-local aircraft and createAircraft duly returns null,
    /// but CivilianRoute.SpawnUnit dereferences that return immediately
    /// (<c>aircraft.DesiredAltitude.Value = ...</c>) and only null-checks further down,
    /// on the Vessel branch. The throw pre-empts the route's own _failedSpawnCounter,
    /// so its "50 failures and I disable myself" backstop never runs and it retries
    /// every frame forever - and because it unwinds out of MissionManager.OnUpdate,
    /// which sits inside GameUpdater.update(), it takes the rest of that frame's game
    /// update with it. One 16-minute guest battle logged 8,618 of them, 9.3 MB of
    /// Player.log, against a clean host log.
    ///
    /// Stopping the route rather than null-guarding the spawn: the host runs its own
    /// copy and the traffic arrives as replicas either way.</summary>
    [HarmonyPatch(typeof(CivilianRoute), nameof(CivilianRoute.OnUpdate))]
    public static class Patch_V2_CivilianRoute_Suppress
    {
        static bool Prefix() => !Suppression.ClientActive;
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
