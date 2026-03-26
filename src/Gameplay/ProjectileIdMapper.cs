using System.Collections.Generic;
using System.Linq;
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
        private const float HardSyncGracePeriod = 0.5f;  // seconds — defer reconciliation after hard sync (was 2.0f)

        private struct PendingEntry
        {
            public int Id;
            public int SourceUnitId;   // for auth revocation on purge
            public float EnqueueTime;  // Time.unscaledTime
        }

        private static readonly Dictionary<int, int> _hostToLocal = new(); // hostId → localId
        private static readonly Dictionary<int, int> _localToHost = new(); // localId → hostId

        // Spawn-time matching queues (per source unit + ammo type, FIFO with stale-skipping)
        private static readonly Dictionary<(int, string), Queue<PendingEntry>> _pendingHostSpawns  = new();
        private static readonly Dictionary<(int, string), Queue<PendingEntry>> _pendingLocalSpawns = new();

        // Running totals for pending counts (avoids iterating all queues)
        private static int _pendingHostTotal;
        private static int _pendingLocalTotal;

        private static float _hardSyncTime = -999f;

        // Scene projectile cache (avoids repeated FindObjectsByType calls per frame)
        private static Missile[] _cachedMissiles;
        private static Torpedo[] _cachedTorpedoes;
        private static bool _cacheValid;

        // Failed match tracking (stops retrying after MaxMatchAttempts)
        private static readonly Dictionary<int, int> _failedMatchAttempts = new();
        private const int MaxMatchAttempts = 20;

        // Log throttling (suppresses duplicate warnings per host projectile ID)
        private static readonly Dictionary<int, (float lastLogTime, int suppressedCount)> _logThrottle = new();
        private const float LogThrottleInterval = 10f;

        // Cycle detection: lock assignments after repeated reassignment
        private static readonly Dictionary<int, int> _reassignmentCount = new(); // hostId → reassignment count
        private static readonly HashSet<int> _lockedHostIds = new();             // hostIds locked from further remapping
        private const int MaxReassignments = 3;

        // Host-only projectile tracking (projectiles on host with no local counterpart)
        private static readonly HashSet<int> _hostOnlyProjectiles = new();

        // Pending host IDs: tracks host IDs currently queued in _pendingHostSpawns
        private static readonly HashSet<int> _pendingHostIds = new();

        private static bool IsInGracePeriod()
        {
            return (Time.unscaledTime - _hardSyncTime) < HardSyncGracePeriod;
        }

        /// <summary>Call on session load / disconnect to reset all mappings.</summary>
        public static void Clear()
        {
            _hostToLocal.Clear();
            _localToHost.Clear();
            _pendingHostSpawns.Clear();
            _pendingLocalSpawns.Clear();
            _pendingHostTotal = 0;
            _pendingLocalTotal = 0;
            _failedMatchAttempts.Clear();
            _logThrottle.Clear();
            _reassignmentCount.Clear();
            _lockedHostIds.Clear();
            _hostOnlyProjectiles.Clear();
            _pendingHostIds.Clear();
            // Fix #52: Only start a new grace period if not already in one
            if (!IsInGracePeriod())
            {
                _hardSyncTime = Time.unscaledTime;
            }
            else
            {
                Plugin.Log.LogDebug("[IdMapper] Cleared — existing grace period still active, not resetting timer");
            }
            ClearSceneCache();
            StalePurgedCount = 0;
            SpawnSuccessCount = 0;
            SpawnFailureCount = 0;
            Plugin.Log.LogInfo($"[IdMapper] Cleared — grace period active for {HardSyncGracePeriod}s (reconciliation deferred, proximity matching active)");
        }

        // ── Stats (read by UI) ──────────────────────────────────────────────

        public static int MappedCount => _hostToLocal.Count;
        public static int StalePurgedCount { get; private set; }
        public static int SpawnSuccessCount { get; private set; }
        public static int SpawnFailureCount { get; private set; }
        public static int PendingHostCount => _pendingHostTotal;
        public static int PendingLocalCount => _pendingLocalTotal;

        /// <summary>Number of projectiles the host reports that have no local counterpart.</summary>
        public static int HostOnlyCount => _hostOnlyProjectiles.Count;

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
            if (_pendingLocalSpawns.TryGetValue(key, out var localQueue))
            {
                SkipStaleEntries(localQueue, now, revokeAuth: false, isHostQueue: false);
                if (localQueue.Count > 0)
                {
                    int localId = localQueue.Peek().Id;
                    localQueue.Dequeue();
                    _pendingLocalTotal--;
                    Register(hostProjectileId, localId);
                    SpawnSuccessCount++;
                    Plugin.Log.LogDebug($"[IdMapper] Spawn-matched (host msg arrived second): host {hostProjectileId} → local {localId} (source unit {sourceUnitId}, ammo={ammoName})");
                    return;
                }
            }

            // No local match yet — queue for when the local projectile spawns
            if (!_pendingHostSpawns.TryGetValue(key, out var hostQueue))
            {
                hostQueue = new Queue<PendingEntry>();
                _pendingHostSpawns[key] = hostQueue;
            }
            hostQueue.Enqueue(new PendingEntry
            {
                Id = hostProjectileId,
                SourceUnitId = sourceUnitId,
                EnqueueTime = now,
            });
            _pendingHostTotal++;
            _pendingHostIds.Add(hostProjectileId);
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
            if (_pendingHostSpawns.TryGetValue(key, out var hostQueue))
            {
                SkipStaleEntries(hostQueue, now, revokeAuth: true, isHostQueue: true);
                if (hostQueue.Count > 0)
                {
                    var entry = hostQueue.Peek();
                    hostQueue.Dequeue();
                    _pendingHostTotal--;
                    _pendingHostIds.Remove(entry.Id);
                    Register(entry.Id, localProjectileId);
                    SpawnSuccessCount++;
                    Plugin.Log.LogDebug($"[IdMapper] Spawn-matched (local spawned second): host {entry.Id} → local {localProjectileId} (source unit {sourceUnitId}, ammo={ammoName})");
                    return;
                }
            }

            // No host match yet — queue for when the host message arrives
            if (!_pendingLocalSpawns.TryGetValue(key, out var localQueue))
            {
                localQueue = new Queue<PendingEntry>();
                _pendingLocalSpawns[key] = localQueue;
            }
            localQueue.Enqueue(new PendingEntry
            {
                Id = localProjectileId,
                SourceUnitId = sourceUnitId,
                EnqueueTime = now,
            });
            _pendingLocalTotal++;
        }

        /// <summary>
        /// Remove stale entries from the front of a pending queue.
        /// revokeAuth should be true for host spawn queues (their auth was never consumed),
        /// false for local spawn queues (their auth was already consumed via ConsumeAuth).
        /// isHostQueue indicates which running counter to decrement.
        /// </summary>
        private static void SkipStaleEntries(Queue<PendingEntry> queue, float now, bool revokeAuth, bool isHostQueue)
        {
            while (queue.Count > 0 && (now - queue.Peek().EnqueueTime) > StaleTimeout)
            {
                var stale = queue.Dequeue();
                if (isHostQueue)
                {
                    _pendingHostTotal--;
                    _pendingHostIds.Remove(stale.Id);
                    SpawnFailureCount++;
                }
                else
                    _pendingLocalTotal--;
                StalePurgedCount++;
                if (revokeAuth && Plugin.Instance.CfgPvP.Value)
                    PvPFireAuth.Revoke(stale.SourceUnitId, 1);
                Plugin.Log.LogWarning($"[IdMapper] Purged stale pending entry: id={stale.Id} source={stale.SourceUnitId} age={now - stale.EnqueueTime:F1}s");
            }
        }

        /// <summary>Cache scene projectiles to avoid repeated FindObjectsByType calls.</summary>
        public static void CacheSceneProjectiles()
        {
            var missileList = UnitRegistry.Missiles;
            _cachedMissiles = new Missile[missileList.Count];
            for (int i = 0; i < missileList.Count; i++) _cachedMissiles[i] = missileList[i];
            var torpedoList = UnitRegistry.Torpedoes;
            _cachedTorpedoes = new Torpedo[torpedoList.Count];
            for (int i = 0; i < torpedoList.Count; i++) _cachedTorpedoes[i] = torpedoList[i];
            _cacheValid = true;
        }

        /// <summary>Clear the scene projectile cache.</summary>
        public static void ClearSceneCache()
        {
            _cachedMissiles = null;
            _cachedTorpedoes = null;
            _cacheValid = false;
        }

        /// <summary>
        /// Periodic cleanup called from StateBroadcaster at 1Hz.
        /// Purges stale entries from both pending queues.
        /// </summary>
        public static void PurgeStaleEntries()
        {
            float now = Time.unscaledTime;

            foreach (var queue in _pendingHostSpawns.Values)
                SkipStaleEntries(queue, now, revokeAuth: true, isHostQueue: true);
            foreach (var queue in _pendingLocalSpawns.Values)
                SkipStaleEntries(queue, now, revokeAuth: false, isHostQueue: false);
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
            // Already mapped, PvP mode, or exceeded match attempts?
            if (_hostToLocal.ContainsKey(hostId)) return;
            if (Plugin.Instance.CfgPvP.Value) return;
            if (_failedMatchAttempts.TryGetValue(hostId, out var attempts) && attempts >= MaxMatchAttempts) return;
            if (_lockedHostIds.Contains(hostId)) return;
            if (_pendingHostIds.Contains(hostId))
                return;
            // Fix #52: Grace period check REMOVED from here (was lines 325-326).
            // Proximity matching now runs continuously — it's incremental and self-correcting.
            // Grace period moved to OnReconciliationReceived() to prevent batch thrashing.

            // Try direct ID match first
            var direct = StateSerializer.FindById(hostId);
            if (direct != null && !direct.IsDestroyed && (direct is Missile || direct is Torpedo))
            {
                Register(hostId, hostId);
                _failedMatchAttempts.Remove(hostId);
                return;
            }

            // Find closest unmapped local projectile
            ObjectBase? bestMatch = null;
            float bestDist = float.MaxValue;
            FindClosestUnmapped<Missile>(hostPos, ref bestMatch, ref bestDist);
            FindClosestUnmapped<Torpedo>(hostPos, ref bestMatch, ref bestDist);

            if (bestMatch != null && bestDist < 300f)
            {
                Register(hostId, bestMatch.UniqueID);
                _failedMatchAttempts.Remove(hostId);
                if (Plugin.Instance.CfgVerboseDebug.Value)
                    Plugin.Log.LogDebug($"[IdMapper] Proximity-matched host projectile {hostId} → local {bestMatch.UniqueID} (dist={bestDist:F1})");
                return;
            }

            // Track failed match attempt
            _failedMatchAttempts[hostId] = (_failedMatchAttempts.TryGetValue(hostId, out var prev) ? prev : 0) + 1;

            // Throttle log warnings (suppress duplicates, show count every LogThrottleInterval)
            if (_logThrottle.TryGetValue(hostId, out var throttle) && Time.unscaledTime - throttle.lastLogTime < LogThrottleInterval)
            {
                _logThrottle[hostId] = (throttle.lastLogTime, throttle.suppressedCount + 1);
                return;
            }
            string suffix = (throttle.suppressedCount > 0) ? $" (suppressed {throttle.suppressedCount} similar)" : "";
            Plugin.Log.LogWarning($"[IdMapper] No local match for host projectile {hostId} at ({hostPos.x:F0},{hostPos.y:F0},{hostPos.z:F0}) bestDist={bestDist:F0}{suffix}");
            _logThrottle[hostId] = (Time.unscaledTime, 0);
        }

        private static void FindClosestUnmapped<T>(Vector3 hostPos, ref ObjectBase? bestMatch, ref float bestDist)
            where T : ObjectBase
        {
            IEnumerable<ObjectBase> projectiles;
            if (_cacheValid && typeof(T) == typeof(Missile))
                projectiles = _cachedMissiles;
            else if (_cacheValid && typeof(T) == typeof(Torpedo))
                projectiles = _cachedTorpedoes;
            else if (typeof(T) == typeof(Missile))
                projectiles = UnitRegistry.Missiles;
            else if (typeof(T) == typeof(Torpedo))
                projectiles = UnitRegistry.Torpedoes;
            else
                projectiles = UnitRegistry.All;

            foreach (var p in projectiles)
            {
                if (p == null || p.IsDestroyed) continue;
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
            // Don't reassign a localId that is already mapped to a different locked hostId.
            // This prevents reconciliation from corrupting locked assignments.
            if (_localToHost.TryGetValue(localId, out int existingHostId) && existingHostId != hostId)
            {
                if (_lockedHostIds.Contains(existingHostId))
                {
                    if (Plugin.Instance.CfgVerboseDebug.Value)
                        Plugin.Log.LogDebug($"[IdMapper] Skipped reassign local {localId}: already locked to host {existingHostId}");
                    return;
                }
                // Clean up stale reverse mapping
                _hostToLocal.Remove(existingHostId);
            }

            // On client: reassign the local projectile's UniqueID to match the host's.
            // After this, FindById(hostId) works directly — no mapping lookup needed.
            // Save/restore _UID to prevent SetUniqueId from polluting the game's global
            // entity counter — without this, the client's counter jumps to the host's ID
            // range, causing subsequent entity spawns to get wildly divergent IDs.
            if (NetworkManager.Instance.IsConnectedClient && hostId != localId)
            {
                var obj = StateSerializer.FindById(localId);
                if (obj != null)
                {
                    // Check cycle detection: has this host ID been reassigned too many times?
                    int count = _reassignmentCount.TryGetValue(hostId, out var c) ? c + 1 : 1;
                    _reassignmentCount[hostId] = count;

                    if (count >= MaxReassignments)
                    {
                        // Lock this assignment to prevent infinite cycling
                        _lockedHostIds.Add(hostId);
                        Plugin.Log.LogWarning($"[IdMapper] Cycle detected: host {hostId} reassigned {count} times — locking assignment to local {localId}");
                        // Still register the mapping so lookups work
                        _hostToLocal[hostId] = localId;
                        _localToHost[localId] = hostId;
                        return;
                    }

                    int prevUid = Singleton<SceneCreator>.Instance._UID;
                    obj.SetUniqueId(hostId);
                    Singleton<SceneCreator>.Instance._UID = prevUid;
                    if (Plugin.Instance.CfgVerboseDebug.Value)
                        Plugin.Log.LogDebug($"[IdMapper] Reassigned client projectile {localId} → {hostId}");
                    return;  // No mapping needed — IDs now match
                }
            }

            // Fallback: keep mapping for host or if object not found yet
            _hostToLocal[hostId] = localId;
            _localToHost[localId] = hostId;
        }

        /// <summary>
        /// Handle reconciliation message from host. Updates tracking of host-only
        /// projectiles and attempts second-chance matching for unmapped entries.
        /// </summary>
        public static void OnReconciliationReceived(Messages.ProjectileReconciliationMessage msg)
        {
            // Fix #52: Defer reconciliation during post-hard-sync grace period.
            // Reconciliation uses batch position matching which can thrash during
            // scene reload when entity positions are still settling.
            // UpdateMapping() handles incremental matching during this window.
            if (IsInGracePeriod())
            {
                Plugin.Log.LogDebug("[IdMapper] Reconciliation deferred — grace period active " +
                    $"({HardSyncGracePeriod - (Time.unscaledTime - _hardSyncTime):F1}s remaining)");
                return;
            }

            var activeHostIds = new HashSet<int>(msg.Projectiles.Count);

            foreach (var entry in msg.Projectiles)
            {
                activeHostIds.Add(entry.HostId);

                // Already mapped? Good.
                if (_hostToLocal.ContainsKey(entry.HostId)) continue;

                // Already locked from cycling? Skip.
                if (_lockedHostIds.Contains(entry.HostId)) continue;

                // Try proximity match as a second chance
                var hostPos = new UnityEngine.Vector3(entry.X, entry.Y, entry.Z);
                ObjectBase bestMatch = null;
                float bestDist = float.MaxValue;
                FindClosestUnmapped<SeaPower.Missile>(hostPos, ref bestMatch, ref bestDist);
                FindClosestUnmapped<SeaPower.Torpedo>(hostPos, ref bestMatch, ref bestDist);

                if (bestMatch != null && bestDist < 200f)
                {
                    Register(entry.HostId, bestMatch.UniqueID);
                    _reassignmentCount.Remove(entry.HostId);
                    Plugin.Log.LogInfo($"[IdMapper] Reconciliation matched: host {entry.HostId} → local {bestMatch.UniqueID} (dist={bestDist:F1})");
                }
                else
                {
                    // Track as host-only (no local counterpart)
                    _hostOnlyProjectiles.Add(entry.HostId);
                }
            }

            // Clean up host-only tracking: remove IDs no longer in host's active list
            _hostOnlyProjectiles.RemoveWhere(id => !activeHostIds.Contains(id));
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
