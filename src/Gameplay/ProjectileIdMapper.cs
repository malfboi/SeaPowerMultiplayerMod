using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Maps host projectile UniqueIDs to local projectile UniqueIDs.
    ///
    /// Primary mechanism: spawn-time matching. The host broadcasts a ProjectileSpawnMessage
    /// immediately when a projectile spawns (via CommonLaunchSettings Postfix). The client
    /// matches it to a local projectile from the same source unit in FIFO order.
    ///
    /// Because network latency means the host message and local spawn can arrive in either
    /// order, we maintain two per-source-unit lists:
    ///   _pendingHostSpawns: host IDs waiting for a local projectile to match
    ///   _pendingLocalSpawns: local IDs waiting for a host message to match
    ///
    /// Stale entries (older than StaleTimeout) are skipped during matching and purged
    /// periodically to prevent FIFO queue corruption when host/client fire different
    /// numbers of missiles.
    ///
    /// Fallback: proximity matching during state updates (for edge cases where spawn
    /// matching didn't fire, e.g. projectiles that existed before connection).
    /// </summary>
    public static class ProjectileIdMapper
    {
        private const float StaleTimeout = 15f; // real seconds before a pending entry is considered stale

        private struct PendingEntry
        {
            public int Id;
            public int SourceUnitId;   // for auth revocation on purge
            public float EnqueueTime;  // Time.unscaledTime
        }

        private static readonly Dictionary<int, int> _hostToLocal = new(); // hostId → localId
        private static readonly Dictionary<int, int> _localToHost = new(); // localId → hostId

        // Spawn-time matching lists (per source unit + ammo type, FIFO with stale-skipping)
        private static readonly Dictionary<(int, string), List<PendingEntry>> _pendingHostSpawns  = new();
        private static readonly Dictionary<(int, string), List<PendingEntry>> _pendingLocalSpawns = new();

        /// <summary>Call on session load / disconnect to reset all mappings.</summary>
        public static void Clear()
        {
            _hostToLocal.Clear();
            _localToHost.Clear();
            _pendingHostSpawns.Clear();
            _pendingLocalSpawns.Clear();
            StalePurgedCount = 0;
        }

        // ── Stats (read by UI) ──────────────────────────────────────────────

        public static int MappedCount => _hostToLocal.Count;
        public static int StalePurgedCount { get; private set; }
        public static int PendingHostCount
        {
            get { int t = 0; foreach (var q in _pendingHostSpawns.Values) t += q.Count; return t; }
        }
        public static int PendingLocalCount
        {
            get { int t = 0; foreach (var q in _pendingLocalSpawns.Values) t += q.Count; return t; }
        }

        // ── Spawn-time matching ───────────────────────────────────────────────

        /// <summary>
        /// Called on client when a ProjectileSpawnMessage arrives from the host.
        /// If a local projectile from the same source unit + ammo type is already waiting, match immediately.
        /// Otherwise queue the host ID for when the local projectile spawns.
        /// </summary>
        public static void OnHostSpawnReceived(int hostProjectileId, int sourceUnitId, string ammoName)
        {
            // Already mapped? (e.g. IDs happen to align)
            if (_hostToLocal.ContainsKey(hostProjectileId)) return;

            // PvP: the remote's spawn message implicitly authorizes the local spawn.
            // Without this, bombs/gun shells (which aren't covered by FireWeapon orders)
            // get suppressed by PvPFireAuth as "unauthorized."
            // Only authorize if the unit has no existing authorizations (avoids double-auth
            // for SAMs which already got authorized via the AutoFireWeapon order).
            if (Plugin.Instance.CfgPvP.Value && PvPFireAuth.ActiveAuthForUnit(sourceUnitId) == 0)
                PvPFireAuth.Authorize(sourceUnitId, 1);

            var key = (sourceUnitId, ammoName);
            float now = Time.unscaledTime;

            // Check if a local projectile from this source + ammo is waiting for a match
            // Skip stale entries first
            if (_pendingLocalSpawns.TryGetValue(key, out var localList))
            {
                SkipStaleEntries(localList, now, revokeAuth: false);
                if (localList.Count > 0)
                {
                    int localId = localList[0].Id;
                    localList.RemoveAt(0);
                    Register(hostProjectileId, localId);
                    Plugin.Log.LogDebug($"[IdMapper] Spawn-matched (host msg arrived second): host {hostProjectileId} → local {localId} (source unit {sourceUnitId}, ammo={ammoName})");
                    return;
                }
            }

            // No local match yet — queue for when the local projectile spawns
            if (!_pendingHostSpawns.TryGetValue(key, out var hostList))
            {
                hostList = new List<PendingEntry>();
                _pendingHostSpawns[key] = hostList;
            }
            hostList.Add(new PendingEntry
            {
                Id = hostProjectileId,
                SourceUnitId = sourceUnitId,
                EnqueueTime = now,
            });
        }

        /// <summary>
        /// Called on client when a local projectile spawns (CommonLaunchSettings Postfix).
        /// If a host spawn message for this source unit + ammo type is already queued, match immediately.
        /// Otherwise queue the local ID for when the host message arrives.
        /// </summary>
        public static void OnLocalSpawn(int localProjectileId, int sourceUnitId, string ammoName)
        {
            // Already mapped?
            if (_localToHost.ContainsKey(localProjectileId)) return;

            var key = (sourceUnitId, ammoName);
            float now = Time.unscaledTime;

            // Check if a host spawn from this source + ammo is waiting for a match
            // Skip stale entries first
            if (_pendingHostSpawns.TryGetValue(key, out var hostList))
            {
                SkipStaleEntries(hostList, now, revokeAuth: true);
                if (hostList.Count > 0)
                {
                    var entry = hostList[0];
                    hostList.RemoveAt(0);
                    Register(entry.Id, localProjectileId);
                    Plugin.Log.LogDebug($"[IdMapper] Spawn-matched (local spawned second): host {entry.Id} → local {localProjectileId} (source unit {sourceUnitId}, ammo={ammoName})");
                    return;
                }
            }

            // No host match yet — queue for when the host message arrives
            if (!_pendingLocalSpawns.TryGetValue(key, out var localList))
            {
                localList = new List<PendingEntry>();
                _pendingLocalSpawns[key] = localList;
            }
            localList.Add(new PendingEntry
            {
                Id = localProjectileId,
                SourceUnitId = sourceUnitId,
                EnqueueTime = now,
            });
        }

        /// <summary>
        /// Remove stale entries from the front of a pending list.
        /// revokeAuth should be true for host spawn lists (their auth was never consumed),
        /// false for local spawn lists (their auth was already consumed via ConsumeAuth).
        /// </summary>
        private static void SkipStaleEntries(List<PendingEntry> list, float now, bool revokeAuth)
        {
            // Find the first non-stale entry
            int staleCount = 0;
            while (staleCount < list.Count && (now - list[staleCount].EnqueueTime) > StaleTimeout)
                staleCount++;

            if (staleCount == 0) return;

            // Log and revoke auth for each stale entry
            for (int i = 0; i < staleCount; i++)
            {
                var stale = list[i];
                StalePurgedCount++;
                if (revokeAuth && Plugin.Instance.CfgPvP.Value)
                    PvPFireAuth.Revoke(stale.SourceUnitId, 1);
                Plugin.Log.LogWarning($"[IdMapper] Purged stale pending entry: id={stale.Id} source={stale.SourceUnitId} age={now - stale.EnqueueTime:F1}s");
            }

            // Batch remove for O(n) instead of O(n^2)
            list.RemoveRange(0, staleCount);
        }

        /// <summary>
        /// Periodic cleanup called from StateBroadcaster at 1Hz.
        /// Purges stale entries from both pending queues.
        /// </summary>
        public static void PurgeStaleEntries()
        {
            float now = Time.unscaledTime;

            foreach (var list in _pendingHostSpawns.Values)
                SkipStaleEntries(list, now, revokeAuth: true);
            foreach (var list in _pendingLocalSpawns.Values)
                SkipStaleEntries(list, now, revokeAuth: false);
        }

        // ── Lookup ────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolve a host projectile ID to a local ObjectBase.
        /// Checks cached mapping first, then falls back to direct FindById.
        /// </summary>
        public static ObjectBase FindByHostId(int hostId)
        {
            if (_hostToLocal.TryGetValue(hostId, out int localId))
            {
                var obj = StateSerializer.FindById(localId);
                if (obj != null) return obj;

                // Mapping is stale (object destroyed) — remove it
                _hostToLocal.Remove(hostId);
                _localToHost.Remove(localId);
            }

            // IDs might match (mission-file units, or if counters happen to align)
            return StateSerializer.FindById(hostId);
        }

        /// <summary>
        /// Translate a local projectile ID to the ID known by the remote side.
        /// Used when sending orders (e.g. AutoFireWeapon) that reference projectiles
        /// spawned by the remote player. Returns the original ID if no mapping exists.
        /// </summary>
        public static int TranslateForRemote(int localId)
        {
            return _localToHost.TryGetValue(localId, out int remoteId) ? remoteId : localId;
        }

        // ── Fallback: proximity matching during state updates ─────────────────

        /// <summary>
        /// Called during state update processing for each host projectile.
        /// Only used as fallback if spawn-time matching didn't already map this ID.
        /// </summary>
        public static void UpdateMapping(int hostId, Vector3 hostPos)
        {
            // Already mapped (likely via spawn-time matching)?
            if (_hostToLocal.ContainsKey(hostId)) return;

            // PvP: skip proximity matching (spawn-time matching is sufficient)
            if (Plugin.Instance.CfgPvP.Value) return;

            // Try direct ID match first
            var direct = StateSerializer.FindById(hostId);
            if (direct != null && !direct.IsDestroyed && (direct is Missile || direct is Torpedo))
            {
                Register(hostId, hostId);
                return;
            }

            // Find closest unmapped local projectile
            ObjectBase? bestMatch = null;
            float bestDist = float.MaxValue;
            FindClosestUnmapped<Missile>(hostPos, ref bestMatch, ref bestDist);
            FindClosestUnmapped<Torpedo>(hostPos, ref bestMatch, ref bestDist);

            if (bestMatch != null && bestDist < 2000f)
            {
                Register(hostId, bestMatch.UniqueID);
                Plugin.Log.LogDebug($"[IdMapper] Proximity-matched host projectile {hostId} → local {bestMatch.UniqueID} (dist={bestDist:F1})");
            }
            else
            {
                Plugin.Log.LogWarning($"[IdMapper] No local match for host projectile {hostId} at ({hostPos.x:F0},{hostPos.y:F0},{hostPos.z:F0}) bestDist={bestDist:F0}");
            }
        }

        private static void FindClosestUnmapped<T>(Vector3 hostPos, ref ObjectBase? bestMatch, ref float bestDist)
            where T : ObjectBase
        {
            foreach (var p in Object.FindObjectsByType<T>(FindObjectsSortMode.None))
            {
                if (p.IsDestroyed) continue;
                if (_localToHost.ContainsKey(p.UniqueID)) continue;
                float dist = Vector3.Distance(p.transform.position, hostPos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestMatch = p;
                }
            }
        }

        private static void Register(int hostId, int localId)
        {
            // On client: reassign the local projectile's UniqueID to match the host's.
            // After this, FindById(hostId) works directly — no mapping lookup needed.
            if (NetworkManager.Instance.IsConnectedClient && hostId != localId)
            {
                var obj = StateSerializer.FindById(localId);
                if (obj != null)
                {
                    obj.SetUniqueId(hostId);
                    Plugin.Log.LogDebug($"[IdMapper] Reassigned client projectile {localId} → {hostId}");
                    return;  // No mapping needed — IDs now match
                }
            }

            // Fallback: keep mapping for host or if object not found yet
            _hostToLocal[hostId] = localId;
            _localToHost[localId] = hostId;
        }

        /// <summary>Remove mapping when a projectile is destroyed.</summary>
        public static void OnProjectileDestroyed(int localId)
        {
            if (_localToHost.TryGetValue(localId, out int hostId))
            {
                _hostToLocal.Remove(hostId);
                _localToHost.Remove(localId);
            }
        }
    }
}
