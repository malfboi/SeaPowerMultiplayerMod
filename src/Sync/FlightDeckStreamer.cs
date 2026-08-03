using System.Collections.Generic;
using System.Text;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// HOST-side flight-ops state streamer. For each carrier with a flight deck,
    /// emits a FlightDeckState snapshot (the full task queue - pending launches plus
    /// the active launch/recovery/cooldown rows - and squadron/vehicle availability
    /// + ammo) whenever it changes - the client mirrors it so its Flight Ops window
    /// shows the same queue. Change-detected, so an idle deck costs one initial
    /// snapshot and nothing after. Called from the host streamer loop.
    /// </summary>
    public static class FlightDeckStreamer
    {
        private static readonly Dictionary<int, string> _lastSig = new();
        private static readonly Dictionary<int, float>  _nextSendAt = new();
        private static readonly StringBuilder _sb = new();
        private static readonly List<FlightDeckStateMessage> _chunks = new();

        // Flight-ops UI does not need the 10 Hz unit tick - during ready-up the
        // Info countdown changes constantly, which would re-send every tick.
        private const float MinSendIntervalSec = 1f;

        // Hard per-message budget. Reliable packets above the ~1000-byte MTU floor
        // are fragmented by LiteNetLib and arrive corrupted past the first fragment
        // in the game's Mono runtime (verified live), so a snapshot bigger than
        // this is split across multiple chunk messages instead.
        private const int ChunkBudgetBytes = 850;

        public static void HostTick()
        {
            if (!CaptureState.HostCaptureActive) return;

            // PvP: only the remote player's own carriers (their taskforce, which is
            // the host's EnemyTaskforce after the side swap) - never leak the host
            // player's flight-ops queue to their opponent. Co-op streams all.
            bool pvp = Plugin.Instance.CfgPvP.Value;
            float now = UnityEngine.Time.unscaledTime;

            var vessels = UnitRegistry.Vessels;
            for (int i = 0; i < vessels.Count; i++)
            {
                var carrier = vessels[i];
                var fd = carrier?._obp?._flightDeck;
                if (fd == null) continue;
                if (pvp && carrier._taskforce != Globals._enemyTaskforce) continue;
                if (_nextSendAt.TryGetValue(carrier.UniqueID, out var next) && now < next) continue;

                var msg = BuildSnapshot(carrier, fd);
                string sig = Signature(msg);
                if (_lastSig.TryGetValue(carrier.UniqueID, out var prev) && sig == prev)
                    continue; // unchanged

                _lastSig[carrier.UniqueID] = sig;
                _nextSendAt[carrier.UniqueID] = now + MinSendIntervalSec;

                Chunk(msg);
                for (int c = 0; c < _chunks.Count; c++)
                    NetworkManager.Instance.BroadcastToClients(_chunks[c], LiteNetLib.DeliveryMethod.ReliableOrdered);
                Telemetry.Count("v2.flightDeckSnapshot");
                _chunks.Clear();
            }
        }

        // ── Chunking ────────────────────────────────────────────────────────────

        private static int StrBytes(string s) => (s?.Length ?? 0) * 2 + 5; // worst-case UTF-8 + length prefix
        private static int RowBytes(FlightDeckStateMessage.TaskRow t) =>
            34 + StrBytes(t.Label) + StrBytes(t.Info) + StrBytes(t.AircraftType) + StrBytes(t.SquadronName);

        /// <summary>Split a full snapshot into sub-MTU messages (into _chunks).
        /// Availability entries and task rows are independent items, so they can be
        /// distributed freely; the client reassembles the union before applying.</summary>
        private static void Chunk(FlightDeckStateMessage full)
        {
            var cur = NewChunk(full);
            int size = 20;

            void CutIfNeeded(int itemBytes)
            {
                if (size + itemBytes <= ChunkBudgetBytes) return;
                _chunks.Add(cur);
                cur = NewChunk(full);
                size = 20;
            }

            for (int i = 0; i < full.VehicleNumbers.Count; i++)
            {
                CutIfNeeded(3);
                cur.VehicleNumbers.Add(full.VehicleNumbers[i]);
                size += 3;
            }
            for (int i = 0; i < full.SquadronNumbers.Count; i++)
            {
                CutIfNeeded(4);
                cur.SquadronNumbers.Add(full.SquadronNumbers[i]);
                size += 4;
            }
            for (int i = 0; i < full.Tasks.Count; i++)
            {
                int rb = RowBytes(full.Tasks[i]);
                CutIfNeeded(rb);
                cur.Tasks.Add(full.Tasks[i]);
                size += rb;
            }
            _chunks.Add(cur);

            for (int i = 0; i < _chunks.Count; i++)
            {
                _chunks[i].ChunkIdx   = (byte)i;
                _chunks[i].ChunkCount = (byte)_chunks.Count;
            }
        }

        private static FlightDeckStateMessage NewChunk(FlightDeckStateMessage full) =>
            new FlightDeckStateMessage { CarrierId = full.CarrierId, CurrentAmmo = full.CurrentAmmo };

        // FlightDeckTask.CrewSkill does not exist in every game build; a direct call
        // MissingMethodExceptions the whole streamer tick there (no snapshot ever
        // sent), so the cosmetic property is read via reflection and degrades to 0.
        private static readonly System.Reflection.PropertyInfo _crewSkillProp =
            HarmonyLib.AccessTools.Property(typeof(FlightDeckTask), "CrewSkill");

        private static byte ReadCrewSkill(FlightDeckTask fdt)
        {
            if (_crewSkillProp == null) return 0;
            try { return System.Convert.ToByte(_crewSkillProp.GetValue(fdt, null)); }
            catch { return 0; }
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
                var fdt = tasks[t];
                if (fdt == null) continue;
                if (msg.Tasks.Count >= 255) break;
                if (fdt is PendingLaunchTask plt)
                {
                    msg.Tasks.Add(new FlightDeckStateMessage.TaskRow
                    {
                        IsPending     = true,
                        Uid           = plt._uid,
                        VehicleIdx    = (byte)Clamp(plt._vehicleIndex),
                        LoadoutIdx    = (byte)Clamp(plt._loadoutIndex),
                        SquadronIdx   = (byte)Clamp(plt._squadronIndex),
                        CallsignIdx   = (byte)Clamp(plt._callsignIndex),
                        LaunchCount   = (short)plt.LaunchCount,
                        DeckSpots     = (short)plt.AssignedDeckSpots,
                        GroundCrew    = (short)plt.AssignedGroundCrew,
                        LaunchAllowed = plt.launchAllowed,
                        AwaitingLaunch = plt._stateMachine?.CurrentState is HandleAwaitSpawnTask,
                        Label         = plt.FlightDeckTaskLabel,
                        Info          = plt.Info,
                    });
                }
                else
                {
                    // Launch / recovery / cooldown rows: display-only client-side.
                    msg.Tasks.Add(new FlightDeckStateMessage.TaskRow
                    {
                        IsPending    = false,
                        Uid          = fdt._uid,
                        Label        = fdt.FlightDeckTaskLabel,
                        Info         = fdt.Info,
                        AircraftType = fdt.AircraftType,
                        SquadronName = fdt.Squadron,
                        CrewSkill    = ReadCrewSkill(fdt),
                    });
                }
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
                _sb.Append(t.IsPending ? 'P' : 'D').Append(t.Uid).Append(',')
                   .Append(t.LaunchCount).Append(',')
                   .Append(t.DeckSpots).Append(',').Append(t.GroundCrew).Append(',')
                   .Append(t.LaunchAllowed ? '1' : '0').Append(',')
                   .Append(t.AwaitingLaunch ? '1' : '0').Append(',')
                   .Append(t.Label).Append(',').Append(t.Info).Append(';');
            }
            return _sb.ToString();
        }

        public static void Reset()
        {
            _lastSig.Clear();
            _nextSendAt.Clear();
        }
    }
}
