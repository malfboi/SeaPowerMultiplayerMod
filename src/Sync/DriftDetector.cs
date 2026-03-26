using System.Collections.Generic;
using LiteNetLib;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    public enum DriftTier : byte
    {
        Normal,
        Elevated,
        High,
        Critical,
    }

    /// <summary>
    /// Detects and corrects simulation drift between host and client.
    /// Accumulates per-unit deltas from StateApplier, computes aggregates,
    /// and sets adaptive correction parameters (soft sync).
    /// Triggers hard sync (full session reload) on catastrophic divergence.
    /// </summary>
    public static class DriftDetector
    {
        // ── Public metrics (read by UI) ──────────────────────────────────────
        public static float AvgPositionDrift { get; private set; }
        public static float MaxPositionDrift { get; private set; }
        public static float SpeedDriftAvg { get; private set; }
        public static float HeadingDriftAvg { get; private set; }
        public static int UnitCountDelta { get; private set; }
        public static int ProjectileCountDelta { get; private set; }
        public static float DriftTrend { get; private set; }
        public static DriftTier DriftLevel { get; private set; } = DriftTier.Normal;

        // ── Correction params (read by StateApplier) ─────────────────────────
        public static float EffectiveLerpFactor { get; private set; } = 0.1f;
        public static bool ShouldCorrectSpeed { get; private set; }
        public static float EffectiveDriftThreshold { get; private set; } = 5f;

        // ── Status ───────────────────────────────────────────────────────────
        public static int CorrectionCount { get; private set; }
        public static float MaxDeltaSeen { get; private set; }
        public static bool HardSyncRequested { get; private set; }

        // Fix #55: Category-specific drift averages
        public static float AirAvgDrift { get; private set; }
        public static float SurfaceAvgDrift { get; private set; }

        // ── Per-frame accumulators ───────────────────────────────────────────
        private static float _sumPosDrift;
        private static float _maxPosDrift;
        private static float _sumSpdDrift;
        private static float _sumHdgDrift;
        private static int _unitCount;

        // Fix #55: Aircraft-specific accumulators
        private static float _sumAirPosDrift;
        private static float _maxAirPosDrift;
        private static int _airUnitCount;

        // Fix #55: Surface/sub-specific accumulators (used for hard sync triggers)
        private static float _sumSurfacePosDrift;
        private static float _maxSurfacePosDrift;
        private static int _surfaceUnitCount;

        // ── EMA state ────────────────────────────────────────────────────────
        private const float EmaAlpha = 0.15f;

        // ── Hard sync timing ─────────────────────────────────────────────────
        private static float _hardSyncBreachStart = -1f;
        private static float _lastHardSyncTime = -999f;

        // ── Tier transition tracking ────────────────────────────────────────
        private static DriftTier _previousTier = DriftTier.Normal;

        // ── Infinite hard-sync loop suppression ─────────────────────────────
        private static int _lastHardSyncUnitDelta = -1;

        // ── Cumulative breach detection ─────────────────────────────────────
        private static readonly Queue<float> _breachTimestamps = new();

        // ── Projectile spawn health tracking ────────────────────────────────
        private static int _lastCheckedSpawnFailures;
        private static int _lastCheckedSpawnSuccesses;
        private static float _lastSpawnHealthCheckTime;
        private const float SpawnHealthCheckInterval = 15f;  // check every 15s

        // ── Defaults ─────────────────────────────────────────────────────────
        private const float DefaultLerpFactor = 0.1f;
        private const float DefaultDriftThreshold = 5f;

        /// <summary>Reset per-frame accumulators. Called before the unit loop.</summary>
        public static void BeginFrame()
        {
            _sumPosDrift = 0f;
            _maxPosDrift = 0f;
            _sumSpdDrift = 0f;
            _sumHdgDrift = 0f;
            _unitCount = 0;

            // Fix #55: Reset category accumulators
            _sumAirPosDrift = 0f;
            _maxAirPosDrift = 0f;
            _airUnitCount = 0;
            _sumSurfacePosDrift = 0f;
            _maxSurfacePosDrift = 0f;
            _surfaceUnitCount = 0;
        }

        /// <summary>Record drift for a single unit. Called per unit in Apply loop.</summary>
        public static void RecordUnit(float posDrift, float speedDrift, float headingDrift,
            bool isAircraft = false)
        {
            _sumPosDrift += posDrift;
            if (posDrift > _maxPosDrift) _maxPosDrift = posDrift;
            _sumSpdDrift += speedDrift;
            _sumHdgDrift += headingDrift;
            _unitCount++;

            if (posDrift > MaxDeltaSeen) MaxDeltaSeen = posDrift;

            // Fix #55: Category-specific accumulation
            if (isAircraft)
            {
                _sumAirPosDrift += posDrift;
                if (posDrift > _maxAirPosDrift) _maxAirPosDrift = posDrift;
                _airUnitCount++;
            }
            else
            {
                _sumSurfacePosDrift += posDrift;
                if (posDrift > _maxSurfacePosDrift) _maxSurfacePosDrift = posDrift;
                _surfaceUnitCount++;
            }
        }

        /// <summary>Increment correction counter. Called from StateApplier when a correction is applied.</summary>
        public static void RecordCorrection() => CorrectionCount++;

        /// <summary>Compute aggregates, determine tier, check hard sync. Called after unit loop.</summary>
        public static void EndFrame(int localUnitCount, int hostUnitCount, int localProjectileCount = 0, int hostProjectileCount = 0)
        {
            // PvP: puppets have no drift — skip entirely
            if (Plugin.Instance.CfgPvP.Value) return;
            // Co-op: client only
            if (!NetworkManager.Instance.IsConnectedClient) return;
            if (!NetworkManager.Instance.IsConnected) return;

            // Compute averages
            if (_unitCount > 0)
            {
                AvgPositionDrift = _sumPosDrift / _unitCount;
                MaxPositionDrift = _maxPosDrift;
                SpeedDriftAvg = _sumSpdDrift / _unitCount;
                HeadingDriftAvg = _sumHdgDrift / _unitCount;
            }
            else
            {
                AvgPositionDrift = 0f;
                MaxPositionDrift = 0f;
                SpeedDriftAvg = 0f;
                HeadingDriftAvg = 0f;
            }

            // Fix #55: Compute category averages
            AirAvgDrift = _airUnitCount > 0 ? _sumAirPosDrift / _airUnitCount : 0f;
            SurfaceAvgDrift = _surfaceUnitCount > 0 ? _sumSurfacePosDrift / _surfaceUnitCount : 0f;

            UnitCountDelta = Mathf.Abs(hostUnitCount - localUnitCount);
            ProjectileCountDelta = Mathf.Abs(hostProjectileCount - localProjectileCount);

            // Update EMA (~5s window at 2 Hz)
            DriftTrend = EmaAlpha * AvgPositionDrift + (1f - EmaAlpha) * DriftTrend;

            // Determine tier from config thresholds
            float elevated = Plugin.Instance.CfgSoftSyncElevated.Value;
            float high = Plugin.Instance.CfgSoftSyncHigh.Value;
            float critical = Plugin.Instance.CfgSoftSyncCritical.Value;

            if (AvgPositionDrift > critical)
            {
                DriftLevel = DriftTier.Critical;
                EffectiveLerpFactor = 1f;
                ShouldCorrectSpeed = true;
                EffectiveDriftThreshold = 0f;
            }
            else if (AvgPositionDrift > high)
            {
                DriftLevel = DriftTier.High;
                float t = Mathf.InverseLerp(high, critical, AvgPositionDrift);
                EffectiveLerpFactor = Mathf.Lerp(0.3f, 0.7f, t);
                ShouldCorrectSpeed = true;
                EffectiveDriftThreshold = 1f;
            }
            else if (AvgPositionDrift > elevated)
            {
                DriftLevel = DriftTier.Elevated;
                float t = Mathf.InverseLerp(elevated, high, AvgPositionDrift);
                EffectiveLerpFactor = Mathf.Lerp(0.1f, 0.3f, t);
                ShouldCorrectSpeed = true;
                EffectiveDriftThreshold = 2f;
            }
            else
            {
                DriftLevel = DriftTier.Normal;
                EffectiveLerpFactor = DefaultLerpFactor;
                ShouldCorrectSpeed = false;
                EffectiveDriftThreshold = DefaultDriftThreshold;
            }

            // Log tier transitions
            if (DriftLevel != _previousTier)
            {
                if (DriftLevel > _previousTier)
                    Plugin.Log.LogWarning($"[DriftDetector] Tier ESCALATED: {_previousTier} → {DriftLevel} " +
                    $"(AvgDrift={AvgPositionDrift:F1}, Surface={SurfaceAvgDrift:F1}, Air={AirAvgDrift:F1}, " +
                    $"Lerp={EffectiveLerpFactor:F2})");
                else
                    Plugin.Log.LogInfo($"[DriftDetector] Tier de-escalated: {_previousTier} → {DriftLevel} (AvgDrift={AvgPositionDrift:F1})");
                _previousTier = DriftLevel;
            }

            // Check projectile spawn health
            CheckSpawnHealth();

            // Check hard sync triggers
            CheckHardSync();
        }

        private static void CheckHardSync()
        {
            // Hard sync is a co-op concept. In PvP each side is authoritative for their own units.
            if (Plugin.Instance.CfgPvP.Value) return;

            float hardDrift = Plugin.Instance.CfgHardSyncDrift.Value;
            int hardUnitDelta = Plugin.Instance.CfgHardSyncUnitDelta.Value;
            float confirmSec = Plugin.Instance.CfgHardSyncConfirmSec.Value;
            float cooldown = Plugin.Instance.CfgHardSyncCooldown.Value;

            // Fix #55: Use surface-only drift for hard sync trigger.
            // Aircraft drift is handled by Fix #51's interpolation buffer.
            // Only ships/submarines with genuine desync should trigger hard sync.
            float effectiveDrift = _surfaceUnitCount > 0 ? SurfaceAvgDrift : AvgPositionDrift;
            bool driftBreached = effectiveDrift > hardDrift;
            bool unitBreached = UnitCountDelta >= hardUnitDelta;
            bool triggered = driftBreached || unitBreached;

            if (!triggered)
            {
                if (_hardSyncBreachStart >= 0f)
                    Plugin.Log.LogInfo("[DriftDetector] Hard sync breach cleared — conditions no longer met");
                _hardSyncBreachStart = -1f;
                HardSyncRequested = false;
                return;
            }

            // Suppress infinite loop: if only unit count is breached (no position drift)
            // and the delta is identical to what caused the last hard sync, skip.
            // This prevents repeated hard syncs when a counting bug persists after reload.
            if (unitBreached && !driftBreached && UnitCountDelta == _lastHardSyncUnitDelta)
            {
                Plugin.Log.LogWarning($"[DriftDetector] Hard sync SUPPRESSED — unit delta {UnitCountDelta} is identical to last hard sync trigger. Possible counting bug; avoiding infinite loop.");
                _hardSyncBreachStart = -1f;
                return;
            }

            // Cooldown check
            if (Time.time - _lastHardSyncTime < cooldown)
            {
                float remaining = cooldown - (Time.time - _lastHardSyncTime);
                Plugin.Log.LogDebug($"[DriftDetector] Hard sync conditions met but on cooldown ({remaining:F1}s remaining)");
                return;
            }

            // Build reason string for logging (after cooldown check to avoid allocations)
            string reason;
            if (driftBreached && unitBreached)
                reason = $"avg position drift {effectiveDrift:F1} > {hardDrift} AND unit count mismatch {UnitCountDelta} >= {hardUnitDelta} (surface={SurfaceAvgDrift:F1}, air={AirAvgDrift:F1})";
            else if (driftBreached)
                reason = $"avg position drift {effectiveDrift:F1} > {hardDrift} (surface={SurfaceAvgDrift:F1}, air={AirAvgDrift:F1})";
            else
                reason = $"unit count mismatch: local has {UnitCountDelta} more/fewer units than host (threshold={hardUnitDelta})";

            if (_hardSyncBreachStart < 0f)
            {
                _hardSyncBreachStart = Time.time;

                // Record breach timestamp for cumulative detection
                _breachTimestamps.Enqueue(Time.time);
                float window = Plugin.Instance.CfgHardSyncBreachWindowSec.Value;
                while (_breachTimestamps.Count > 0 && Time.time - _breachTimestamps.Peek() > window)
                    _breachTimestamps.Dequeue();

                int threshold = Plugin.Instance.CfgHardSyncBreachCountThreshold.Value;
                if (_breachTimestamps.Count >= threshold)
                {
                    // Cumulative breach threshold reached — trigger immediately
                    int breachCount = _breachTimestamps.Count;
                    HardSyncRequested = true;
                    _lastHardSyncTime = Time.time;
                    _hardSyncBreachStart = -1f;
                    _lastHardSyncUnitDelta = UnitCountDelta;
                    _breachTimestamps.Clear();

                    Plugin.Log.LogWarning($"[DriftDetector] HARD SYNC TRIGGERED by cumulative breaches — {breachCount} breaches in {window:F0}s window (threshold={threshold}). Current: {reason}");
                    Plugin.Log.LogWarning($"[DriftDetector]   Metrics: AvgDrift={AvgPositionDrift:F1}, MaxDrift={MaxPositionDrift:F1}, SpeedDrift={SpeedDriftAvg:F1}, UnitDelta={UnitCountDelta}, Tier={DriftLevel}, Trend={DriftTrend:F1}");
                    NetworkManager.Instance.SendToServer(new GameEventMessage
                    {
                        EventType = GameEventType.HardSyncRequest,
                    }, DeliveryMethod.ReliableOrdered);
                    return;
                }

                Plugin.Log.LogWarning($"[DriftDetector] Hard sync breach started — {reason}. Must persist for {confirmSec:F1}s to trigger. (cumulative: {_breachTimestamps.Count}/{threshold} in {window:F0}s window)");
                return;
            }

            float elapsed = Time.time - _hardSyncBreachStart;
            if (elapsed >= confirmSec)
            {
                HardSyncRequested = true;
                _lastHardSyncTime = Time.time;
                _hardSyncBreachStart = -1f;
                _lastHardSyncUnitDelta = UnitCountDelta;

                Plugin.Log.LogWarning($"[DriftDetector] HARD SYNC TRIGGERED after {elapsed:F1}s breach — {reason}");
                Plugin.Log.LogWarning($"[DriftDetector]   Metrics: AvgDrift={AvgPositionDrift:F1}, MaxDrift={MaxPositionDrift:F1}, SpeedDrift={SpeedDriftAvg:F1}, UnitDelta={UnitCountDelta}, Tier={DriftLevel}, Trend={DriftTrend:F1}");
                NetworkManager.Instance.SendToServer(new GameEventMessage
                {
                    EventType = GameEventType.HardSyncRequest,
                }, DeliveryMethod.ReliableOrdered);
            }
        }

        private static void CheckSpawnHealth()
        {
            if (Time.unscaledTime - _lastSpawnHealthCheckTime < SpawnHealthCheckInterval)
                return;
            _lastSpawnHealthCheckTime = Time.unscaledTime;

            int currentFailures = ProjectileIdMapper.SpawnFailureCount;
            int currentSuccesses = ProjectileIdMapper.SpawnSuccessCount;
            int newFailures = currentFailures - _lastCheckedSpawnFailures;
            int newSuccesses = currentSuccesses - _lastCheckedSpawnSuccesses;
            _lastCheckedSpawnFailures = currentFailures;
            _lastCheckedSpawnSuccesses = currentSuccesses;

            // If 3+ spawn failures occurred in the last check interval with no successes,
            // that's a sign of serious simulation divergence — inject a synthetic breach
            if (newFailures >= 3 && newSuccesses == 0)
            {
                Plugin.Log.LogWarning($"[DriftDetector] Projectile spawn health POOR: {newFailures} failures, {newSuccesses} successes in last {SpawnHealthCheckInterval}s — injecting synthetic breach");
                _breachTimestamps.Enqueue(Time.time);
            }
        }

        /// <summary>Clear all state. Called after hard sync or disconnect.</summary>
        public static void Reset()
        {
            AvgPositionDrift = 0f;
            MaxPositionDrift = 0f;
            SpeedDriftAvg = 0f;
            HeadingDriftAvg = 0f;
            UnitCountDelta = 0;
            ProjectileCountDelta = 0;
            DriftTrend = 0f;
            DriftLevel = DriftTier.Normal;
            EffectiveLerpFactor = DefaultLerpFactor;
            ShouldCorrectSpeed = false;
            EffectiveDriftThreshold = DefaultDriftThreshold;
            CorrectionCount = 0;
            MaxDeltaSeen = 0f;
            HardSyncRequested = false;
            _sumPosDrift = 0f;
            _maxPosDrift = 0f;
            _sumSpdDrift = 0f;
            _sumHdgDrift = 0f;
            _unitCount = 0;
            // Fix #55: Reset category accumulators and properties
            _sumAirPosDrift = 0f;
            _maxAirPosDrift = 0f;
            _airUnitCount = 0;
            _sumSurfacePosDrift = 0f;
            _maxSurfacePosDrift = 0f;
            _surfaceUnitCount = 0;
            AirAvgDrift = 0f;
            SurfaceAvgDrift = 0f;
            _hardSyncBreachStart = -1f;
            _previousTier = DriftTier.Normal;
            _lastHardSyncUnitDelta = -1;
            _breachTimestamps.Clear();
            _lastCheckedSpawnFailures = 0;
            _lastCheckedSpawnSuccesses = 0;
            _lastSpawnHealthCheckTime = 0f;
            // Don't reset _lastHardSyncTime — preserve cooldown across resets
        }
    }
}
