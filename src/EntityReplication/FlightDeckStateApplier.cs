using System;
using System.Collections.Generic;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// CLIENT-side flight-ops mirror. The client runs no flight-deck logic of its own
    /// (its task pump and task state machines are suppressed - see
    /// Patch_V2_FlightDeckTasks_Suppress / Patch_V2_FlightDeckTaskFsm_Suppress), so its
    /// carriers would otherwise show an empty/stale Flight Ops window. This applies the
    /// host's FlightDeckState snapshot: it sets the availability counts + ammo and
    /// reconciles the carrier's full task queue - interactive pending-launch rows plus
    /// display-only launch/recovery/cooldown rows - to match the host, identifying
    /// tasks by their stable Guid and mirroring the host's queue order. The launched
    /// aircraft themselves still arrive via the normal EntitySpawn path.
    /// </summary>
    public static class FlightDeckStateApplier
    {
        private static readonly HashSet<Guid> _incoming = new();

        // One logical snapshot may arrive as several sub-MTU chunk messages (see
        // FlightDeckStateMessage doc). ReliableOrdered delivery makes each train
        // contiguous and in-order per carrier, so accumulation is just: head chunk
        // starts, later chunks append, last chunk applies the union.
        private static readonly Dictionary<int, FlightDeckStateMessage> _partial = new();

        public static void Reset() => _partial.Clear();

        public static void Apply(FlightDeckStateMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;

            if (msg.ChunkCount > 1)
            {
                if (msg.ChunkIdx == 0)
                    _partial[msg.CarrierId] = msg;
                else if (_partial.TryGetValue(msg.CarrierId, out var acc) && acc.ChunkCount == msg.ChunkCount)
                {
                    acc.VehicleNumbers.AddRange(msg.VehicleNumbers);
                    acc.SquadronNumbers.AddRange(msg.SquadronNumbers);
                    acc.Tasks.AddRange(msg.Tasks);
                }
                else
                    return; // head chunk was dropped pre-sync - wait for the next full train

                if (msg.ChunkIdx < msg.ChunkCount - 1) return;
                msg = _partial[msg.CarrierId];
                _partial.Remove(msg.CarrierId);
            }

            var carrier = ReplicaRegistry.Find(msg.CarrierId) ?? StateSerializer.FindById(msg.CarrierId);
            var fd = carrier?._obp?._flightDeck;
            if (fd == null) return;

            var vob = fd._vehiclesOnBoard;

            // Availability + ammo the Flight Ops "aircraft to prepare" list reads.
            //
            // WRITE ONLY ON CHANGE. Every one of these setters raises
            // NotifyPropertyChanged unconditionally, and a snapshot arrives about once
            // a second for as long as any deck task is running (the host's change
            // signature includes each task's ready-up countdown text, which ticks).
            // Re-assigning identical values therefore machine-gunned the Flight Ops
            // window with property notifications, and the vanilla view model answers
            // those by rebuilding its aircraft-type / squadron / count lists - a
            // cascade that ends in UpdateAvailableAircraft() forcing Assigned back to
            // 1. That is why a client could never ready more than one aircraft: the
            // count reset roughly once a second, always before the player could press
            // READY. Every launch order in the logs from that session arrived as
            // count=1, including five retries in a row on one carrier.
            //
            // SQUADRON COUNTS FIRST. The game maintains this pair squadron-then-vehicle
            // everywhere it touches it (FlightDeck.abortLaunchTask at :1715/:1721,
            // abortAllLaunchTasks at :1812/:1814, AddVehicle, CreatePendingLaunchTask),
            // and the order is load-bearing rather than stylistic: the Flight Ops window
            // refreshes off the VEHICLE-level CollectionChanged and never subscribes to
            // Squadron.PropertyChanged, so writing the vehicle count first rebuilds the
            // window against a squadron count that is still mid-update. On a
            // full-squadron abort the refund snapshot rebuilt it at the instant vehicles
            // were back up and squadrons still read 0 - empty squadron list, null
            // SelectedSquadron - and because the streamer only re-sends on change, the
            // view never healed and that deck could not launch again.
            fd._currentAmmo = msg.CurrentAmmo;
            for (int i = 0; i < msg.SquadronNumbers.Count; i++)
            {
                var sc = msg.SquadronNumbers[i];
                if (sc.VehicleIdx >= vob.Count || vob[sc.VehicleIdx] == null) continue;
                var squads = vob[sc.VehicleIdx].Squadrons;
                if (sc.SquadronIdx >= squads.Count || squads[sc.SquadronIdx] == null) continue;
                if (squads[sc.SquadronIdx].Numbers != sc.Numbers)
                    squads[sc.SquadronIdx].Numbers = sc.Numbers;
            }
            for (int i = 0; i < msg.VehicleNumbers.Count; i++)
            {
                var vc = msg.VehicleNumbers[i];
                if (vc.VehicleIdx >= vob.Count || vob[vc.VehicleIdx] == null) continue;
                if (vob[vc.VehicleIdx].Numbers != vc.Numbers)
                    vob[vc.VehicleIdx].Numbers = vc.Numbers;
            }

            // Reconcile the task queue by Guid - the host is authoritative for every
            // row, so anything not in the snapshot goes.
            _incoming.Clear();
            for (int i = 0; i < msg.Tasks.Count; i++) _incoming.Add(msg.Tasks[i].Uid);

            var tasks = fd.FlightDeckTasks;
            for (int i = tasks.Count - 1; i >= 0; i--)
            {
                if (!_incoming.Contains(tasks[i]._uid))
                    tasks.RemoveAt(i);
            }

            for (int i = 0; i < msg.Tasks.Count; i++)
            {
                var t = msg.Tasks[i];
                var existing = FindByUid(tasks, t.Uid);
                if (t.IsPending)
                {
                    if (existing is PendingLaunchTask plt)
                    {
                        // Change-guarded for the same reason as the counts above -
                        // LaunchCount, FlightDeckTaskLabel and Info all notify, and
                        // LaunchCount's setter rewrites Info as a side effect.
                        if (plt.LaunchCount != t.LaunchCount) plt.LaunchCount = t.LaunchCount;
                        plt.AssignedDeckSpots  = t.DeckSpots;
                        plt.AssignedGroundCrew = t.GroundCrew;
                        plt.launchAllowed      = t.LaunchAllowed;
                        if (plt.FlightDeckTaskLabel != t.Label) plt.FlightDeckTaskLabel = t.Label;
                        if (plt.Info != t.Info) plt.Info = t.Info;
                        SyncLaunchCommand(carrier, plt, t.AwaitingLaunch);
                    }
                    else if (existing == null)
                    {
                        var task = BuildDisplayTask(fd, vob, t);
                        if (task != null)
                        {
                            tasks.Add(task);
                            SyncLaunchCommand(carrier, task, t.AwaitingLaunch);
                        }
                    }
                }
                else
                {
                    if (existing != null)
                    {
                        if (existing.FlightDeckTaskLabel != t.Label) existing.FlightDeckTaskLabel = t.Label;
                        if (existing.Info != t.Info) existing.Info = t.Info;
                    }
                    else
                    {
                        tasks.Add(BuildActiveDisplayTask(fd, t));
                    }
                }
            }

            // Mirror the host's queue order (it prioritises / reorders its list).
            int insert = 0;
            for (int i = 0; i < msg.Tasks.Count; i++)
            {
                int j = IndexOfUid(tasks, msg.Tasks[i].Uid);
                if (j < 0) continue; // BuildDisplayTask rejected the row
                if (j != insert) tasks.Move(j, insert);
                insert++;
            }
        }

        /// <summary>The task row's LAUNCH command is normally added by
        /// HandleAwaitSpawnTask.onEnter - a deck state the client never reaches, since its
        /// task pump is suppressed. Without this a readied aircraft offers only ABORT on
        /// the client and can never be sent. Mirror the host's AwaitSpawn state onto the
        /// display task's command list, wired to an upstream AllowLaunch order (the game's
        /// own AllowLaunchFunc only flips local state, which the next snapshot would undo).
        /// The client's frozen task only ever holds ABORT at index 0, so command count is
        /// a sufficient presence test.</summary>
        private static void SyncLaunchCommand(ObjectBase carrier, PendingLaunchTask task, bool awaitingLaunch)
        {
            bool has = task.Commands.Count > 1;
            if (awaitingLaunch == has) return;

            if (!awaitingLaunch) { task.Commands.RemoveAt(1); return; }

            int carrierId = carrier.UniqueID;
            var uid = task._uid;
            task.Commands.Add(new SeapowerUI.ViewModels.ContextMenuItem(
                Singleton<LanguageResourceHandler>.Instance.getText("Windows", "Launch"), null,
                new DelegateCommand(delegate
                {
                    NetworkManager.Instance.SendToServer(new PlayerOrderMessage
                    {
                        SourceEntityId = carrierId,
                        Order          = OrderType.AllowLaunch,
                        AmmoId         = uid.ToString(),
                    });
                    Telemetry.Count("v2.clientAllowLaunchUpstream");
                    Plugin.Log.LogInfo($"[FlightOps] Upstream allow launch: carrier={carrierId} uid={uid}");
                })));
        }

        private static FlightDeckTask FindByUid(
            System.Collections.Generic.IList<FlightDeckTask> tasks, Guid uid)
        {
            for (int i = 0; i < tasks.Count; i++)
                if (tasks[i]._uid == uid) return tasks[i];
            return null;
        }

        private static int IndexOfUid(
            System.Collections.Generic.IList<FlightDeckTask> tasks, Guid uid)
        {
            for (int i = 0; i < tasks.Count; i++)
                if (tasks[i]._uid == uid) return i;
            return -1;
        }

        /// <summary>Build a display-only PendingLaunchTask the suppressed client deck
        /// never advances. readyUpTime is passed non-NaN so the constructor skips the
        /// AI ready-up estimation path; Label/Info come from the host.</summary>
        private static PendingLaunchTask BuildDisplayTask(FlightDeck fd,
            System.Collections.Generic.IList<VehicleTypeOnBoard> vob, FlightDeckStateMessage.TaskRow t)
        {
            if (t.VehicleIdx >= vob.Count) return null;
            var vehicle = vob[t.VehicleIdx];
            if (vehicle == null) return null;
            if (t.LoadoutIdx >= vehicle.Loadouts.Count) return null;
            if (t.SquadronIdx >= vehicle.Squadrons.Count) return null;
            var squadron = vehicle.Squadrons[t.SquadronIdx];
            if (t.CallsignIdx >= squadron.Callsigns.Count) return null;

            var loadout  = vehicle.Loadouts[t.LoadoutIdx];
            var callsign = squadron.Callsigns[t.CallsignIdx];
            var ltp = new LaunchTaskParameters { _launchCount = t.LaunchCount };

            var task = new PendingLaunchTask(fd, vehicle, loadout, squadron, callsign, ltp, 0f, t.LaunchAllowed);
            task._uid = t.Uid;
            task.LaunchCount        = t.LaunchCount;
            // FlightDeck.OnUpdate sums these for the deck/crew utilisation readouts.
            task.AssignedDeckSpots  = t.DeckSpots;
            task.AssignedGroundCrew = t.GroundCrew;
            task.launchAllowed      = t.LaunchAllowed;
            task.FlightDeckTaskLabel = t.Label;
            task.Info               = t.Info;
            return task;
        }

        /// <summary>Inert display row for a non-pending host task (launching / recovery /
        /// cooldown). Empty state machine and _isRTB set so the client's still-running
        /// FlightDeck.OnFixedUpdate tick and _performingAirOps predicate ignore it -
        /// it exists only for the Flight Ops window.</summary>
        // FlightDeckTask.CrewSkill does not exist in every game build; assigning it
        // directly MissingMethodExceptions on builds without it, so the cosmetic
        // property is set via reflection and skipped when absent.
        private static readonly System.Reflection.PropertyInfo _crewSkillProp =
            HarmonyLib.AccessTools.Property(typeof(FlightDeckTask), "CrewSkill");

        private static FlightDeckTask BuildActiveDisplayTask(FlightDeck fd, FlightDeckStateMessage.TaskRow t)
        {
            var task = new FlightDeckTask
            {
                FlightDeckTaskLabel = t.Label,
                Info         = t.Info,
                AircraftType = t.AircraftType,
                Squadron     = t.SquadronName,
            };
            if (_crewSkillProp != null)
            {
                try { _crewSkillProp.SetValue(task, Enum.ToObject(_crewSkillProp.PropertyType, t.CrewSkill), null); }
                catch { }
            }
            task._uid = t.Uid;
            task._flightDeck = fd;
            task._isRTB = true;
            task._stateMachine = new StateMachine();
            return task;
        }
    }
}
