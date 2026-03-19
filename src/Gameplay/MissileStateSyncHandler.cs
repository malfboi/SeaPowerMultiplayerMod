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

                // Position: lerp toward authoritative owner state
                var geo = new GeoPosition
                {
                    _longitude = entry.X,
                    _latitude  = entry.Z,
                    _height    = entry.Y,
                };
                Vector2 local = Utils.longLatToLocal(geo, Globals._currentCenterTile);
                Vector3 targetPos = new Vector3(local.x, entry.Y, local.y);

                float drift = Vector3.Distance(obj.transform.position, targetPos);

                if (drift > 100f) // snap threshold
                {
                    obj.transform.position = targetPos;
                    obj.transform.eulerAngles = new Vector3(entry.CurrentPitch, entry.Heading, 0f);
                    weapon._velocityInKnots = entry.Speed;
                }
                else if (drift > 2f) // ignore trivial
                {
                    obj.transform.position = Vector3.Lerp(obj.transform.position, targetPos, 0.7f);
                    float heading = Mathf.LerpAngle(obj.transform.eulerAngles.y, entry.Heading, 0.8f);
                    float pitch = Mathf.LerpAngle(obj.transform.eulerAngles.x, entry.CurrentPitch, 0.8f);
                    obj.transform.eulerAngles = new Vector3(pitch, heading, 0f);
                    weapon._velocityInKnots = Mathf.Lerp(weapon._velocityInKnots, entry.Speed, 0.6f);
                }
            }
        }
    }
}
