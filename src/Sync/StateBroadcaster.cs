using System.Collections;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// MonoBehaviour attached to the Plugin GameObject.
    /// - Logs all vessels at 1 Hz (Phase 1 verification)
    /// - Broadcasts STATE_UPDATE at 10 Hz when connected (Phase 3+)
    /// </summary>
    public class StateBroadcaster : MonoBehaviour
    {
        private const float LogInterval = 1.0f;
        private static readonly FieldInfo _launchPlatformField =
            AccessTools.Field(typeof(WeaponBase), "_launchPlatform");
        private static readonly WaitForSeconds _waitBroadcastCoop = new(0.2f);  // 5 Hz — increased from 2 Hz to reduce AI visual lag in co-op
        private static readonly WaitForSeconds _waitBroadcastPvP  = new(0.1f);  // 10 Hz — tighter sync for carrier flight ops

        private void Start()
        {
            StartCoroutine(LogLoop());
            StartCoroutine(BroadcastLoop());
            StartCoroutine(DamageCorrectionLoop());
            StartCoroutine(WaypointFlushLoop());
            StartCoroutine(OrphanCleanupLoop());
            StartCoroutine(MissileStateSyncLoop());
            StartCoroutine(PendingSpawnCleanupLoop());
        }

        private void Update()
        {
            // PvP: local physics provides 60fps movement; no interpolation needed
        }

        // ── Phase 1: log all vessels every second ─────────────────────────────
        private IEnumerator LogLoop()
        {
            var wait = new WaitForSeconds(LogInterval);
            while (true)
            {
                yield return wait;
                LogAllVessels();
            }
        }

        private static void LogAllVessels()
        {
            LogUnits<Vessel>    ("Vessel");
            LogUnits<Submarine> ("Submarine");
            LogUnits<Aircraft>  ("Aircraft");
            LogUnits<Helicopter>("Helicopter");
            LogUnits<LandUnit>  ("LandUnit");
        }

        private static void LogUnits<T>(string label) where T : ObjectBase
        {
            var units = Object.FindObjectsByType<T>(FindObjectsSortMode.None);
            if (units.Length == 0) return;
            Plugin.Log.LogInfo($"[{label}] count={units.Length}");
            foreach (var u in units)
            {
                var pos = u.transform.position;
                Plugin.Log.LogInfo(
                    $"  uid={u.UniqueID}" +
                    $"  pos=({pos.x:F1},{pos.y:F1},{pos.z:F1})" +
                    $"  hdg={u.transform.eulerAngles.y:F1}" +
                    $"  spd={u._velocityInKnots:F1}kts");
            }
        }

        // ── Phase 3: broadcast state to client ────────────────────────────────
        private IEnumerator BroadcastLoop()
        {
            while (true)
            {
                yield return Plugin.Instance.CfgPvP.Value
                    ? _waitBroadcastPvP : _waitBroadcastCoop;
                if (NetworkManager.Instance.IsConnected)
                {
                    try { BroadcastState(); }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogError($"[Broadcast] Exception in BroadcastState: {ex}");
                    }
                }
            }
        }

        private static void BroadcastState()
        {
            bool isHost = Plugin.Instance.CfgIsHost.Value;
            bool isPvP  = Plugin.Instance.CfgPvP.Value;

            // Don't broadcast during scene transitions or before sync is established
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;
            if (SessionManager.SceneLoading) return;

            // Determine authority filter mode
            var localPlayer = PlayerRegistry.GetLocalPlayer();
            bool hasSpecificAssignment = localPlayer != null && localPlayer.AssignedTfNames.Count > 0;

            if (!hasSpecificAssignment && !isHost && !isPvP)
            {
                // Co-op client with no assignment: don't broadcast
                return;
            }

            // Use per-unit authority filter when player has specific group assignments,
            // or in PvP mode (each side only broadcasts its own units).
            bool useAuthorityFilter = hasSpecificAssignment || isPvP;

            var msg = StateSerializer.Capture(useAuthorityFilter);

            if (isHost)
                NetworkManager.Instance.BroadcastToClients(msg, LiteNetLib.DeliveryMethod.Unreliable);
            else
                NetworkManager.Instance.SendToServer(msg, LiteNetLib.DeliveryMethod.Unreliable);
        }

        // ── Waypoint drag flush (catches throttled final positions) ────────
        private IEnumerator WaypointFlushLoop()
        {
            var wait = new WaitForSeconds(0.15f);
            while (true)
            {
                yield return wait;
                if (!NetworkManager.Instance.IsConnected) continue;

                foreach (var kvp in Patch_UserRootNode_UpdateSimulation._pending)
                {
                    var (unit, index) = kvp.Value;
                    var root = unit._userRoot;
                    if (root == null || index >= root.TaskViewModels.Count) continue;
                    if (root.TaskViewModels[index].Task is GoToWaypointTask wp)
                        Patch_UserRootNode_UpdateSimulation.SendEditWaypoint(unit, index, wp);
                }
                Patch_UserRootNode_UpdateSimulation._pending.Clear();
            }
        }

        // ── Periodic damage correction (catches drift / packet loss) ────────
        private IEnumerator DamageCorrectionLoop()
        {
            var wait = new WaitForSeconds(5f);
            while (true)
            {
                yield return wait;
                if (!NetworkManager.Instance.IsConnected) continue;

                bool isPvP = Plugin.Instance.CfgPvP.Value;
                // Send corrections for locally authoritative damaged ships
                var localPlayer = PlayerRegistry.GetLocalPlayer();
                if (!isPvP && !Plugin.Instance.CfgIsHost.Value && (localPlayer == null || localPlayer.AssignedTfNames.Count == 0)) continue;

                BroadcastDamageCorrections();
            }
        }

        private static void BroadcastDamageCorrections()
        {
            SendCorrections<Vessel>();
            SendCorrections<Submarine>();
        }

        private static void SendCorrections<T>() where T : ObjectBase
        {
            foreach (var unit in Object.FindObjectsByType<T>(FindObjectsSortMode.None))
            {
                if (unit.IsDestroyed) continue;
                var comps = unit.Compartments;
                if (comps == null) continue;

                // Only send corrections for units we are authoritative for
                if (!PlayerRegistry.IsLocallyAuthoritative(unit)) continue;

                // Only send for units that have taken damage or are sinking
                if (!comps._isSinking && comps.IntegrityPercentage > 99f) continue;

                var msg = DamageStateSerializer.Capture(unit);
                if (msg != null)
                    NetworkManager.Instance.SendToOther(msg, DeliveryMethod.Unreliable);
            }
        }

        // ── PvP missile state sync (guidance/position for incoming missiles) ─
        private IEnumerator MissileStateSyncLoop()
        {
            var wait = new WaitForSeconds(0.1f); // 10 Hz
            while (true)
            {
                yield return wait;
                if (!Plugin.Instance.CfgPvP.Value) continue;
                if (!NetworkManager.Instance.IsConnected) continue;
                if (SimSyncManager.CurrentState != SimState.Synchronized) continue;
                if (SessionManager.SceneLoading) continue;

                var missiles = Object.FindObjectsByType<Missile>(FindObjectsSortMode.None);
                if (missiles.Length == 0) continue;

                var msg = new MissileStateSyncMessage();

                foreach (var m in missiles)
                {
                    if (m.IsDestroyed) continue;

                    // Owner authority: send state for missiles launched by units we control
                    var launcher = _launchPlatformField?.GetValue(m) as ObjectBase;
                    if (launcher == null || !PlayerRegistry.IsLocallyAuthoritative(launcher)) continue;

                    // Encode position as absolute GeoPosition (floating-origin safe)
                    var geo = Utils.worldPositionFromUnityToLongLat(
                        m.transform.position, SeaPower.Globals._currentCenterTile);

                    msg.Entries.Add(new MissileStateEntry
                    {
                        MissileId                = m.UniqueID,
                        Jammed                   = m._jammed,
                        DeviationMagnitudeWithJam = m._deviationMagnitudeWithJam,
                        FuseActive               = m._fuseActive,
                        IgnoreCollisions         = m._ignoreCollisions,
                        ConnectionLost           = m.ConnectionLost.Value,
                        ConnectionLostForever    = m.ConnectionLostForever.Value,
                        TargetLost               = m._targetLost,
                        CollisionNoticed         = m._collisionNoticed,
                        PoppingUp               = m._poppingUp,
                        HasPoppedUp             = m._hasPoppedUp,
                        CurrentPitch            = m._currentPitch,
                        X       = (float)geo._longitude,
                        Y       = (float)geo._height,
                        Z       = (float)geo._latitude,
                        Heading = m.transform.eulerAngles.y,
                        Speed   = m._velocityInKnots,
                    });
                }

                if (msg.Entries.Count > 0)
                    NetworkManager.Instance.SendToOther(msg, DeliveryMethod.Unreliable);
            }
        }

        // ── PvP pending spawn cleanup (purges stale FIFO entries) ─────────
        private IEnumerator PendingSpawnCleanupLoop()
        {
            var wait = new WaitForSeconds(1f);
            while (true)
            {
                yield return wait;
                if (!Plugin.Instance.CfgPvP.Value) continue;
                if (!NetworkManager.Instance.IsConnected) continue;
                ProjectileIdMapper.PurgeStaleEntries();
            }
        }

        // ── PvP orphan cleanup (destroys unmatched units/missiles) ──────────
        private IEnumerator OrphanCleanupLoop()
        {
            var wait = new WaitForSeconds(5f);
            while (true)
            {
                yield return wait;
                if (!Plugin.Instance.CfgPvP.Value) continue;
                if (!NetworkManager.Instance.IsConnected) continue;
                StateApplier.CleanupOrphans();
            }
        }
    }
}

