using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    public static class MissileStateSyncHandler
    {
        // ── Stats (read by UI) ──────────────────────────────────────────────

        public static int LastAppliedCount { get; private set; }
        public static int JammedCount { get; private set; }
        public static int TargetLostCount { get; private set; }

        public static void ResetCounters()
        {
            LastAppliedCount = 0;
            JammedCount = 0;
            TargetLostCount = 0;
        }

        public static void Apply(MissileStateSyncMessage msg)
        {
            if (!Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;
            if (SessionManager.SceneLoading) return;

            LastAppliedCount = 0;
            JammedCount = 0;
            TargetLostCount = 0;

            foreach (var entry in msg.Entries)
            {
                var obj = StateSerializer.FindById(entry.MissileId);
                if (obj == null || obj.IsDestroyed) continue;

                var weapon = obj as WeaponBase;
                if (weapon == null) continue;

                LastAppliedCount++;
                if (entry.Jammed) JammedCount++;
                if (entry.TargetLost) TargetLostCount++;

                // Apply guidance/jamming flags (keep for visual fidelity on puppets)
                weapon._jammed = entry.Jammed;
                weapon._deviationMagnitudeWithJam = entry.DeviationMagnitudeWithJam;
                weapon._fuseActive = entry.FuseActive;
                weapon._ignoreCollisions = entry.IgnoreCollisions;
                weapon._collisionNoticed = entry.CollisionNoticed;
                weapon._targetLost = entry.TargetLost;

                if (weapon.ConnectionLost.Value != entry.ConnectionLost)
                    weapon.ConnectionLost.Value = entry.ConnectionLost;
                if (weapon.ConnectionLostForever.Value != entry.ConnectionLostForever)
                    weapon.ConnectionLostForever.Value = entry.ConnectionLostForever;

                if (weapon is Missile missile)
                {
                    missile._currentPitch = entry.CurrentPitch;
                    missile._poppingUp = entry.PoppingUp;
                    missile._hasPoppedUp = entry.HasPoppedUp;
                }

                // Position: no longer corrected — local physics runs the missile
                // after force-spawn. Position overrides cause visible jitter.
                // Guidance state (jammed, targetLost, etc.) synced above is sufficient.
            }
        }
    }
}
