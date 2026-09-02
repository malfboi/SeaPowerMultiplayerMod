using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Mitigation for the vanilla "aircraft stuck on the elevator / catapult, carrier
    /// jams up" bug. Reported against the mod, but the launch pipeline is host-only
    /// under v2 (the client's deck pump and task FSMs are suppressed - see
    /// Patch_V2_FlightDeckTasks_Suppress / Patch_V2_FlightDeckTaskFsm_Suppress), so
    /// nothing we add can freeze a launch. Every stall condition below is in the game's
    /// own code; we only make it survivable. The mod does raise the hit rate, because a
    /// join round-trips every deck through save/load and the host drives both fleets.
    ///
    /// Three independent problems, three parts:
    ///
    /// 1. ONE BAD TASK KILLS THE SHIP. FlightDeck.OnFixedUpdate walks its task list with
    ///    no try/catch, and neither does its caller (ObjectBase.OnFixedUpdate walks
    ///    _obp._systems the same way). A single throwing task therefore freezes every
    ///    later task on that deck AND every subsystem after FlightDeck on that vessel,
    ///    every frame, permanently. The finalizer below contains the blast radius to the
    ///    one task that threw.
    ///
    /// 2. TWO REACHABLE THROWS IN TaxiToLaunchPoint. Both are plain slips:
    ///      - TryGetLaunchPoint assigns _launchPoint._lastAssigned BEFORE the
    ///        `if (_launchPoint == null) return;` two lines below it. The dead null check
    ///        is the proof of intent; we restore it.
    ///      - GetTaxiPath guards its route loop with `if (list.Count > 0)` and then
    ///        indexes list[0][0] unguarded two statements later.
    ///
    /// 3. RESERVATIONS LEAK, AND NOTHING TIMES OUT. Elevator.IsBusy is
    ///    `_incomingObjects.Count > 0 || _isLowered != _startsLowered`, and a launch
    ///    point is blocked while any elevator blocking it is busy (the graph is mutual,
    ///    FlightDeck.cs:470-592). LaunchTask gates raiseElevator -> taxiToLaunch on
    ///    `getLaunchPoint(...) != null` with no timeout and no alternative exit, so one
    ///    stale reservation parks every later launch on the elevator forever. Leaks:
    ///    launchVehicle adds the aircraft to elevator._incomingObjects but only
    ///    TaxiToLaunchPoint.onExit / DeSpawn remove it, so a task that ends early (its
    ///    aircraft destroyed on deck, an abort, a save/load) never does - and
    ///    FinishFlightDeckTask's CleanUp() only drops Unity-null entries. Likewise
    ///    Lower-/RaiseElevator skip the _isLowered write when interrupted, which two
    ///    tasks sharing one elevator can leave inverted.
    ///
    /// The watchdog's rule is "collect ownerless reservations", not "guess what is
    /// stuck": anything a live task still holds is never touched, so it cannot fight a
    /// legitimately slow deck. It only wakes on a deck that already has a launch stalled
    /// past <see cref="StallSeconds"/>, and a stall it cannot prove stale (an off-course
    /// carrier, a genuinely blocked cat) is logged with its diagnosis rather than
    /// "fixed" - stranding a live aircraft mid-deck would be worse than the jam.
    /// </summary>
    internal static class FlightDeckJamGuard
    {
        // A LaunchTask that has not changed state in this much SIM time is stalled.
        // Generous on purpose: engine warm-up plus FlightDeck._launchDelay plus a
        // deflector cycle is the longest legitimate single state, and it is nowhere
        // near this. GameTime.time does not advance while paused.
        private const float StallSeconds = 90f;

        // Real-time cadence of the sweep. The sweep is a no-op on a healthy deck.
        private const float SweepIntervalSec = 5f;

        // Per-deck log throttle: a permanently wedged deck must not flood the log.
        private const float LogRepeatSec = 60f;

        private static float _nextSweep;
        private static readonly Dictionary<int, float> _nextLogAt = new();

        // Scratch, reused per deck - this runs every few seconds for the whole battle.
        private static readonly HashSet<(FlightDeckPoint, ObjectBase)> _owners = new();
        private static readonly HashSet<Elevator> _heldElevators = new();
        private static readonly HashSet<FlightDeckTask> _dropped = new();
        private static readonly List<FlightDeckTask> _stalled = new();

        /// <summary>The local machine simulates its own decks (host or offline). A
        /// client's FlightDeckTasks are display-only mirrors rebuilt from the host's
        /// snapshot by FlightDeckStateApplier, with empty state machines - sweeping
        /// those would fight the mirror and repair nothing real.</summary>
        private static bool DecksAreLocal => !Suppression.ClientActive;

        public static void Reset()
        {
            _nextSweep = 0f;
            _nextLogAt.Clear();
        }

        /// <summary>Per-frame entry point (Plugin.Update). Self-throttled.</summary>
        public static void Tick()
        {
            if (!DecksAreLocal) return;
            if (GameTime.IsPaused()) return;

            float now = Time.unscaledTime;
            if (now < _nextSweep) return;
            _nextSweep = now + SweepIntervalSec;

            var vessels = UnitRegistry.Vessels;
            for (int i = 0; i < vessels.Count; i++) SweepDeck(vessels[i]);

            // Airbases are LandUnits and carry flight decks exactly as carriers do -
            // the report covers both.
            var landUnits = UnitRegistry.LandUnits;
            for (int i = 0; i < landUnits.Count; i++) SweepDeck(landUnits[i]);
        }

        private static void SweepDeck(ObjectBase? carrier)
        {
            var fd = carrier?._obp?._flightDeck;
            if (fd == null || carrier == null) return;

            // Detection: only a LaunchTask counts. A PendingLaunchTask sits in
            // HandleAwaitSpawnTask indefinitely by design (it is waiting for the player
            // to press LAUNCH), and a RecoveryTask's Approach is a holding stack.
            _stalled.Clear();
            float nowSim = GameTime.time;
            var tasks = fd.FlightDeckTasks;
            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                if (!(t is LaunchTask)) continue;
                var sm = t._stateMachine;
                if (sm?.CurrentState == null) continue;
                if (nowSim - sm.StateChangeTime > StallSeconds) _stalled.Add(t);
            }
            if (_stalled.Count == 0) return; // healthy deck: nothing is touched

            // A task whose aircraft is gone can never advance - its AtAny(IsDestroyed)
            // transition re-enters FinishFlightDeckTask forever (and PlaneTakeOff.onExit
            // throws on the way, which is how part 1 above gets triggered). Drop it so
            // its reservations become ownerless below.
            _dropped.Clear();
            for (int i = 0; i < _stalled.Count; i++)
            {
                var t = _stalled[i];
                if (t.Vehicle != null && !t.Vehicle.IsDestroyed) continue;
                _dropped.Add(t);
                fd.removeFlightDeckTask(t, delayed: true);
                Plugin.Log.LogWarning($"[DeckJam] {carrier.getUIDAndName()}: dropping stalled " +
                    $"{t._stateMachine?.CurrentStateName} task whose aircraft is gone (uid={t._uid}).");
                Telemetry.Count("deckJam.taskDropped");
            }

            int purged = PurgeOwnerlessReservations(fd);
            purged += RepairElevatorFlags(fd, carrier);

            if (purged > 0)
            {
                Plugin.Log.LogWarning($"[DeckJam] {carrier.getUIDAndName()}: cleared {purged} " +
                    "ownerless deck reservation(s) after a stalled launch.");
                Telemetry.Count("deckJam.reservationsCleared", purged);
            }

            ReportStalls(fd, carrier, nowSim, purged);
        }

        // ── Ownerless-reservation collection ────────────────────────────────────

        /// <summary>A reservation is legitimate only while some LIVE task on this deck
        /// both owns the vehicle and holds that point. Anything else is garbage nothing
        /// will ever release, because release only happens from the owning task's own
        /// state exits.</summary>
        private static int PurgeOwnerlessReservations(FlightDeck fd)
        {
            _owners.Clear();
            CollectOwners(fd.FlightDeckTasks);
            // Staged adds count as owners: launchVehicle reserves the elevator BEFORE
            // handleFlightDeckTasks migrates the task into the live list.
            CollectOwners(fd._flightDeckTasksToAdd);

            int purged = 0;
            for (int i = 0; i < fd._elevators.Count; i++)      purged += PurgePoint(fd._elevators[i]);
            for (int i = 0; i < fd._launchPoints.Count; i++)   purged += PurgePoint(fd._launchPoints[i]);
            for (int i = 0; i < fd._recoveryPoints.Count; i++) purged += PurgePoint(fd._recoveryPoints[i]);
            return purged;
        }

        private static void CollectOwners(IList<FlightDeckTask> tasks)
        {
            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                if (t == null || _dropped.Contains(t)) continue;
                var v = t.Vehicle;
                if (v == null) continue;
                if (t._elevator != null)      _owners.Add((t._elevator, v));
                if (t._launchPoint != null)   _owners.Add((t._launchPoint, v));
                if (t._recoveryPoint != null) _owners.Add((t._recoveryPoint, v));
            }
        }

        private static int PurgePoint(FlightDeckPoint point)
        {
            int purged = 0;

            for (int i = point._incomingObjects.Count - 1; i >= 0; i--)
            {
                var v = point._incomingObjects[i];
                if (v != null && !v.IsDestroyed && _owners.Contains((point, v))) continue;
                point._incomingObjects.RemoveAt(i);
                purged++;
            }

            var occupant = point.OccupyingObject;
            if (occupant != null && (occupant.IsDestroyed || !_owners.Contains((point, occupant))))
            {
                point.Release(occupant);
                purged++;
            }

            // The game's own tidy-up for _serviceState / IsOccupied, now that the lists
            // are honest.
            point.CleanUp();
            return purged;
        }

        /// <summary>Lower-/RaiseElevator skip their `_isLowered` write when interrupted,
        /// which leaves the flag inverted and Elevator.IsBusy stuck true forever. Safe to
        /// reset only when no live task is riding that elevator - _startsLowered is the
        /// resting value the game itself restores (FlightDeck.cs:749, :3231).</summary>
        private static int RepairElevatorFlags(FlightDeck fd, ObjectBase carrier)
        {
            _heldElevators.Clear();
            CollectHeldElevators(fd.FlightDeckTasks);
            CollectHeldElevators(fd._flightDeckTasksToAdd);

            int repaired = 0;
            for (int i = 0; i < fd._elevators.Count; i++)
            {
                var el = fd._elevators[i];
                if (el._isLowered == el._startsLowered) continue;
                if (_heldElevators.Contains(el)) continue;
                if (el._incomingObjects.Count > 0) continue; // still reserved by someone

                el._isLowered = el._startsLowered;
                repaired++;
                Plugin.Log.LogWarning($"[DeckJam] {carrier.getUIDAndName()}: elevator {i} " +
                    $"was flagged {(el._startsLowered ? "raised" : "lowered")} with nothing riding it - reset.");
            }
            return repaired;
        }

        private static void CollectHeldElevators(IList<FlightDeckTask> tasks)
        {
            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                if (t == null || _dropped.Contains(t)) continue;
                if (t._elevator != null) _heldElevators.Add(t._elevator);
            }
        }

        // ── Diagnosis for the stalls we deliberately do NOT touch ───────────────

        /// <summary>A stall we cannot prove stale gets logged, not "fixed". Prints the
        /// three things that separate the remaining causes: which state it is wedged in,
        /// whether the ship is OnCourse (Spawn, Taxi and every TakeOff state hard-block
        /// on it, and it needs the heading within 2 degrees of the demanded course), and
        /// whether a launch point is obtainable at all.</summary>
        private static void ReportStalls(FlightDeck fd, ObjectBase carrier, float nowSim, int purged)
        {
            int id = carrier.UniqueID;
            float now = Time.unscaledTime;
            if (_nextLogAt.TryGetValue(id, out float next) && now < next) return;
            _nextLogAt[id] = now + LogRepeatSec;

            for (int i = 0; i < _stalled.Count; i++)
            {
                var t = _stalled[i];
                if (_dropped.Contains(t)) continue;

                string lp;
                try
                {
                    lp = t._elevator == null
                        ? "no elevator"
                        : (t._launchPoint != null
                            ? "held"
                            : (t._elevator.getLaunchPoint(t.VehicleType,
                                    fd._baseObject is LandUnit, t._ltp?._loadoutIndex ?? -1) != null
                                ? "available"
                                : "none available (all busy or blocked)"));
                }
                catch (Exception e) { lp = "query threw: " + e.GetType().Name; }

                Plugin.Log.LogWarning($"[DeckJam] {carrier.getUIDAndName()}: " +
                    $"{t.Vehicle?.getUIDAndName() ?? "?"} stalled in {t._stateMachine?.CurrentStateName} for " +
                    $"{nowSim - t._stateMachine!.StateChangeTime:F0}s - OnCourse={carrier.OnCourse}, " +
                    $"launchPoint={lp}, reservationsCleared={purged}. " +
                    "If OnCourse=False the deck is waiting for the ship to settle within 2 degrees of its ordered course.");
                Telemetry.Count("deckJam.stallReported");
            }
        }
    }

    // ── 1. Contain a throwing deck task ─────────────────────────────────────────

    /// <summary>FlightDeck.OnFixedUpdate iterates its tasks with no try/catch, and
    /// ObjectBase.OnFixedUpdate iterates _obp._systems the same way, so one throwing
    /// task takes down the rest of the deck AND the rest of that ship's subsystems for
    /// good. Swallow at the task boundary instead: the bad task stops advancing (the
    /// watchdog above will collect it once it stalls), everything else keeps running.
    ///
    /// Logged once per task - the throw repeats every FixedUpdate, so an unthrottled
    /// message is a log flood at 50 Hz.</summary>
    [HarmonyPatch(typeof(FlightDeckTask), nameof(FlightDeckTask.fixedTickStateMachine))]
    public static class Patch_FlightDeckTask_ContainThrow
    {
        private static readonly HashSet<Guid> _reported = new();

        static Exception? Finalizer(Exception? __exception, FlightDeckTask __instance)
        {
            if (__exception == null) return null;

            if (_reported.Add(__instance._uid))
            {
                Plugin.Log.LogError($"[DeckJam] Deck task threw and was contained on " +
                    $"{__instance._flightDeck?._baseObject?.getUIDAndName() ?? "?"} " +
                    $"(state={__instance._stateMachine?.CurrentStateName}, " +
                    $"aircraft={__instance.Vehicle?.getUIDAndName() ?? "?"}): {__exception}");
                Telemetry.Count("deckJam.taskThrew");
            }
            return null; // swallow: the rest of the deck and the ship keep ticking
        }
    }

    // ── 2. The two reachable throws in TaxiToLaunchPoint ────────────────────────

    /// <summary>Both fixes below target PRIVATE game methods by name. Resolved through
    /// here rather than a plain TargetMethod so that a rename in a game update skips the
    /// patch with a warning: a null TargetMethod aborts PatchAll, which would take the
    /// whole mod down (Plugin.Awake disables multiplayer on any patch failure) over a
    /// bug fix that is optional by nature.</summary>
    internal static class DeckFix
    {
        internal static IEnumerable<System.Reflection.MethodBase> Target(Type type, string name)
        {
            var m = AccessTools.Method(type, name);
            if (m == null)
            {
                Plugin.Log.LogWarning($"[DeckJam] {type.Name}.{name} not found - the vanilla " +
                    "deck-jam throw it guards is unpatched in this game build.");
                yield break;
            }
            yield return m;
        }
    }

    /// <summary>TaxiToLaunchPoint.TryGetLaunchPoint does:
    ///
    ///     _launchPoint = _elevator.getLaunchPoint(...);
    ///     _launchPoint._lastAssigned = GameTime.time;   // throws when none was free
    ///     if (_launchPoint == null) return;             // dead code
    ///
    /// so it NREs whenever no launch point is obtainable - which is the normal state of a
    /// busy deck, and is reached every FixedUpdate while a plane waits for a cat. Restore
    /// the intent by skipping the body in exactly that case; the plane simply retries
    /// next tick, which is what the dead guard was for.
    ///
    /// When a point IS available the original runs unmodified (its _lastAssigned write is
    /// valid then), so this changes nothing on a healthy deck beyond one extra
    /// getLaunchPoint query per tick while waiting.</summary>
    [HarmonyPatch]
    public static class Patch_TaxiToLaunchPoint_NullLaunchPoint
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            DeckFix.Target(typeof(TaxiToLaunchPoint), "TryGetLaunchPoint");

        static bool Prefix(TaxiToLaunchPoint __instance)
        {
            var task = __instance._flightDeckTask;
            if (task?._elevator == null) return false; // original would NRE on the same field

            try
            {
                var lp = task._elevator.getLaunchPoint(
                    task.VehicleType,
                    task._flightDeck?._baseObject is LandUnit,
                    task._ltp?._loadoutIndex ?? -1);
                if (lp == null)
                {
                    Telemetry.Count("deckJam.launchPointWaitAverted");
                    return false; // nothing free yet - retry next tick, do not throw
                }
            }
            catch
            {
                return true; // an unexpected shape: leave vanilla behaviour exactly as-is
            }

            return true;
        }
    }

    /// <summary>TaxiToLaunchPoint.GetTaxiPath guards its route loop with
    /// `if (list.Count > 0)` and then indexes `list[0][0]` unconditionally two statements
    /// later, so an elevator/launch-point pair with no taxi path in the ini throws
    /// ArgumentOutOfRangeException out of the deck tick. Do what the guarded original
    /// would have done: the launch point itself is already the unconditional final
    /// waypoint, so only the physics model needs a default.</summary>
    [HarmonyPatch]
    public static class Patch_TaxiToLaunchPoint_NoTaxiRoute
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            DeckFix.Target(typeof(TaxiToLaunchPoint), "GetTaxiPath");

        static bool Prefix(TaxiToLaunchPoint __instance)
        {
            var task = __instance._flightDeckTask;
            if (task?._elevator == null || task._launchPoint == null || task._flightDeck == null)
                return true;

            List<List<TaxiPath>> routes;
            try { routes = task._flightDeck.FindValidRoutes(task._elevator, task._launchPoint); }
            catch { return true; }

            if (routes != null && routes.Count > 0 && routes[0].Count > 0)
                return true; // healthy: the original runs and recomputes the same thing

            __instance._waypoints.Clear();
            __instance._waypoints.Add(task._launchPoint._position);

            if (__instance._airVehicle is Aircraft aircraft)
                __instance._motionController = aircraft.SetTaxiPhysicsModel(task._flightDeck, false, false);
            else if (__instance._airVehicle is Helicopter helicopter)
                __instance._motionController = helicopter.SetTaxiPhysicsModel(task._flightDeck, false, false);

            if (__instance._motionController != null)
                __instance._motionController.CommandPosition = __instance._waypoints[0];

            Plugin.Log.LogWarning($"[DeckJam] {task._flightDeck._baseObject?.getUIDAndName() ?? "?"}: " +
                $"no taxi route from {task._elevator._sectionName} to {task._launchPoint._sectionName} - " +
                "taxiing direct instead of throwing.");
            Telemetry.Count("deckJam.noTaxiRoute");
            return false;
        }
    }
}
