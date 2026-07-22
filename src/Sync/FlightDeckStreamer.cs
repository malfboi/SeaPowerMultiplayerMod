using System.Collections.Generic;
using System.Text;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// HOST-side flight-ops state streamer. For each carrier with a flight deck,
    /// emits a FlightDeckState snapshot (pending launch queue + squadron/vehicle
    /// availability + ammo) whenever it changes - the client mirrors it so its
    /// Flight Ops window shows the same aircraft being readied. Change-detected, so
    /// an idle or paused deck costs nothing. Called from the host streamer loop.
    /// </summary>
    public static class FlightDeckStreamer
    {
        private static readonly Dictionary<int, string> _lastSig = new();
        private static readonly StringBuilder _sb = new();

        public static void HostTick()
        {
            if (!CaptureState.HostCaptureActive) return;

            // PvP: only the remote player's own carriers (their taskforce, which is
            // the host's EnemyTaskforce after the side swap) - never leak the host
            // player's flight-ops queue to their opponent. Co-op streams all.
            bool pvp = Plugin.Instance.CfgPvP.Value;

            var vessels = UnitRegistry.Vessels;
            for (int i = 0; i < vessels.Count; i++)
            {
                var carrier = vessels[i];
                var fd = carrier?._obp?._flightDeck;
                if (fd == null) continue;
                if (pvp && carrier._taskforce != Globals._enemyTaskforce) continue;

                var msg = BuildSnapshot(carrier, fd);
                bool hasTasks = msg.Tasks.Count > 0;
                string sig = Signature(msg);
                _lastSig.TryGetValue(carrier.UniqueID, out var prev);
                bool had = !string.IsNullOrEmpty(prev);

                if (!hasTasks && !had) continue; // idle deck never carried a queue - stay silent
                if (sig == prev) continue;       // unchanged

                _lastSig[carrier.UniqueID] = hasTasks ? sig : "";
                NetworkManager.Instance.BroadcastToClients(msg, LiteNetLib.DeliveryMethod.ReliableOrdered);
                Telemetry.Count("v2.flightDeckSnapshot");
            }
        }

        private static FlightDeckStateMessage BuildSnapshot(ObjectBase carrier, FlightDeck fd)
        {
            var msg = new FlightDeckStateMessage
            {
                CarrierId   = carrier.UniqueID,
                CurrentAmmo = fd._currentAmmo,
            };

            var vob = fd._vehiclesOnBoard;
            for (int v = 0; v < vob.Count && v < 255; v++)
            {
                var vehicle = vob[v];
                if (vehicle == null) continue;
                msg.VehicleNumbers.Add(new FlightDeckStateMessage.VehicleCount
                { VehicleIdx = (byte)v, Numbers = (short)vehicle.Numbers });

                var squads = vehicle.Squadrons;
                for (int s = 0; s < squads.Count && s < 255; s++)
                {
                    if (squads[s] == null) continue;
                    msg.SquadronNumbers.Add(new FlightDeckStateMessage.SquadronCount
                    { VehicleIdx = (byte)v, SquadronIdx = (byte)s, Numbers = (short)squads[s].Numbers });
                }
            }

            var tasks = fd.FlightDeckTasks;
            for (int t = 0; t < tasks.Count; t++)
            {
                if (!(tasks[t] is PendingLaunchTask plt)) continue;
                if (msg.Tasks.Count >= 255) break;
                msg.Tasks.Add(new FlightDeckStateMessage.PendingTask
                {
                    Uid           = plt._uid,
                    VehicleIdx    = (byte)Clamp(plt._vehicleIndex),
                    LoadoutIdx    = (byte)Clamp(plt._loadoutIndex),
                    SquadronIdx   = (byte)Clamp(plt._squadronIndex),
                    CallsignIdx   = (byte)Clamp(plt._callsignIndex),
                    LaunchCount   = (short)plt.LaunchCount,
                    LaunchAllowed = plt.launchAllowed,
                    AwaitingLaunch = plt._stateMachine?.CurrentState is HandleAwaitSpawnTask,
                    Label         = plt.FlightDeckTaskLabel,
                    Info          = plt.Info,
                });
            }
            return msg;
        }

        private static int Clamp(int i) => i < 0 ? 0 : (i > 255 ? 255 : i);

        private static string Signature(FlightDeckStateMessage m)
        {
            _sb.Clear();
            _sb.Append(m.CurrentAmmo.ToString("F0")).Append('|');
            for (int i = 0; i < m.VehicleNumbers.Count; i++)
                _sb.Append(m.VehicleNumbers[i].VehicleIdx).Append(':')
                   .Append(m.VehicleNumbers[i].Numbers).Append(';');
            _sb.Append('|');
            for (int i = 0; i < m.SquadronNumbers.Count; i++)
            {
                var sn = m.SquadronNumbers[i];
                _sb.Append(sn.VehicleIdx).Append(',').Append(sn.SquadronIdx).Append(',')
                   .Append(sn.Numbers).Append(';');
            }
            _sb.Append('#');
            for (int i = 0; i < m.Tasks.Count; i++)
            {
                var t = m.Tasks[i];
                _sb.Append(t.Uid).Append(',').Append(t.LaunchCount).Append(',')
                   .Append(t.LaunchAllowed ? '1' : '0').Append(',')
                   .Append(t.AwaitingLaunch ? '1' : '0').Append(',')
                   .Append(t.Label).Append(',').Append(t.Info).Append(';');
            }
            return _sb.ToString();
        }

        public static void Reset() => _lastSig.Clear();
    }
}
