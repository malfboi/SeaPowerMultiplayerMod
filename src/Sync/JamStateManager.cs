using System.Collections.Generic;
using LiteNetLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Replicates offensive ECM jam assignments, host → client. See
    /// <see cref="JamStateMessage"/> for why the emitter masks are not enough.
    ///
    /// The host collects the assignments its sim holds and sends the whole set
    /// whenever it changes (plus a 10 s heartbeat). The client re-imposes that set
    /// verbatim: assignments it is told about are written onto its own copies of
    /// the jammers, and any local assignment the host does not report is torn down.
    /// From there the client's own <c>SensorSystemECM.OnFixedUpdate</c> does the
    /// range / line-of-sight / cone work locally, so the jam cone, the degraded
    /// radar ranges and the ESM strobes all follow without further traffic.
    /// </summary>
    public static class JamStateManager
    {
        /// <summary>Systems are addressed by index, matching SensorStateManager's
        /// ceiling so the two agree about what is addressable.</summary>
        private const int MaxSensorsPerUnit = 64;

        /// <summary>21 B per entry - 40 keeps a chunk under the ~1000 B reliable
        /// packet floor the game's Mono runtime imposes.</summary>
        private const int MaxEntriesPerPacket = 40;

        private const float FullResendInterval = 10f;

        // ── Shared: read the local sim's assignments ──────────────────────────

        /// <summary>Every offensive jam assignment on this machine. Used by the host
        /// to build the snapshot and by the client to find local assignments the
        /// snapshot does not mention.</summary>
        private static void Collect(List<JamStateMessage.Entry> into)
        {
            into.Clear();
            var all = UnitRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var unit = all[i];
                if (unit == null || unit.UniqueID == 0) continue;
                // Weapon-mounted ECM re-points itself at its own nose every fixed
                // update (SensorSystemECM.OnFixedUpdate), so it would churn the
                // snapshot for no purpose - and weapons are host-simulated and
                // streamed whole anyway.
                if (unit is WeaponBase) continue;

                var sensors = unit._obp?._sensorSystems;
                if (sensors == null) continue;

                int count = Mathf.Min(sensors.Count, MaxSensorsPerUnit);
                for (int b = 0; b < count; b++)
                {
                    if (!(sensors[b] is SensorSystemECM ecm)) continue;

                    var target = ecm._associatedTarget;
                    bool hasTarget = target != null && !target.IsDestroyed && target.UniqueID != 0;
                    if (!hasTarget && !ecm._geoPositionTargeted) continue;

                    var entry = new JamStateMessage.Entry
                    {
                        UnitId    = unit.UniqueID,
                        SensorIdx = (byte)b,
                    };
                    // A unit target wins over a bearing: JamTask clears
                    // _geoPositionTargeted when it assigns one, and the game's own
                    // OnFixedUpdate reads them in that order.
                    if (hasTarget)
                    {
                        entry.TargetId = target!.UniqueID;
                    }
                    else
                    {
                        entry.Lon    = (float)ecm._associatedTargetPosition._longitude;
                        entry.Lat    = (float)ecm._associatedTargetPosition._latitude;
                        entry.Height = (float)ecm._associatedTargetPosition._height;
                    }
                    into.Add(entry);
                }
            }
        }

        // ── Host capture ──────────────────────────────────────────────────────

        private static readonly List<JamStateMessage.Entry> _current  = new(16);
        private static readonly List<JamStateMessage.Entry> _lastSent = new(16);
        private static readonly JamStateMessage _msg = new();
        private static float _nextFullResend;

        public static void HostBroadcast()
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (!NetworkManager.Instance.IsEstablished) return;

            Collect(_current);

            bool changed = !Same(_current, _lastSent);
            bool heartbeat = Time.unscaledTime >= _nextFullResend;
            if (!changed && !heartbeat) return;

            // Assignments change rarely and are exactly what goes wrong, so each
            // change is logged outright rather than behind a debug flag.
            if (changed)
                Plugin.Log.LogInfo($"[Jam] Host assignments: {Describe(_current)}");

            _nextFullResend = Time.unscaledTime + FullResendInterval;
            Send(_current);

            _lastSent.Clear();
            _lastSent.AddRange(_current);
        }

        private static void Send(List<JamStateMessage.Entry> entries)
        {
            int chunks = Mathf.Max(1, Mathf.CeilToInt(entries.Count / (float)MaxEntriesPerPacket));
            for (int c = 0; c < chunks; c++)
            {
                _msg.Reset();
                _msg.ChunkIdx   = (byte)c;
                _msg.ChunkCount = (byte)chunks;

                int start = c * MaxEntriesPerPacket;
                int end   = Mathf.Min(start + MaxEntriesPerPacket, entries.Count);
                for (int i = start; i < end; i++) _msg.Entries.Add(entries[i]);

                NetworkManager.Instance.BroadcastToClients(_msg, DeliveryMethod.ReliableOrdered);
            }
        }

        private static bool Same(List<JamStateMessage.Entry> a, List<JamStateMessage.Entry> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                // Same walk order on both lists (UnitRegistry order, then sensor
                // index), so position-wise comparison is enough.
                if (a[i].UnitId != b[i].UnitId) return false;
                if (a[i].SensorIdx != b[i].SensorIdx) return false;
                if (a[i].TargetId != b[i].TargetId) return false;
                if (!Mathf.Approximately(a[i].Lon, b[i].Lon)) return false;
                if (!Mathf.Approximately(a[i].Lat, b[i].Lat)) return false;
            }
            return true;
        }

        private static string Describe(List<JamStateMessage.Entry> entries)
        {
            if (entries.Count == 0) return "(none)";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                if (sb.Length > 0) sb.Append(", ");
                var e = entries[i];
                var unit = StateSerializer.FindById(e.UnitId);
                sb.Append(unit != null ? unit.name : e.UnitId.ToString());
                sb.Append('[').Append(e.SensorIdx).Append("] → ");
                if (e.TargetId != 0)
                {
                    var target = StateSerializer.FindById(e.TargetId);
                    sb.Append(target != null ? target.name : e.TargetId.ToString());
                }
                else
                {
                    sb.Append($"bearing {e.Lat:F3},{e.Lon:F3}");
                }
            }
            return sb.ToString();
        }

        // ── Client apply ──────────────────────────────────────────────────────

        private static readonly List<JamStateMessage.Entry> _inbox = new(16);
        private static readonly List<JamStateMessage.Entry> _local = new(16);
        private static readonly HashSet<(int, byte)> _desired = new();

        public static void ApplyReceived(JamStateMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            // Chunk 0 restarts the accumulation; ReliableOrdered guarantees the rest
            // of the train follows it contiguously.
            if (msg.ChunkIdx == 0) _inbox.Clear();
            _inbox.AddRange(msg.Entries);
            if (msg.ChunkIdx + 1 < msg.ChunkCount) return;

            // Mid-load there is nothing to resolve ids against and half the scene is
            // partially initialised. Dropping the snapshot costs at most one
            // heartbeat, and the host resends the whole set anyway.
            if (SessionManager.SceneLoading) { _inbox.Clear(); return; }

            try { Apply(_inbox); }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Jam] Apply failed: {ex.Message}");
            }
        }

        private static void Apply(List<JamStateMessage.Entry> entries)
        {
            _desired.Clear();
            for (int i = 0; i < entries.Count; i++)
                _desired.Add((entries[i].UnitId, entries[i].SensorIdx));

            // Anything jamming locally that the host does not report has to stop -
            // a stale assignment keeps registering itself on victims every fixed
            // update. This covers a jam order the client ran optimistically and the
            // host then refused, which no per-entry write would ever undo.
            Collect(_local);
            for (int i = 0; i < _local.Count; i++)
            {
                var stale = _local[i];
                if (_desired.Contains((stale.UnitId, stale.SensorIdx))) continue;
                var ecm = FindEcm(stale.UnitId, stale.SensorIdx);
                if (ecm?._ecm == null) continue;
                ecm._ecm.ceaseOffensiveJam();
                Plugin.Log.LogInfo($"[Jam] Cleared local assignment: id={stale.UnitId} sensor={stale.SensorIdx}");
            }

            for (int i = 0; i < entries.Count; i++) ApplyOne(entries[i]);
        }

        private static void ApplyOne(JamStateMessage.Entry e)
        {
            var ecm = FindEcm(e.UnitId, e.SensorIdx);
            if (ecm == null)
            {
                // The jammer's replica may simply not exist yet. The next snapshot
                // repairs it, so this is a note rather than a failure.
                Plugin.Log.LogInfo($"[Jam] id={e.UnitId} sensor={e.SensorIdx} not resolvable locally - assignment deferred");
                return;
            }

            if (e.TargetId != 0)
            {
                var target = StateSerializer.FindById(e.TargetId);
                if (target == null)
                {
                    Plugin.Log.LogInfo($"[Jam] target id={e.TargetId} not found locally - " +
                                       $"{ecm.DisplayName.Value} assignment deferred");
                    return;
                }
                if (ReferenceEquals(ecm._associatedTarget, target) && !ecm._geoPositionTargeted) return;

                // Tear the old assignment down first: ceaseOffensiveJam is the game's
                // own teardown and takes the system off every victim's
                // _ECMSystemsTryingToJamMe, which nothing else does once the
                // assignment has been replaced.
                ecm._ecm?.ceaseOffensiveJam();
                ecm._associatedTarget      = target;
                ecm._geoPositionTargeted   = false;
                // Same registration JamTask.DoJamming performs; OnFixedUpdate
                // maintains the rest (range, LOS, cone) from here.
                if (!target._ECMSystemsTryingToJamMe.Contains(ecm))
                    target._ECMSystemsTryingToJamMe.Add(ecm);

                Plugin.Log.LogInfo($"[Jam] {ecm.DisplayName.Value} on id={e.UnitId} → jamming {target.name}");
                return;
            }

            var geo = new GeoPosition { _longitude = e.Lon, _latitude = e.Lat, _height = e.Height };
            if (ecm._geoPositionTargeted && ecm._associatedTarget == null
                && Mathf.Approximately((float)ecm._associatedTargetPosition._longitude, e.Lon)
                && Mathf.Approximately((float)ecm._associatedTargetPosition._latitude, e.Lat))
                return;

            ecm._ecm?.ceaseOffensiveJam();
            ecm._associatedTargetPosition = geo;
            ecm._geoPositionTargeted      = true;

            Plugin.Log.LogInfo($"[Jam] {ecm.DisplayName.Value} on id={e.UnitId} → jamming bearing {e.Lat:F3},{e.Lon:F3}");
        }

        private static SensorSystemECM? FindEcm(int unitId, byte sensorIdx)
        {
            var sensors = StateSerializer.FindById(unitId)?._obp?._sensorSystems;
            if (sensors == null || sensorIdx >= sensors.Count) return null;
            return sensors[sensorIdx] as SensorSystemECM;
        }

        public static void Reset()
        {
            _current.Clear();
            _lastSent.Clear();
            _inbox.Clear();
            _local.Clear();
            _desired.Clear();
            _nextFullResend = 0f;
        }
    }
}
