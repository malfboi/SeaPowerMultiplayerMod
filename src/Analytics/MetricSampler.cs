using System;
using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Rolls the mod's existing measurements up into one snapshot line every
    /// <see cref="EmitSec"/> seconds.
    ///
    /// Nothing here measures anything new: RTT, packet loss, drift, prediction
    /// error and the named counters are all already computed for the Ctrl+F9
    /// overlay and then thrown away. This is the part that keeps them.
    ///
    /// Sampled at 1 Hz rather than per-frame - min/avg/max/p95 over a 10 s window
    /// answers every question a raw 10 Hz series would, at a tenth the bytes. The
    /// exception is frame time, which is sampled every frame because a 200 ms
    /// hitch is invisible at 1 Hz.
    ///
    /// Main thread only: everything it reads (Telemetry, StateApplier,
    /// ReplicaRegistry) is main-thread-only by contract.
    /// </summary>
    internal static class MetricSampler
    {
        private const float SampleSec = 1f;
        private const float EmitSec   = 10f;
        private const float HitchSec  = 0.1f;   // a frame over 100 ms is a visible stutter

        private static float _nextSample;
        private static float _nextEmit;
        private static float _windowStart;

        // RTT samples over the window (1 Hz, so ~10 entries)
        private static readonly List<int> _rtt = new(16);
        private static float _lossSum;
        private static int   _lossN;

        // Frame time, sampled every frame
        private static float _ftSum, _ftWorst;
        private static int   _ftFrames, _hitches;

        // Deltas
        private static long _prevBytesIn, _prevBytesOut;
        private static long _prevErrors, _prevWarnings;
        private static readonly Dictionary<string, long> _prevCounters = new();

        internal static void Reset()
        {
            _nextSample = _nextEmit = 0f;
            _windowStart = Time.realtimeSinceStartup;
            _rtt.Clear();
            _lossSum = 0f; _lossN = 0;
            _ftSum = _ftWorst = 0f; _ftFrames = _hitches = 0;
            _prevBytesIn = Telemetry.TotalBytesIn;
            _prevBytesOut = Telemetry.TotalBytesOut;
            _prevErrors = Analytics.ErrorCount;
            _prevWarnings = Analytics.WarningCount;
            _prevCounters.Clear();
            foreach (var kv in Telemetry.Counters) _prevCounters[kv.Key] = kv.Value;
        }

        /// <summary>Per-frame. Cheap enough to run unconditionally once opted in.</summary>
        internal static void Tick()
        {
            float dt = Time.unscaledDeltaTime;
            _ftSum += dt;
            _ftFrames++;
            if (dt > _ftWorst) _ftWorst = dt;
            if (dt > HitchSec) _hitches++;

            float now = Time.realtimeSinceStartup;

            if (now >= _nextSample)
            {
                _nextSample = now + SampleSec;
                SampleOnce();
            }

            if (now >= _nextEmit)
            {
                // Skip the very first emit: the window would be a fraction of a
                // second of startup noise.
                if (_nextEmit > 0f) Emit(now);
                _nextEmit = now + EmitSec;
            }
        }

        private static void SampleOnce()
        {
            var nm = NetworkManager.Instance;
            if (!nm.IsConnected) return;

            _rtt.Add(nm.LastRttMs);

            float loss = nm.PacketLossPct;
            if (loss >= 0f) { _lossSum += loss; _lossN++; }
        }

        private static void Emit(float now)
        {
            var sink = LogRingSink.Active;
            if (sink == null) return;

            float window = Mathf.Max(0.001f, now - _windowStart);
            _windowStart = now;

            var nm = NetworkManager.Instance;
            var j = new Json().Obj();
            j.Str("t", "m").Num("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Num("w", window);

            // ── Network ──────────────────────────────────────────────────────
            j.Sub("rtt");
            if (_rtt.Count > 0)
            {
                _rtt.Sort();
                long sum = 0;
                foreach (int v in _rtt) sum += v;
                j.Num("a", (double)sum / _rtt.Count)
                 .Num("p95", _rtt[Mathf.Min(_rtt.Count - 1, Mathf.FloorToInt(_rtt.Count * 0.95f))])
                 .Num("mx", _rtt[_rtt.Count - 1]);
            }
            j.End();

            // Steam exposes no packet counters, so loss is genuinely unknown
            // there rather than zero. Null says so.
            if (_lossN > 0) j.Num("loss", _lossSum / _lossN); else j.Null("loss");

            long bin  = Telemetry.TotalBytesIn;
            long bout = Telemetry.TotalBytesOut;
            j.Num("bin",  (bin  - _prevBytesIn)  / window)
             .Num("bout", (bout - _prevBytesOut) / window);
            _prevBytesIn = bin;
            _prevBytesOut = bout;

            var (_, _, sendMax) = Telemetry.FrameSendStats();
            j.Num("sfmx", sendMax);

            // ── Performance ──────────────────────────────────────────────────
            j.Sub("fps")
             .Num("a", _ftFrames > 0 ? _ftFrames / Mathf.Max(0.001f, _ftSum) : 0f)
             .Num("mn", _ftWorst > 0f ? 1f / _ftWorst : 0f)
             .End();
            j.Num("hitch", _hitches);
            j.Num("mem", GC.GetTotalMemory(false) / (1024 * 1024));
            _ftSum = _ftWorst = 0f; _ftFrames = _hitches = 0;

            // Errors and warnings raised during this window. Cumulative counters
            // diffed here, so a spike lands on the snapshot it happened in and
            // can be plotted against mission time rather than only wall clock.
            long errs = Analytics.ErrorCount, warns = Analytics.WarningCount;
            j.Num("err", errs - _prevErrors).Num("warn", warns - _prevWarnings);
            _prevErrors = errs; _prevWarnings = warns;

            // ── Sync health ──────────────────────────────────────────────────
            j.Sub("drift")
             .Num("sa", StateApplier.ShipDriftAvg).Num("sm", StateApplier.ShipDriftMax)
             .Num("sn", StateApplier.ShipDriftCount)
             .Num("aa", StateApplier.AirDriftAvg).Num("am", StateApplier.AirDriftMax)
             .Num("an", StateApplier.AirDriftCount)
             .End();

            j.Sub("perr")
             .Num("sa", StateApplier.ShipPredictErrAvg).Num("sm", StateApplier.ShipPredictErrMax)
             .Num("aa", StateApplier.AirPredictErrAvg).Num("am", StateApplier.AirPredictErrMax)
             .End();

            // The truest network-quality signal the mod has: arrival jitter and
            // the host cadence the client actually observes, as opposed to the
            // rate the host was configured to send at.
            j.Num("jit", UnitReplicaDriver.ArrivalJitterSec)
             .Num("cad", UnitReplicaDriver.HostCadenceSec);

            j.Num("rep", ReplicaRegistry.Count)
             .Num("wrep", WeaponReplicaDriver.ActiveReplicas)
             .Str("hs", nm.Handshake.ToString())
             .Str("sim", SimSyncManager.CurrentState.ToString());

            try { j.Num("tc", GameTime.TimeCompression); } catch { }

            // Where in the mission this snapshot sits. `mis` is the shared sync
            // clock, so it is what lines the host's and client's uploads up with
            // each other; `misEl` is elapsed, which wall clock cannot give at
            // time compression. Both null outside a mission.
            j.Num("mis", TimeSyncManager.MissionSeconds())
             .Num("misEl", Analytics.MissionElapsedSec);

            // ── Counter deltas (non-zero only) ───────────────────────────────
            j.Sub("c");
            foreach (var kv in Telemetry.Counters)
            {
                _prevCounters.TryGetValue(kv.Key, out long prev);
                long d = kv.Value - prev;
                if (d != 0) j.Num(kv.Key, d);
                _prevCounters[kv.Key] = kv.Value;
            }
            j.End();

            sink.PushStructured(LogKind.Metric, j.End().ToString());
        }
    }
}
