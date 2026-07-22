using System;
using System.Collections.Generic;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// CLIENT-side flight-ops mirror. The client runs no flight-deck logic of its own
    /// (its task pump is suppressed - see Patch_V2_FlightDeckTasks_Suppress), so its
    /// carriers would otherwise show an empty/stale Flight Ops window. This applies the
    /// host's FlightDeckState snapshot: it sets the availability counts + ammo and
    /// reconciles the carrier's pending-launch queue (the aircraft being readied) to
    /// match the host, identifying tasks by their stable Guid. The launched aircraft
    /// themselves still arrive via the normal EntitySpawn path.
    /// </summary>
    public static class FlightDeckStateApplier
    {
        private static readonly HashSet<Guid> _incoming = new();

        public static void Apply(FlightDeckStateMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;

            var carrier = ReplicaRegistry.Find(msg.CarrierId) ?? StateSerializer.FindById(msg.CarrierId);
            var fd = carrier?._obp?._flightDeck;
            if (fd == null) return;

            var vob = fd._vehiclesOnBoard;

            // Availability + ammo the Flight Ops "aircraft to prepare" list reads.
            fd._currentAmmo = msg.CurrentAmmo;
            for (int i = 0; i < msg.VehicleNumbers.Count; i++)
            {
                var vc = msg.VehicleNumbers[i];
                if (vc.VehicleIdx < vob.Count && vob[vc.VehicleIdx] != null)
                    vob[vc.VehicleIdx].Numbers = vc.Numbers;
            }
            for (int i = 0; i < msg.SquadronNumbers.Count; i++)
            {
                var sc = msg.SquadronNumbers[i];
                if (sc.VehicleIdx >= vob.Count || vob[sc.VehicleIdx] == null) continue;
                var squads = vob[sc.VehicleIdx].Squadrons;
                if (sc.SquadronIdx < squads.Count && squads[sc.SquadronIdx] != null)
                    squads[sc.SquadronIdx].Numbers = sc.Numbers;
            }

            // Reconcile the pending-launch queue by Guid.
            _incoming.Clear();
            for (int i = 0; i < msg.Tasks.Count; i++) _incoming.Add(msg.Tasks[i].Uid);

            var tasks = fd.FlightDeckTasks;
            for (int i = tasks.Count - 1; i >= 0; i--)
            {
                if (tasks[i] is PendingLaunchTask plt && !_incoming.Contains(plt._uid))
                    tasks.RemoveAt(i);
            }

            for (int i = 0; i < msg.Tasks.Count; i++)
            {
                var t = msg.Tasks[i];
                var existing = FindByUid(tasks, t.Uid);
                if (existing != null)
                {
                    existing.LaunchCount       = t.LaunchCount;
                    existing.launchAllowed     = t.LaunchAllowed;
                    existing.FlightDeckTaskLabel = t.Label;
                    existing.Info              = t.Info;
                    SyncLaunchCommand(carrier, existing, t.AwaitingLaunch);
                }
                else
                {
                    var task = BuildDisplayTask(fd, vob, t);
                    if (task != null)
                    {
                        tasks.Add(task);
                        SyncLaunchCommand(carrier, task, t.AwaitingLaunch);
                    }
                }
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

        private static PendingLaunchTask FindByUid(
            System.Collections.Generic.IList<FlightDeckTask> tasks, Guid uid)
        {
            for (int i = 0; i < tasks.Count; i++)
                if (tasks[i] is PendingLaunchTask plt && plt._uid == uid) return plt;
            return null;
        }

        /// <summary>Build a display-only PendingLaunchTask the suppressed client deck
        /// never advances. readyUpTime is passed non-NaN so the constructor skips the
        /// AI ready-up estimation path; Label/Info come from the host.</summary>
        private static PendingLaunchTask BuildDisplayTask(FlightDeck fd,
            System.Collections.Generic.IList<VehicleTypeOnBoard> vob, FlightDeckStateMessage.PendingTask t)
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
            task.launchAllowed      = t.LaunchAllowed;
            task.FlightDeckTaskLabel = t.Label;
            task.Info               = t.Info;
            return task;
        }
    }
}
