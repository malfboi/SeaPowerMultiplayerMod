using System.Collections.Generic;
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

        /// <summary>
        /// Taskforce-level form of the same test, for AI that runs per TASKFORCE rather
        /// than per unit (Taskforce.CheckAI).
        ///
        /// "Not my own side, and a human is commanding it" replaces the old
        /// "PvP and it is the enemy taskforce". Correct in every seating: a co-op trio
        /// leaves Red as AI and suppresses nothing; 1v1 and 2v1 both make Red human and
        /// suppress it.
        /// </summary>
        internal static bool HostSuppressesRemoteTfAi(Taskforce? tf) =>
            Plugin.Instance.CfgIsHost.Value
            && NetworkManager.Instance.IsHostRunning
            && tf != null
            && tf != Globals._playerTaskforce
            && Teams.IsHumanControlled(tf);

        /// <summary>HOST-side: an opposing PLAYER's fleet, tested WITHOUT requiring the
        /// transport to be up. Spawn-time stamps run while the mission loads, which on
        /// the host can be before it starts listening - but the seating is already known
        /// by then, because the host assigns teams in the lobby before loading.
        /// Use this ONLY for those load-time corrections; anything that acts during a
        /// live battle should ask <see cref="HostSuppressesRemoteTfAi(ObjectBase)"/>,
        /// which additionally requires a session.</summary>
        internal static bool RemotePlayerFleet(ObjectBase? unit) =>
            Plugin.Instance.CfgIsHost.Value
            && unit != null
            && unit._taskforce != null
            && unit._taskforce != Globals._playerTaskforce
            && PlayerRegistry.AnyOnTeam(Team.Red);

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

        // Same handicap, countermeasure side: the difficulty setting now also scales the
        // chaff spoof roll (SeekerBase.CheckCountermeasureLock) and the ECM jam roll
        // (ECM.jamWeapon) - a reduction when the WEAPON is a player object, a bonus when
        // the defender is - so on the host in PvP it lands on the host's side exactly
        // as above. Kept separate so the log line above still reads the same.
        private static (float chaffBonus, float chaffReduction,
                        float ecmBonus, float ecmReduction)? _countermeasureHandicap;

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
            // Only when there are humans on BOTH sides: the handicaps are keyed on
            // IsPlayerObject, so with an AI opponent they are doing exactly the job they
            // were designed for and must be left alone.
            bool hostContestedSession = Plugin.Instance.CfgIsHost.Value
                && NetworkManager.Instance.IsHostRunning
                && Teams.ContestedSession;

            if (hostContestedSession)
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

                if (Globals._chaffSpoofChanceBonus != 0f || Globals._chaffSpoofChanceReduction != 0f
                    || Globals._ecmJamChanceBonus != 0f || Globals._ecmJamChanceReduction != 0f)
                {
                    _countermeasureHandicap = (
                        Globals._chaffSpoofChanceBonus, Globals._chaffSpoofChanceReduction,
                        Globals._ecmJamChanceBonus, Globals._ecmJamChanceReduction);
                    Plugin.Log.LogInfo("[Suppression] PvP countermeasure handicap cleared (chaff " +
                        $"+{_countermeasureHandicap.Value.chaffBonus:0.##}/-{_countermeasureHandicap.Value.chaffReduction:0.##}, " +
                        $"ECM +{_countermeasureHandicap.Value.ecmBonus:0.##}/-{_countermeasureHandicap.Value.ecmReduction:0.##})");
                }

                Globals._chaffSpoofChanceBonus = 0f;
                Globals._chaffSpoofChanceReduction = 0f;
                Globals._ecmJamChanceBonus = 0f;
                Globals._ecmJamChanceReduction = 0f;
                return;
            }

            if (_countermeasureHandicap.HasValue)
            {
                var savedCm = _countermeasureHandicap.Value;
                Globals._chaffSpoofChanceBonus = savedCm.chaffBonus;
                Globals._chaffSpoofChanceReduction = savedCm.chaffReduction;
                Globals._ecmJamChanceBonus = savedCm.ecmBonus;
                Globals._ecmJamChanceReduction = savedCm.ecmReduction;
                _countermeasureHandicap = null;
                Plugin.Log.LogInfo("[Suppression] PvP countermeasure handicap restored");
            }

            if (_interceptHandicap.HasValue)
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

    /// <summary>The cost of that kill-switch, paid once per received session.
    ///
    /// FlightDeck.addFlightDeckTask defaults to delayed:true, which only STAGES a task
    /// in _flightDeckTasksToAdd; handleFlightDeckTasks is what migrates the staging
    /// list into FlightDeckTasks - and the patch above stops it running at all on a
    /// client. FlightDeck.LoadStateFromFile rebuilds every saved task through that same
    /// delayed add (FlightDeck.cs:2681 for pending launches, :2777 and :2950 for the
    /// active ones), so on a guest they all loaded correctly and then sat in the
    /// staging list for the rest of the battle: not in FlightDeckTasks, so not in the
    /// Flight Ops window and not seen by FlightDeckStateApplier's reconcile either.
    ///
    /// The save has already spent them - CreatePendingLaunchTask decrements
    /// squadron.Numbers and vehicle.Numbers before the save is written - so a strike
    /// readied before the battle was missing from the deck AND from the hangar on the
    /// other machine, with nothing to say so. One suppression, which is why it hit
    /// carrier, ASW escort and airfield identically.
    ///
    /// Draining is exactly what the suppressed method would have done for the add
    /// list, and nothing else: the pump stays off, so the queue still never advances
    /// or spawns aircraft locally. Postfixed on the load itself rather than swept
    /// later, so the deck is whole before anything reads it.</summary>
    [HarmonyPatch(typeof(FlightDeck), "LoadStateFromFile",
        new[] { typeof(IniHandler), typeof(string) })]
    public static class Patch_V2_FlightDeckLoad_Drain
    {
        static void Postfix(FlightDeck __instance)
        {
            // Not Suppression.ClientActive: a session load is exactly when the
            // handshake may not be up yet, and on a client the pump never runs at any
            // point, so migrating early is always right.
            if (Plugin.Instance.CfgIsHost.Value) return;

            var staged = __instance._flightDeckTasksToAdd;
            if (staged == null || staged.Count == 0) return;

            int n = staged.Count;
            for (int i = 0; i < n; i++) __instance.FlightDeckTasks.Add(staged[i]);
            staged.Clear();

            Plugin.Log.LogInfo($"[FlightDeck] {__instance._baseObject?.getUIDAndName()}: " +
                $"restored {n} saved deck task(s) from the session " +
                $"(queue now {__instance.FlightDeckTasks.Count})");
        }
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

    /// <summary>The AI's torpedo-wire pilot, both directions.
    ///
    /// TorpedoWireController steers an AI boat's on-wire torpedoes once a second -
    /// SetWireSpeedSetting, OrderWireDepth, dog-legs - and gates itself on
    /// <c>!_submarine.IsPlayerObject</c>. HOST, PvP: the remote player's boat is not a
    /// player object here, so the crew flew their wires for them and every speed or
    /// depth they sent (OrderType.WeaponCommand) was overwritten within the second.
    /// Parity, like the wire limits above.
    ///
    /// CLIENT: it is AI, and its torpedoes are replicas the host flies.</summary>
    [HarmonyPatch(typeof(TorpedoWireController), nameof(TorpedoWireController.OnFixedUpdate))]
    public static class Patch_V2_TorpedoWireController_Suppress
    {
        static bool Prefix(Submarine ____submarine) =>
            !Suppression.ClientActive && !Suppression.HostSuppressesRemoteTfAi(____submarine);
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

    /// <summary>HOST-side PvP: AllowCombatSpeedControl is <c>!(IsPlayerObject &amp;&amp;
    /// _speedSetByPlayer)</c> - the guard that stops the aircraft combat states
    /// re-telegraphing a speed the player chose. The remote player's aircraft are not
    /// player objects here, so it was always true for them and their chosen speed was
    /// overwritten on the first intercept. (_speedSetByPlayer itself is stamped when
    /// the speed order is applied - see OrderHandler SetSpeed / SetSpeedCustom.)</summary>
    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.AllowCombatSpeedControl), MethodType.Getter)]
    public static class Patch_V2_RemoteTf_CombatSpeedControl
    {
        static void Postfix(ObjectBase __instance, ref bool __result)
        {
            if (!__result || !__instance._speedSetByPlayer) return;
            if (!Suppression.HostSuppressesRemoteTfAi(__instance)) return;
            __result = false;
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
    /// a player's own boat uses them too. TorpedoEvasion is auto-defence and has its
    /// own patch above; CounterLaunch is the same shape but is registered through this
    /// one - see the AiEntry list.</summary>
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
            // Auto counter-launch (Submarine.cs:246). The comment below used to say the
            // torpedo-evasion patch covered this; it does not - that one matches only
            // the two TorpedoEvasion types. Same AtAny registration and the same
            // (DM._subAIAppliesToPlayer || !IsPlayerObject) gate, so it belongs here.
            // Its escape (:247) is IsFinished, ungated, so blocking entry cannot strand.
            typeof(SubmarineStates.CounterLaunch),
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
            // The movement cluster is no longer AI-only: the Sprint-and-Drift standing
            // order lets a player's own boat through the same door (Submarine.cs:171,
            // "|| SprintAndDriftDoctrine.Value"). Parity is the player's answer, so the
            // remote boat may enter exactly when its owner has ordered it.
            if (to is SubmarineStates.SelectMovementType)
                predicate = () => inner() && (!Suppression.HostSuppressesRemoteTfAi(sub) || sub.SprintAndDriftDoctrine.Value);
            else
                predicate = () => inner() && !Suppression.HostSuppressesRemoteTfAi(sub);
        }

        internal static void ForceEscape(IState? from, IState? to, ref System.Func<bool> predicate)
        {
            if (predicate == null || from == null || to == null) return;

            // The Baffle-Clearing standing order's door (Submarine.cs:172) is gated on
            // IsPlayerObject being TRUE, so the remote boat would never get it. Open it
            // with the player's own condition.
            if (from is SubmarineStates.Default && to is SubmarineStates.ClearBafflesTurningBack)
            {
                var owner = OwnerOf(to);
                if (owner == null) return;
                var innerDoor = predicate;
                predicate = () => innerDoor() || (Suppression.HostSuppressesRemoteTfAi(owner)
                    && owner.BafflesClearingDoctrine.Value
                    && owner.CourseCommand.Value == null && owner.CommandPosition is GeoPosition
                    && GameTime.missionElapsedTime - owner._lastBafflesClearingTime > Submarine.BafflesClearingDoctrineInterval);
                return;
            }

            if (!(to is SubmarineStates.Default)) return;
            if (!MovementCluster.Contains(from.GetType())) return;
            var sub = OwnerOf(from);
            if (sub == null) return;

            // The escapes (:188, :192-196) are the player's own exits, now doctrine-aware:
            // a boat on Sprint-and-Drift / Baffle-Clearing orders stays in the cycle.
            // Same condition for every registration on a pair - the game's own ordering
            // still picks which one fires.
            var inner = predicate;
            predicate = () => inner() || (Suppression.HostSuppressesRemoteTfAi(sub) && PlayerEscapes(from, sub));
        }

        private static bool PlayerEscapes(IState from, Submarine sub)
        {
            bool sprintDoctrine = sub.SprintAndDriftDoctrine.Value;
            bool bafflesDoctrine = sub.BafflesClearingDoctrine.Value;
            if (from is SubmarineStates.ClearBafflesListening cbl)
                return !sprintDoctrine && (!bafflesDoctrine || cbl.IsFinished);
            if (from is SubmarineStates.ClearBafflesTurningBack)
                return !sprintDoctrine && !bafflesDoctrine;
            return !sprintDoctrine;
        }
    }

    /// <summary>HOST-side PvP: the SURFACE half of the same crew-autonomy layer - the
    /// ASW sprint-and-drift cycle a towed-array escort runs while keeping station.
    ///
    /// Vessel.initStates builds it as a ring: MovingInFormation → FormationSprint →
    /// FormationDrift → ClearBafflesTurningBack → FormationClearBafflesListening →
    /// FormationSprint (Vessel.cs:123-130). Only the door at :123 carries the
    /// ownership gate - <c>(DM._subAIAppliesToPlayer || !IsPlayerObject)</c>, the same
    /// test the whole of item 22 turns on - and _subAIAppliesToPlayer is off by
    /// default, so the game never runs any of this for a player's own ship. On the
    /// host the remote player's escorts are not player objects, so they ran the cycle
    /// unbidden: the owner's ships sprinting ahead and coasting back, at speeds the
    /// owner did not set.
    ///
    /// AND IT IS THE LAST OF THE TELEGRAPH FLOOD. Each of these three states calls
    /// setTelegraph on entry, Patch_Vessel_SetTelegraph puts every one on the wire, and
    /// no existing flag covers them - the origination half of item 10 that survived the
    /// ReturnToFormation work. Silencing the states silences their telegraphs at
    /// source, which is better than filtering them afterwards: the ship never picks the
    /// speed in the first place.
    ///
    /// EVERY entry into FormationSprint is blocked, not just the gated door, because
    /// the ring's own :130 loop-back would otherwise restart it forever. And a ship
    /// already inside the ring when hosting begins is forced out to MovingInFormation -
    /// the ring's escapes (:124, :126, :129, :134-136) are speed- and
    /// FollowingFormation-conditioned rather than ownership-gated, so without this it
    /// could circle indefinitely on a slow leader.</summary>
    public static class RemoteTfVesselFormationStates
    {
        private static readonly System.Collections.Generic.HashSet<System.Type> Ring = new()
        {
            typeof(VesselStates.FormationSprint),
            typeof(VesselStates.FormationDrift),
            typeof(VesselStates.ClearBafflesTurningBack),
            typeof(VesselStates.FormationClearBafflesListening),
        };

        /// <summary>Each state holds its own <c>private Vessel _vessel</c>, assigned in
        /// the constructor before initStates registers anything - resolved once here so
        /// the per-tick predicate carries no reflection.</summary>
        private static Vessel? OwnerOf(IState? state)
        {
            if (state == null) return null;
            var field = AccessTools.Field(state.GetType(), "_vessel");
            if (field == null)
            {
                Plugin.Log.LogWarning($"[Suppression] {state.GetType().Name}._vessel not found - the remote " +
                    "player's escorts will keep running the ASW sprint-and-drift cycle on their own");
                return null;
            }
            return field.GetValue(state) as Vessel;
        }

        internal static void Gate(IState? from, IState? to, ref System.Func<bool> predicate)
        {
            if (predicate == null || to == null) return;

            // Door: no remote-owned ship may enter the ring at all.
            if (to is VesselStates.FormationSprint)
            {
                var ship = OwnerOf(to);
                if (ship == null) return;
                var inner = predicate;
                predicate = () => inner() && !Suppression.HostSuppressesRemoteTfAi(ship);
                return;
            }

            // Escape: force the way out for a ship caught inside when hosting starts.
            if (to is VesselStates.MovingInFormation && from != null && Ring.Contains(from.GetType()))
            {
                var ship = OwnerOf(from);
                if (ship == null) return;
                var inner = predicate;
                predicate = () => inner() || Suppression.HostSuppressesRemoteTfAi(ship);
            }
        }
    }

    /// <summary>HOST-side PvP: AI.KeepTowedArrayStreamed, which re-streams a towed array
    /// the crew thinks should be out.
    ///
    /// Self-gated in the engine as <c>(!DM._subAIAppliesToPlayer &amp;&amp;
    /// IsPlayerObject) → return</c> (AI.cs:3259), so a player's own boat is left to
    /// manage its own array. The remote player's was not, and the array would re-deploy
    /// under an owner who had just stowed it.</summary>
    [HarmonyPatch(typeof(AI), "KeepTowedArrayStreamed")]
    public static class Patch_V2_RemoteTf_TowedArray_Suppress
    {
        static bool Prefix(ObjectBase ____baseObject) =>
            !Suppression.HostSuppressesRemoteTfAi(____baseObject);
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
            RemoteTfVesselFormationStates.Gate(from, to, ref predicate);
        }
    }

    /// <summary>The Sprint-and-Drift / Baffle-Clearing standing orders put a PLAYER's
    /// own ship or boat through the same speed/depth cycle the AI uses (Vessel.cs:137-142,
    /// Submarine.cs:171-196), and those states issue setTelegraph / setPresetDepth from
    /// inside the unit's state machine. Both machines run the cycle off the same
    /// doctrine, so these are derived like FormationInternal - executed, never sent:
    /// on a guest they would otherwise travel upstream as player speed/depth orders on
    /// every leg, and on the host the remote-player telegraph/depth locks would refuse
    /// them and the doctrine would do nothing for the remote player.
    ///
    /// "Inside a state-machine tick" is what tells them apart from the player's own
    /// clicks on the same unit, which arrive from the UI. Frame-scoped like
    /// FormationUpdate, so a missed Exit costs at most the rest of one frame.</summary>
    internal static class DoctrineCycle
    {
        private static int _depth;
        private static int _frame = -1;

        private static readonly HashSet<System.Type> CycleStates = new()
        {
            typeof(SubmarineStates.SelectMovementType),
            typeof(SubmarineStates.Sprint),
            typeof(SubmarineStates.Drift),
            typeof(SubmarineStates.ClearBafflesTurningBack),
            typeof(SubmarineStates.ClearBafflesListening),
            typeof(SubmarineStates.GoingToPeriscopeDepth),
            typeof(VesselStates.SelectMovementType),
            typeof(VesselStates.Sprint),
            typeof(VesselStates.Drift),
        };

        internal static void Enter()
        {
            if (_frame != Time.frameCount) { _frame = Time.frameCount; _depth = 0; }
            _depth++;
        }

        internal static void Exit() { if (_depth > 0) _depth--; }

        internal static bool Drives(ObjectBase? unit)
        {
            if (_depth <= 0 || _frame != Time.frameCount || unit == null) return false;
            if (!unit.SprintAndDriftDoctrine.Value && !unit.BafflesClearingDoctrine.Value) return false;
            var state = unit.StateMachine?.CurrentState;
            return state != null && CycleStates.Contains(state.GetType());
        }
    }

    /// <summary>Prefix/Postfix, not a Finalizer, for the reason Patch_UnitFormation_OnUpdate
    /// gives: this wraps every unit's state machine, and the frame scope already makes a
    /// missed Exit self-correcting.</summary>
    [HarmonyPatch(typeof(StateMachine), nameof(StateMachine.OnFixedUpdate))]
    public static class Patch_StateMachine_OnFixedUpdate_DoctrineScope
    {
        static void Prefix()  => DoctrineCycle.Enter();
        static void Postfix() => DoctrineCycle.Exit();
    }

    [HarmonyPatch(typeof(StateMachine), nameof(StateMachine.OnUpdate))]
    public static class Patch_StateMachine_OnUpdate_DoctrineScope
    {
        static void Prefix()  => DoctrineCycle.Enter();
        static void Postfix() => DoctrineCycle.Exit();
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

    /// <summary>HOST-side PvP: Force EMCON is an AI-side mission flag. The editor writes
    /// <c>ExcludeFromAIRadarRoutine</c> only for non-player-side units, and
    /// SetAdditionalParameters ends with <c>if (!IsPlayerObject &amp;&amp;
    /// _excludeFromAIRadarRoutine) setEMCON(true)</c> - so on the host the remote
    /// player's fleet spawned dark, and the flag kept vetoing the automatic search-radar
    /// activation for engagements (AI.CanAutomaticallyBreakForcedEmcon) that a player's
    /// own ships get. On its owner's machine the fleet is the player side and neither
    /// happens. Clearing the shared data before the method copies it onto the AI stops
    /// both, and the save then carries the cleared flag to the guest.
    /// Gated on RemotePlayerFleet: this runs during the mission load.</summary>
    [HarmonyPatch(typeof(SceneCreator), "SetAdditionalParameters")]
    public static class Patch_V2_RemoteTf_SpawnForceEmcon
    {
        static void Prefix(ObjectBase unit, SharedUnitData unitData)
        {
            if (unitData == null || !unitData._excludeFromAIRadarRoutine) return;
            if (!Suppression.RemotePlayerFleet(unit)) return;

            unitData._excludeFromAIRadarRoutine = false;
            Plugin.Log.LogInfo($"[Suppression] Remote fleet: {unit.getUIDAndName()} spawn Force EMCON " +
                               "dropped (AI-side mission flag)");
        }
    }

    /// <summary>The same flag from a mission trigger's "Force EMCON" action. The game
    /// skips IsPlayerObject units, which on the host misses the remote player's fleet
    /// (and on a guest misses nothing human but its own) - so a scripted trigger could
    /// switch a human player's radars off mid-battle and lock them out of automatic
    /// activation. The action is applied here instead, with every human-commanded
    /// fleet skipped, and the original sees NoChange; the field is restored afterwards
    /// so the trigger still saves as authored.</summary>
    [HarmonyPatch(typeof(Trigger), nameof(Trigger.ActionTrigger))]
    public static class Patch_V2_RemoteTf_TriggerForceEmcon
    {
        static void Prefix(Trigger __instance, out ForceEmconAction __state)
        {
            __state = __instance._actionForceEmcon;
            if (__state == ForceEmconAction.NoChange) return;
            if (!NetworkManager.Instance.IsEstablished && !NetworkManager.Instance.IsHostRunning) return;

            bool force = __state == ForceEmconAction.Force;
            foreach (var unit in __instance._actionUnits)
            {
                if (unit == null || unit.IsDestroyed || unit.IsPlayerObject || unit._ai == null) continue;
                if (unit._taskforce != Globals._playerTaskforce && Teams.IsHumanControlled(unit)) continue;

                unit._ai._excludeFromAIRadarRoutine = force;
                unit.setEMCON(force);
            }
            __instance._actionForceEmcon = ForceEmconAction.NoChange;
        }

        static void Postfix(Trigger __instance, ForceEmconAction __state)
        {
            __instance._actionForceEmcon = __state;
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
    /// relay carries that across.
    ///
    /// Only a fallback now: a launch the owner ordered carries their "launch on
    /// Weapons X" option, which Patch_FlightDeck_LaunchVehicle_RequestedWeaponStatus
    /// writes after this.</summary>
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

    /// <summary>HOST-side PvP: an aircraft heading home off the deck's ReturnToBase state
    /// (flight-deck recovery, not AircraftStates) is stamped
    /// <c>IsPlayerObject ? Hold : Tight</c> - player aircraft now return on Weapons Hold.
    /// The host's copy of the remote player's aircraft took the AI branch. Hold is what
    /// its owner's machine would have set.</summary>
    [HarmonyPatch(typeof(SeaPower.ReturnToBase), nameof(SeaPower.ReturnToBase.onEnter))]
    public static class Patch_V2_RemoteTf_RecoveryWeaponStatus
    {
        static void Postfix(ObjectBase ____objectToReturn)
        {
            if (____objectToReturn == null) return;
            if (!Suppression.HostSuppressesRemoteTfAi(____objectToReturn)) return;
            ____objectToReturn._weaponStatus = ObjectBase.WeaponStatus.Hold;
        }
    }

    /// <summary>HOST-side PvP: at bingo fuel a player's aircraft now diverts to the
    /// nearest compatible tanker of its own taskforce before falling back to RTB -
    /// Aircraft.TryStartEmergencyRefuel, which opens with
    /// <c>if (!IsPlayerObject || _taskforce == null) return false;</c>. On the host the
    /// remote player's aircraft therefore always went straight home. This runs the
    /// player branch for them, through the game's own tanker test.
    ///
    /// KNOWN LIMIT: Order.Type.Refuel, issued by a formation leader, only takes wingmen
    /// that AcceptsOrdersFromPlayer, which the remote side's are not here - such a flight
    /// gets no refuel task, the call reports false, and it RTBs as before.</summary>
    [HarmonyPatch(typeof(Aircraft), "TryStartEmergencyRefuel")]
    public static class Patch_V2_RemoteTf_EmergencyRefuelParity
    {
        private static readonly MethodInfo? _canReach =
            AccessTools.Method(typeof(Aircraft), "CanReachEmergencyRefuelTanker");

        static bool Prefix(Aircraft __instance, ref bool __result)
        {
            if (_canReach == null) return true;
            if (__instance._taskforce == null) return true;
            if (!Suppression.HostSuppressesRemoteTfAi(__instance)) return true;

            var order = __instance.CurrentOrder.Value;
            if (order?.OrderType == Order.Type.Refuel
                && order.TargetUnit is Aircraft ordered
                && __instance._userRoot?.GetRefuelTask(ordered) != null)
            {
                __result = true;
                return false;
            }

            Aircraft? nearest = null;
            float nearestSq = float.MaxValue;
            foreach (ObjectBase unit in __instance._taskforce._taskforceAircraft)
            {
                var tanker = unit as Aircraft;
                if (!(bool)_canReach.Invoke(__instance, new object?[] { tanker })) continue;
                float sq = (tanker!.transform.position - __instance.transform.position).sqrMagnitude;
                if (sq >= nearestSq) continue;
                nearestSq = sq;
                nearest = tanker;
            }

            if (nearest == null) { __result = false; return false; }

            __instance.setOrder(Order.Type.Refuel, nearest, true);
            __result = __instance._userRoot?.GetRefuelTask(nearest) != null;
            return false;
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

    /// <summary>The ballistics overhaul moved the CIWS hit roll out of the intercept
    /// and into a per-tick resolver: while a mount is firing (and for one flight time
    /// after), WeaponSystemCIWS.OnFixedUpdate calls ResolveFireTick, which samples hits
    /// off Globals._rnd and writes the result onto the TARGET - _ciwsDamageTaken and
    /// the other tallies, the wounded-missile fire trail (UpdateCiwsDamageState), the
    /// shot-up aircraft smoke (UpdateGunDamageState) and the hit puffs. The client's
    /// mounts fire too, replaying the host's bursts, so each machine rolled its own
    /// outcome: the client's replica could burn while the host's flew on untouched,
    /// and only the final InterceptAirTarget was being refused above. The host's roll
    /// is the one that happened; its kill arrives as a despawn.</summary>
    [HarmonyPatch]
    public static class Patch_V2_CIWS_ResolveFireTick_Suppress
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(WeaponSystemCIWS), "ResolveFireTick");
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

    /// <summary>The other way a replica gets killed locally. Gun shells the client
    /// replays from the host's bursts are real Projectiles, and Projectile
    /// .ProcessCollision now runs the same fragment-damage model against weapons that
    /// CIWS uses - its own Utils.RandomFloatAdv rolls - and on a kill calls
    /// notifyOfExternalDestruction directly, not through Blastzone. That latches
    /// _externalDestructionNotified on the replica: its threat entry, CIWS targeting and
    /// the defeated-shot checks all treat it as dead while the host's round flies on.
    /// Network kills never come this way (DestroyFromNetwork skips weapons), so a
    /// replica refuses it outright.</summary>
    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.notifyOfExternalDestruction))]
    public static class Patch_V2_WeaponExternalDestruction_Guard
    {
        static bool Prefix(ObjectBase __instance)
        {
            if (!Suppression.ClientActive) return true;
            if (Authority.IsAllowed) return true;
            if (!(__instance is WeaponBase wb)) return true;
            if (ReplicaRegistry.PolicyOf(wb) != ReplicaPolicy.KinematicWeapon) return true;
            Telemetry.Count("v2.blockedLocalExternalDestruction");
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
        // Host: a route can now launch from a client's deck (Mission Editor Air
        // Operations) - run it under Authority, see Patch_PatrolZone_HostAuthority.
        static bool Prefix(out System.IDisposable? __state)
        {
            __state = null;
            if (Suppression.ClientActive) return false;
            if (Plugin.Instance.CfgIsHost.Value && NetworkManager.Instance.IsEstablished)
                __state = Authority.Allowed();
            return true;
        }

        static void Finalizer(System.IDisposable? __state) => __state?.Dispose();
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
    /// is a suppression leak - block it, kill the object, count it loudly.
    ///
    /// EXCEPT during a session load, which is not a leak and must not be reported as
    /// one. Restoring a received save runs the game's own
    /// SceneCreator.LaunchWeapons(), which re-launches everything that was in flight
    /// when the host saved (SceneCreator.cs:5071-5097, Container_Launch per weapon).
    /// Blocking is still right - the host simulates those rounds and streams them
    /// back as replicas, so a local copy would be a duplicate - but it is an expected
    /// consequence of the load, not a hole in the kill-switches, and an error-level
    /// "report this" for it costs somebody an investigation. Ask
    /// SessionManager.SceneLoading, the same exemption Patch_V2_CreateHelicopter_Guard
    /// already carries.</summary>
    [HarmonyPatch(typeof(WeaponBase), nameof(WeaponBase.CommonLaunchSettings))]
    public static class Patch_V2_LaunchCanary
    {
        static bool Prefix(WeaponBase __instance)
        {
            if (!Suppression.ClientActive) return true;
            if (Authority.IsAllowed) return true;
            if (CaptureState.WeaponClassOf(__instance) == null) return true; // chaff/noisemaker etc. stay local for now

            // Deactivating is not enough on its own. The object reached Awake, so it is
            // in UnitRegistry under the id it carried in the save - which is the HOST's
            // id for that same round, the one the host streams a replica under. FindById
            // asks UnitRegistry BEFORE ReplicaRegistry (StateSerializer.cs:152-157), so
            // the corpse would answer for every later state sample, impact and despawn
            // aimed at the live replica: a missile frozen on the client and a despawn
            // landing on the wrong object.
            //
            // And the id has to go, not just the registration. FindById's last resort is
            // ScanForId → SceneCreator.FindGlobalObjectById, which is
            // Resources.FindObjectsOfTypeAll and therefore finds INACTIVE objects too -
            // so a merely deactivated corpse still answers. That matters most for the
            // census: HandleCensus skips requesting a spawn replay for any id FindById
            // can resolve, so the corpse would have told the client it already had the
            // round and suppressed the very replay WeaponLedgerRebuild exists to send.
            //
            // SetUniqueId(0) is the game's own "no id" sentinel and is safe here: it only
            // advances SceneCreator._UID when the new id is HIGHER (ObjectBase.cs:1641),
            // so it cannot disturb allocation, and every lookup path already treats 0 as
            // unresolvable.
            UnitRegistry.Unregister(__instance);
            __instance.SetUniqueId(0);
            __instance.gameObject.SetActive(false);

            if (SessionManager.SceneLoading)
            {
                // Refused, not lost. WeaponLedgerRebuild re-ledgers this round on the
                // host at the session boundary, so the next census advertises it and
                // this client asks for a spawn replay - which is exactly why the id had
                // to be surrendered above: FindById would otherwise have answered with
                // this corpse and suppressed the request. What arrives instead is a
                // host-driven replica, and two copies of one round is the only outcome
                // worse than a late one.
                Telemetry.Count("v2.loadWeaponDeferred");
                Plugin.Log.LogInfo($"[Session] In-flight weapon from the received save not " +
                    $"re-launched locally: {__instance.name} — arrives as a replica via the census.");
                return false;
            }

            Telemetry.Count("v2.canaryBlockedLaunch");
            Plugin.Log.LogError($"[Canary] Blocked un-authorized client weapon launch: " +
                $"{__instance.name} ({__instance._ap?._ammunitionFileName}) — suppression leak, report this");
            return false;
        }
    }

    /// <summary>HOST-side PvP: the remote player's decks ready up on the PLAYER clock,
    /// not the AI convenience clock.
    ///
    /// PendingLaunchTask's constructor discounts the ready-up for a non-player deck:
    ///
    ///   _duration = loadout._readyUpDuration * LaunchCount;
    ///   if (!_flightDeck._baseObject.IsPlayerObject) {
    ///       ModifyDurationForAI(...);
    ///       _duration -= _flightDeck._activeTime * LaunchCount;   // ← the discount
    ///   }
    ///
    /// IsPlayerObject is per-machine, so on the host the OTHER player's airbase takes
    /// the AI branch. _activeTime grows for as long as the deck has been running, and
    /// late in a battle it exceeds a bomber's ~30-minute ready-up outright - so the task
    /// is born with a NEGATIVE duration, instantly complete, the label flashing the
    /// negative remainder. Playtest 40: "when I recover and then launch bombers - they
    /// only use the recovery time and then are ready immediately - says -14 mins."
    ///
    /// That is a fairness break rather than a cosmetic one: one player's strike aircraft
    /// are ready the moment they are ordered and the other's are not.
    ///
    /// ONLY THE MIDDLE BRANCH IS TOUCHED. The two siblings are left exactly as the
    /// engine set them: the -1 sentinel means ready-up is switched off or the task asked
    /// to skip it, and an explicit singleUnitReadyUpTime is a caller stating the figure
    /// outright - which is how FlightDeck.LoadStateFromFile restores a saved task, so
    /// recomputing there would rewrite history on every session load.
    ///
    /// A genuine AI deck keeps its discount, which is why the test is
    /// HostSuppressesRemoteTfAi and not merely !IsPlayerObject.
    ///
    /// The companion prefix below is why this only has to correct arithmetic:
    /// ModifyDurationForAI is not a pure calculation, so undoing its result alone would
    /// leave the rest of what it did standing.</summary>
    [HarmonyPatch(typeof(PendingLaunchTask), MethodType.Constructor,
        new[] { typeof(FlightDeck), typeof(VehicleTypeOnBoard), typeof(Loadout), typeof(Squadron),
                typeof(string), typeof(LaunchTaskParameters), typeof(float), typeof(bool) })]
    public static class Patch_V2_RemoteTf_ReadyUpParity
    {
        static void Postfix(PendingLaunchTask __instance, FlightDeck flightDeck, Loadout loadout,
                            LaunchTaskParameters ltp, float singleUnitReadyUpTime)
        {
            // Reproduce the constructor's own branch test rather than inferring from
            // _duration: a negative duration is exactly what this fixes, so it cannot
            // also be the signal for which path produced it.
            if (!DM_TestTools._allowAircraftReadyUpTime || ltp == null || ltp._skipReadyUp) return;
            if (!float.IsNaN(singleUnitReadyUpTime)) return;

            var deck = flightDeck?._baseObject;
            if (deck == null || loadout == null) return;
            if (deck.IsPlayerObject) return;                        // already on the player clock
            if (!Suppression.HostSuppressesRemoteTfAi(deck)) return; // real AI keeps the discount

            float player = loadout._readyUpDuration * __instance.LaunchCount;
            if (System.Math.Abs(__instance._duration - player) < 0.01f) return;

            Plugin.Log.LogInfo($"[Deck] Ready-up parity on {deck.getUIDAndName()}: " +
                $"{__instance._duration:F0}s -> {player:F0}s (remote player's deck, not AI)");
            __instance._duration = player;
        }
    }

    /// <summary>The other half of ready-up parity, and the reason the postfix above can
    /// be a simple assignment.
    ///
    /// ModifyDurationForAI is not a calculation - it SPENDS the deck's pre-prepared
    /// aircraft. It sorts _flightDeck._aiPreparationData, decrements the matching
    /// entries' _launchCount and drops them once exhausted. Correcting _duration
    /// afterwards therefore fixes the number the player sees while leaving the remote
    /// player's deck quietly stripped of preparation data it was never meant to draw on,
    /// and that consumption is not visible anywhere it would be noticed.
    ///
    /// So it is refused outright for a deck the other player owns, and the duration
    /// arithmetic is then the postfix's only job. Genuine AI decks are untouched.</summary>
    [HarmonyPatch(typeof(PendingLaunchTask), nameof(PendingLaunchTask.ModifyDurationForAI))]
    public static class Patch_V2_RemoteTf_NoAiPreparationSpend
    {
        static bool Prefix(PendingLaunchTask __instance) =>
            !Suppression.HostSuppressesRemoteTfAi(__instance._flightDeck?._baseObject);
    }

    [HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.LoadPreparationTask))]
    public static class Patch_V2_RemoteTf_PreparedAircraft
    {
        static void Prefix(FlightDeck __instance, ref int __state) =>
            __state = __instance._aiPreparationData.Count;

        static void Postfix(FlightDeck __instance, IniHandler ini, string sectionName,
                            string keyName, int __state)
        {
            var deck = __instance._baseObject;
            if (deck == null) return;
            if (deck.IsPlayerObject) return;                  // vanilla already made a real task
            if (!Suppression.RemotePlayerFleet(deck)) return; // a genuine AI deck keeps AI prep

            // Nothing was diverted by THIS call (parse rejected it, or the count clamped
            // to zero) - there is nothing to convert.
            var prep = __instance._aiPreparationData;
            if (prep.Count <= __state) return;

            int idx = prep.Count - 1;
            var data = prep[idx];
            if (data?._vehicle == null || data._loadout == null) return;

            string[] taskData = ini.readValue(sectionName, keyName, "").Split(',');
            if (taskData.Length < 5) return;

            var vehicle = data._vehicle;
            var squadron = FindSquadron(vehicle, taskData[1].Trim());
            if (squadron == null || squadron.Callsigns.Count == 0) return;

            int vehicleIndex  = __instance._vehiclesOnBoard.IndexOf(vehicle);
            int loadoutIndex  = vehicle.Loadouts.IndexOf(data._loadout);
            int squadronIndex = vehicle.Squadrons.IndexOf(squadron);
            if (vehicleIndex < 0 || loadoutIndex < 0 || squadronIndex < 0) return;

            // The player branch clamps against the SQUADRON as well as the vehicle; the
            // AI branch never looked at a squadron, so that clamp has not been applied.
            // Skipped for a save, exactly as vanilla skips it.
            int count = data._launchCount;
            if (!SaveLoadManager.IsASaveFile)
            {
                if (squadron.Numbers < count) count = squadron.Numbers;
                if (count < 1) return;
            }

            var ltp = new LaunchTaskParameters
            {
                _vehicleIndex  = vehicleIndex,
                _loadoutIndex  = loadoutIndex,
                _squadronIndex = squadronIndex,
                _callsignIndex = 0,
                _launchCount   = count,
            };

            // Drop the AI record FIRST: createLaunchTask → CreatePendingLaunchTask spends
            // the airframes out of the squadron and vehicle pools, and leaving the record
            // behind would let the same aircraft be counted twice.
            prep.RemoveAt(idx);

            // Authority: this is the host reproducing the game's own load-time behaviour,
            // not a player order, so the ownership gate on createLaunchTask
            // (Patch_FlightDeck_CreateLaunchTask_Ownership) must not refuse it.
            PendingLaunchTask? task;
            using (Authority.Allowed())
                task = __instance.createLaunchTask(vehicle, data._loadout, squadron,
                    squadron.Callsigns[0], ltp, allowLaunch: false, data._readyUpTime);

            if (task == null)
            {
                // Only CanLaunchVehicle refuses (deck out of ammo points for this
                // loadout). Put the record back so the deck's books stay honest.
                prep.Insert(idx, data);
                Plugin.Log.LogWarning($"[Deck] {deck.getUIDAndName()}: could not prepare " +
                    $"{count}x {vehicle._fileName} for its owner - deck stores refused it.");
                return;
            }

            Plugin.Log.LogInfo($"[Deck] {deck.getUIDAndName()}: prepared {count}x " +
                $"{vehicle._fileName} ({squadron._name}) for the player commanding that side " +
                "- vanilla would have filed it as AI preparation data.");
            Telemetry.Count("v2.remotePreparedAircraft");
        }

        private static Squadron? FindSquadron(VehicleTypeOnBoard vehicle, string name)
        {
            var squadrons = vehicle.Squadrons;
            for (int i = 0; i < squadrons.Count; i++)
                if (squadrons[i] != null && squadrons[i]._name == name) return squadrons[i];

            // Vanilla's own fallback when the name does not match (FlightDeck.cs:3111).
            return squadrons.Count > 0 ? squadrons[0] : null;
        }
    }

    /// <summary>HOST-side PvP: give the remote player's aircraft the winchester rules
    /// THEIR standing orders set, as their own machine would apply them.
    ///
    /// Since Standing Orders the rules live in the unit's TacticalDoctrine, and
    /// DoctrineSyncManager keeps this machine's copy of the remote fleet's doctrine
    /// equal to its owner's - so the values are right here. What is still wrong is
    /// that most of the exits inside CheckWinchester are each a <c>return false</c>
    /// ("stay airborne") behind <c>IsPlayerObject</c>, which is false for the remote
    /// fleet on the machine that simulates it. Their aircraft skipped every exit their
    /// owner had chosen and went home the moment the stock rules said winchester.
    /// (The PlanesWinchester/HelicoptersWinchester master switches are NOT gated and
    /// need nothing.)
    ///
    /// A postfix is enough precisely because of that shape: it runs only when the
    /// engine has already said "winchester", and can only take it back. It can never
    /// send an aircraft home that the engine was going to keep flying.
    ///
    /// This is the GATE. Patch_V2_RemoteTf_WinchesterStamp is the other half and stays:
    /// it deals with what happens once the state is legitimately entered (stop
    /// re-testing, restore the pre-entry weapon status), which is parity of a different
    /// kind and not in conflict with this.</summary>
    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.CheckWinchester))]
    public static class Patch_V2_RemoteTf_WinchesterParity
    {
        static void Postfix(Aircraft __instance, ref bool __result)
        {
            if (!__result) return;                       // only ever turns it OFF
            if (!Suppression.HostSuppressesRemoteTfAi(__instance)) return;

            if (StaysAirborne(__instance)) __result = false;
        }

        /// <summary>The IsPlayerObject-gated exits of Aircraft.CheckWinchester, in the
        /// engine's own branch order and with the engine's own conditions. Branch
        /// membership is by INITIAL loadout, so this lands in the same branch whose
        /// final <c>return true</c> the engine just took.</summary>
        private static bool StaysAirborne(Aircraft a)
        {
            var d = a.TacticalDoctrine;
            if (d == null) return false;
            var initial = a._initialLoadoutCapabilities;
            var current = a._currentLoadoutCapabilities;
            bool fighter = a.Ap != null
                        && a.Ap._unitRoles.Contains(ObjectBaseParameters.UnitRoles.Fighter);
            bool aamAndGun = d.FighterRtbCondition.ResolvedValue == TacticalDoctrine.RtbCondition.AllAamAndGunExpended;
            bool aamOnly   = d.FighterRtbCondition.ResolvedValue == TacticalDoctrine.RtbCondition.AllAamExpended;

            // Strike and ASW share the fighter/bomber exits; ASW adds the sonobuoy one.
            if (initial.CanStrike || initial._hasASWTorpedoes)
            {
                if (!initial.CanStrike && d.ASWBombersStaysWithSonobuoys.ResolvedValue
                    && current._hasSonobuoys) return true;

                if (fighter)
                {
                    if (aamAndGun && (current.HasAAM || current._hasGun)) return true;
                    if (d.FighterBombersStaysWithAAM.ResolvedValue && current.HasAAM) return true;
                    if (d.SRFightersStaysWithGuns.ResolvedValue
                        && !initial.HasMLRAAM && current._hasGun) return true;
                }
                else if (d.BombersStaysWithAAM.ResolvedValue && current.HasAAM) return true;

                return false;
            }

            if (initial.HasMLRAAM)
                return (aamAndGun && (current.HasAAM || current._hasGun))
                    || (aamOnly && current.HasAAM);

            if (initial.HasAAM)
                return (aamAndGun && current._hasGun)
                    || (d.SRFightersStaysWithGuns.ResolvedValue && current._hasGun);

            return false;
        }
    }

    /// <summary>The helicopter half. Helicopter.CheckWinchester has no player exits in
    /// its strike branch; the ASW, interceptor and fighter branches mirror the
    /// aircraft ones above.</summary>
    [HarmonyPatch(typeof(Helicopter), nameof(Helicopter.CheckWinchester))]
    public static class Patch_V2_RemoteTf_WinchesterParity_Helo
    {
        static void Postfix(Helicopter __instance, ref bool __result)
        {
            if (!__result) return;
            if (!Suppression.HostSuppressesRemoteTfAi(__instance)) return;

            var d = __instance.TacticalDoctrine;
            if (d == null) return;
            var initial = __instance._initialLoadoutCapabilities;
            var current = __instance._currentLoadoutCapabilities;
            bool aamAndGun = d.FighterRtbCondition.ResolvedValue == TacticalDoctrine.RtbCondition.AllAamAndGunExpended;
            bool aamOnly   = d.FighterRtbCondition.ResolvedValue == TacticalDoctrine.RtbCondition.AllAamExpended;

            bool stay;
            if (initial.CanStrike) stay = false;
            else if (initial._hasASWTorpedoes)
                stay = d.ASWBombersStaysWithSonobuoys.ResolvedValue && current._hasSonobuoys;
            else if (initial.HasMLRAAM)
                stay = (aamAndGun && (current.HasAAM || current._hasGun)) || (aamOnly && current.HasAAM);
            else if (initial.HasAAM)
                stay = (aamAndGun && current._hasGun) || (d.SRFightersStaysWithGuns.ResolvedValue && current._hasGun);
            else stay = false;

            if (stay) __result = false;
        }
    }

    /// <summary>HOST-side PvP: the standing orders AI reads only for a PLAYER's ships
    /// and fighters - "ships use SAMs / anti-ship missiles against surface targets",
    /// "ships on weapons tight engage hostile aircraft", "fighters do not engage
    /// anti-ship missiles". Each helper starts from the doctrine value (synced from
    /// the owner by DoctrineSyncManager) and then bails on <c>!IsPlayerObject</c>,
    /// which is false for the remote fleet here - so that fleet fought by AI rules
    /// whatever its owner ordered.
    ///
    /// Each postfix recomputes the helper exactly as the owner's machine would, with
    /// IsPlayerObject replaced by "a human commands this taskforce". The helpers exist
    /// twice - in AI and in the behaviour-tree ActionTask_CheckForTargets - and both
    /// are covered. Auto-attack-surface needs nothing: AI no longer gates that test.
    /// AAW salvo size is still gated, inside larger methods, and is not covered.</summary>
    [HarmonyPatch]
    public static class Patch_V2_RemoteTf_DoctrineGateParity
    {
        private static readonly string[] Helpers =
        {
            "ShipsOnWeaponsTightCanEngageHostileAircraft",
            "ShipSAMAntiSurfaceAutoEngagementBlocked",
            "ShipAntiShipMissileAutoEngagementBlocked",
            "FighterAntiShipMissileEngagementBlocked",
        };

        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var type in new[] { typeof(AI), AccessTools.TypeByName("SeaPower.ActionTask_CheckForTargets") })
            {
                if (type == null) continue;
                foreach (var name in Helpers)
                {
                    var m = AccessTools.Method(type, name);
                    if (m != null) yield return m;
                }
            }
        }

        static void Postfix(System.Reflection.MethodBase __originalMethod, object[] __args,
                            ObjectBase ____baseObject, ref bool __result)
        {
            var self = ____baseObject;
            if (self == null || self.IsPlayerObject) return;   // the engine already decided it properly
            if (!Suppression.HostSuppressesRemoteTfAi(self)) return;
            var d = self.TacticalDoctrine;
            if (d == null) return;

            switch (__originalMethod.Name)
            {
                case "ShipsOnWeaponsTightCanEngageHostileAircraft":
                {
                    var target = __args[0] as ObjectBase;
                    __result = d.ShipsOnWeaponsTightEngageHostileAircraft.ResolvedValue
                        && self is Vessel
                        && (target is Aircraft || target is Helicopter)
                        && self._taskforce.RelationshipTo(target._taskforce) == RelationsState.Hostile;
                    break;
                }
                case "ShipSAMAntiSurfaceAutoEngagementBlocked":
                {
                    var ap = __args[0] as AmmunitionParameters;
                    var target = __args[1] as ObjectBase;
                    __result = !d.ShipsAutoUseSAMsAgainstSurfaceTargets.ResolvedValue
                        && self is Vessel && ap != null
                        && target != null && !target.IsAirUnit && !(target is Missile) && !(target is Bomb)
                        && ap._type == Ammunition.Type.Missile
                        && ap._targetType == Ammunition.Target.AAW
                        && ap._secondaryTargetType == Ammunition.Target.ASuW;
                    break;
                }
                case "ShipAntiShipMissileAutoEngagementBlocked":
                {
                    var ap = __args[0] as AmmunitionParameters;
                    var target = __args[1] as ObjectBase;
                    __result = !d.ShipsAutoUseAntiShipMissilesAgainstSurfaceTargets.ResolvedValue
                        && self is Vessel && ap != null
                        && target is Vessel && !target.isSubmerged()
                        && ap._type == Ammunition.Type.Missile
                        && ap._targetType == Ammunition.Target.ASuW;
                    break;
                }
                case "FighterAntiShipMissileEngagementBlocked":
                {
                    var missile = __args[0] as Missile;
                    __result = self.IsAirUnit
                        && self._obp != null && self._obp._unitRoles.Contains(ObjectBaseParameters.UnitRoles.Fighter)
                        && (d.FightersDoNotEngageAntiShipMissiles.ResolvedValue || self.FighterDoNotEngageAntiShipMissilesDoctrine.Value)
                        && missile != null
                        && (missile._ap._targetType == Ammunition.Target.ASuW || missile._ap._secondaryTargetType == Ammunition.Target.ASuW);
                    break;
                }
            }
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
