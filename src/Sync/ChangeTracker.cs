using System.Collections.Generic;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Tracks per-entity "last sent" state to avoid resending unchanged units every tick.
    /// Changed entities are included immediately; idle entities get a staggered heartbeat.
    /// Projectiles are not tracked here — they always send at full rate.
    /// </summary>
    public static class ChangeTracker
    {
        private static readonly Dictionary<int, UnitState> _lastSent = new();
        private static readonly Dictionary<int, float> _nextHeartbeat = new();
        private static readonly Dictionary<int, float> _phaseOffset = new();
        private static int _staggerCounter;

        // Idle entities resend at this interval (seconds, unscaled time)
        private const float HeartbeatInterval = 1.5f;

        // Spread initial heartbeats across this many slots to avoid bursts
        private const int StaggerBuckets = 15;

        // ── Change thresholds ────────────────────────────────────────────────
        private const float PositionThreshold  = 0.00001f; // ~1 m in geo coords (lon/lat)
        private const float HeightThreshold    = 0.5f;
        private const float HeadingThreshold   = 0.5f;     // degrees
        private const float SpeedThreshold     = 0.2f;     // knots
        private const float RudderThreshold    = 1.0f;     // degrees
        private const float IntegrityThreshold = 1.0f;     // percent
        private const float AltitudeThreshold  = 5.0f;
        private const float PitchRollThreshold = 2.0f;     // degrees

        /// <summary>
        /// Given the full list of captured units, return the set of entity IDs
        /// that should be included in this tick's message. Updates internal
        /// tracking state for included entities.
        /// </summary>
        public static HashSet<int> ComputeDirtySet(List<UnitState> allUnits, float now)
        {
            var dirty = new HashSet<int>(allUnits.Count);

            for (int i = 0; i < allUnits.Count; i++)
            {
                var current = allUnits[i];
                int id = current.EntityId;

                bool include;
                if (!_lastSent.TryGetValue(id, out var previous))
                {
                    // New entity — always send, assign staggered phase offset
                    include = true;
                    float phase = HeartbeatInterval * (_staggerCounter++ % StaggerBuckets) / StaggerBuckets;
                    _phaseOffset[id] = phase;
                    _nextHeartbeat[id] = now + phase;
                }
                else if (HasChanged(current, previous))
                {
                    include = true;
                }
                else if (now >= (_nextHeartbeat.TryGetValue(id, out var hb) ? hb : 0f))
                {
                    // Heartbeat expired — resend even though unchanged
                    include = true;
                }
                else
                {
                    include = false;
                }

                if (include)
                {
                    dirty.Add(id);
                    _lastSent[id] = current;
                    // Advance to next heartbeat aligned to this entity's phase,
                    // so idle units stay distributed across ticks instead of clumping.
                    float phase = _phaseOffset.TryGetValue(id, out var p) ? p : 0f;
                    float nextAligned = now - ((now - phase) % HeartbeatInterval) + HeartbeatInterval;
                    _nextHeartbeat[id] = nextAligned;
                }
            }

            return dirty;
        }

        private static bool HasChanged(UnitState current, UnitState previous)
        {
            // Critical state transitions — always immediate
            if (current.IsDestroyed != previous.IsDestroyed) return true;
            if (current.IsSinking  != previous.IsSinking)  return true;
            if (current.Telegraph  != previous.Telegraph)  return true;

            // Position (geo coords)
            if (Mathf.Abs(current.X - previous.X) > PositionThreshold) return true;
            if (Mathf.Abs(current.Z - previous.Z) > PositionThreshold) return true;
            if (Mathf.Abs(current.Y - previous.Y) > HeightThreshold)   return true;

            // Movement
            if (Mathf.Abs(current.Heading - previous.Heading) > HeadingThreshold) return true;
            if (Mathf.Abs(current.Speed   - previous.Speed)   > SpeedThreshold)   return true;
            if (Mathf.Abs(current.RudderAngle - previous.RudderAngle) > RudderThreshold) return true;

            // Damage
            if (Mathf.Abs(current.IntegrityPercent - previous.IntegrityPercent) > IntegrityThreshold) return true;

            // Aircraft/helicopter altitude command
            if (Mathf.Abs(current.DesiredAltitude - previous.DesiredAltitude) > AltitudeThreshold) return true;

            // Visual orientation (low priority but still tracked)
            if (Mathf.Abs(current.Pitch - previous.Pitch) > PitchRollThreshold) return true;
            if (Mathf.Abs(current.Roll  - previous.Roll)  > PitchRollThreshold) return true;

            return false;
        }

        /// <summary>
        /// Clear all tracking state. Call on disconnect, scene change, or registry clear.
        /// </summary>
        public static void Clear()
        {
            _lastSent.Clear();
            _nextHeartbeat.Clear();
            _phaseOffset.Clear();
            _staggerCounter = 0;
        }
    }
}
