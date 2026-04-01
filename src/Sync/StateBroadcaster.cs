using System.Collections;
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
        private static readonly WaitForSeconds _waitBroadcastCoop = new(0.5f);  // 2 Hz
        private static readonly WaitForSeconds _waitBroadcastPvP  = new(0.1f);  // 10 Hz — tighter sync for carrier flight ops
        private static readonly MissileStateSyncMessage _pooledMissileMsg = new();

        private void Start()
        {
            // StartCoroutine(LogLoop());  // debug logging disabled
            StartCoroutine(BroadcastLoop());
            StartCoroutine(DamageCorrectionLoop());
            StartCoroutine(WaypointFlushLoop());
            StartCoroutine(OrphanCleanupLoop());
            StartCoroutine(MissileStateSyncLoop());
            StartCoroutine(PendingSpawnCleanupLoop());
            StartCoroutine(ProjectileReconciliationLoop());
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
            LogUnits("Vessel",     UnitRegistry.Vessels);
            LogUnits("Submarine",  UnitRegistry.Submarines);
            LogUnits("Aircraft",   UnitRegistry.AircraftList);
            LogUnits("Helicopter", UnitRegistry.Helicopters);
            LogUnits("LandUnit",   UnitRegistry.LandUnits);
        }

        private static void LogUnits<T>(string label, System.Collections.Generic.IReadOnlyList<T> units) where T : ObjectBase
        {
            if (units.Count == 0) return;
            Plugin.Log.LogInfo($"[{label}] count={units.Count}");
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null) continue;
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
            float nextBroadcast = 0f;
            while (true)
            {
                yield return null; // run every frame, check timing manually

                if (Time.unscaledTime < nextBroadcast) continue;

                // PvP: fixed 10 Hz. Co-op: scale with time compression (2-10 Hz).
                float interval;
                if (Plugin.Instance.CfgPvP.Value)
                {
                    interval = 0.1f; // 10 Hz fixed for PvP
                }
                else
                {
                    float tc = Mathf.Max(1f, Time.timeScale);
                    interval = Mathf.Max(0.1f, 0.5f / tc);
                    // TC=1: 0.50s (2 Hz), TC=2: 0.25s (4 Hz), TC=3: 0.17s (6 Hz), TC=5: 0.10s (10 Hz)
                }

                nextBroadcast = Time.unscaledTime + interval;

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

            // Co-op: only host broadcasts (unchanged)
            if (!isPvP && !isHost) return;

            // Don't broadcast during scene transitions or before sync is established
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;
            if (SessionManager.SceneLoading) return;

            // PvP: capture own units only; Co-op: capture all
            // Guard: if _playerTaskforce isn't set yet, skip to avoid unfiltered broadcast
            SeaPower.Taskforce filter = null;
            if (isPvP)
            {
                filter = SeaPower.Globals._playerTaskforce;
                if (filter == null) return;
            }

            // Phase 1: Full capture (all units + projectiles) for change comparison
            var msg = StateSerializer.Capture(filter);

            // Phase 2: Determine which units actually need sending this tick
            float now = UnityEngine.Time.unscaledTime;
            var dirtyIds = ChangeTracker.ComputeDirtySet(msg.Units, now);

            // Phase 3: Filter units in-place (projectiles are untouched — always full rate)
            if (dirtyIds.Count < msg.Units.Count)
            {
                for (int i = msg.Units.Count - 1; i >= 0; i--)
                {
                    if (!dirtyIds.Contains(msg.Units[i].EntityId))
                        msg.Units.RemoveAt(i);
                }
            }

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
            while (true)
            {
                yield return new WaitForSeconds(Plugin.Instance.CfgDamageSyncInterval.Value);
                if (!NetworkManager.Instance.IsConnected) continue;

                bool isPvP = Plugin.Instance.CfgPvP.Value;
                // Co-op: host only. PvP: both sides send corrections for own damaged ships.
                if (!isPvP && !Plugin.Instance.CfgIsHost.Value) continue;

                BroadcastDamageCorrections(isPvP);
            }
        }

        private static void BroadcastDamageCorrections(bool isPvP)
        {
            SendCorrections(isPvP, UnitRegistry.Vessels);
            SendCorrections(isPvP, UnitRegistry.Submarines);
        }

        private static void SendCorrections<T>(bool isPvP, System.Collections.Generic.IReadOnlyList<T> units) where T : ObjectBase
        {
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null || unit.IsDestroyed) continue;
                var comps = unit.Compartments;
                if (comps == null) continue;

                // PvP: only send corrections for own units that have taken damage
                if (isPvP && unit._taskforce != SeaPower.Globals._playerTaskforce) continue;

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

                var playerTf = SeaPower.Globals._playerTaskforce;
                if (playerTf == null) continue;

                var missiles = UnitRegistry.Missiles;
                if (missiles.Count == 0) continue;

                var msg = _pooledMissileMsg;
                msg.Reset();

                for (int i = 0; i < missiles.Count; i++)
                {
                    var m = missiles[i];
                    if (m == null || m.IsDestroyed) continue;

                    // Owner authority: send state for missiles launched by my units
                    var launcher = StateSerializer.GetLaunchPlatform(m);
                    if (launcher == null || launcher._taskforce != playerTf) continue;

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

        // ── Pending spawn cleanup (purges stale FIFO entries) ─────────────
        private IEnumerator PendingSpawnCleanupLoop()
        {
            var wait = new WaitForSeconds(1f);
            while (true)
            {
                yield return wait;
                if (!NetworkManager.Instance.IsConnected) continue;
                ProjectileIdMapper.PurgeStaleEntries();
            }
        }

        // ── Projectile reconciliation (host sends active list to client) ────
        private IEnumerator ProjectileReconciliationLoop()
        {
            var wait = new WaitForSeconds(5f);
            var msg = new Messages.ProjectileReconciliationMessage();
            while (true)
            {
                yield return wait;
                if (!Plugin.Instance.CfgIsHost.Value) continue;
                if (Plugin.Instance.CfgPvP.Value) continue;
                if (!NetworkManager.Instance.IsConnected) continue;
                if (SimSyncManager.CurrentState != SimState.Synchronized) continue;
                if (SessionManager.SceneLoading) continue;

                msg.Reset();

                var missiles = UnitRegistry.Missiles;
                for (int i = 0; i < missiles.Count; i++)
                {
                    var m = missiles[i];
                    if (m == null || m.IsDestroyed) continue;
                    var launcher = StateSerializer.GetLaunchPlatform(m);
                    var pos = m.transform.position;
                    msg.Projectiles.Add(new Messages.ProjectileReconciliationMessage.ActiveProjectile
                    {
                        HostId = m.UniqueID,
                        SourceUnitId = launcher?.UniqueID ?? 0,
                        X = pos.x, Y = pos.y, Z = pos.z,
                    });
                }

                var torpedoes = UnitRegistry.Torpedoes;
                for (int i = 0; i < torpedoes.Count; i++)
                {
                    var t = torpedoes[i];
                    if (t == null || t.IsDestroyed) continue;
                    var launcher = StateSerializer.GetLaunchPlatform(t);
                    var pos = t.transform.position;
                    msg.Projectiles.Add(new Messages.ProjectileReconciliationMessage.ActiveProjectile
                    {
                        HostId = t.UniqueID,
                        SourceUnitId = launcher?.UniqueID ?? 0,
                        X = pos.x, Y = pos.y, Z = pos.z,
                    });
                }

                if (msg.Projectiles.Count > 0)
                    NetworkManager.Instance.BroadcastToClients(msg, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
        }

        // ── PvP orphan cleanup (destroys unmatched units/missiles) ──────────
        private IEnumerator OrphanCleanupLoop()
        {
            var wait = new WaitForSeconds(10f);
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

