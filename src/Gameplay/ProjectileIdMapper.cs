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

        // ── TryForceSpawn retry queue ────────────────────────────────────────
        // When TryForceSpawn fails (container reloading, unit temporarily unavailable),
        // retry a few times before giving up instead of losing the mapping forever.
        private const int MaxForceSpawnRetries = 3;
        private const float ForceSpawnRetryDelay = 0.5f;

        private struct RetryEntry
        {
            public float RetryAt;
            public int HostId;
            public int SourceUnitId;
            public string AmmoName;
            public ObjectBase TargetObj;
            public Vector3? TargetPos;
            public Vector3? LaunchDir;
            public int RetryCount;
        }

        private static readonly Queue<RetryEntry> _forceSpawnRetries = new();

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
            _forceSpawnRetries.Clear();
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
        /// Called when a ProjectileSpawnMessage arrives (on either side in PvP, or on the client in co-op).
        /// Attempts to match against an already-spawned local missile, or queues for later matching.
        /// Then calls TryForceSpawn so a local missile is spawned immediately for ID-mapping purposes.
        /// In PvP the spawn message includes target info so TryForceSpawn fires at the correct target.
        /// </summary>
        public static void OnHostSpawnReceived(int hostProjectileId, int sourceUnitId, string ammoName,
            int targetEntityId = 0, float targetX = 0f, float targetY = 0f, float targetZ = 0f,
            float launchDirX = 0f, float launchDirY = 0f, float launchDirZ = 0f)
        {
            // Already mapped? (e.g. IDs happen to align)
            if (_hostToLocal.ContainsKey(hostProjectileId)) return;

            // PvP: each spawn message represents one authorized shot from the remote side.
            // Always grant 1 auth token per message — without this, rapid fire (multiple
            // spawn messages before the first is consumed) causes auth exhaustion, which
            // suppresses the local TryForceSpawn and permanently desynchronizes the FIFO queue.
            if (Plugin.Instance.CfgPvP.Value)
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

            // No local match yet — queue the host ID, then try to force-spawn immediately
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

            // Resolve target from the message for guided missiles.
            // In PvP, coordinates in the spawn message are geo-encoded (same as AutoFireWeapon orders).
            ObjectBase targetObject = null;
            Vector3 targetPos = Vector3.zero;
            bool hasTarget = false;
            if (targetEntityId > 0 || targetX != 0f || targetY != 0f || targetZ != 0f)
            {
                hasTarget = true;
                if (targetEntityId > 0)
                    targetObject = StateSerializer.FindById(targetEntityId) ?? FindByHostId(targetEntityId);
                if (Plugin.Instance.CfgPvP.Value)
                {
                    var geo = new GeoPosition { _longitude = targetX, _latitude = targetZ, _height = targetY };
                    UnityEngine.Vector2 local = Utils.longLatToLocal(geo, Globals._currentCenterTile);
                    targetPos = new Vector3(local.x, targetY, local.y);
                }
                else
                {
                    targetPos = new Vector3(targetX, targetY, targetZ);
                }
            }

            if (hasTarget)
                Plugin.Log.LogInfo($"[IdMapper] Target resolution: entityId={targetEntityId} " +
                    $"resolved={targetObject != null} " +
                    (targetObject != null ? $"name={targetObject.name} " : "") +
                    $"pos=({targetPos.x:F0},{targetPos.y:F0},{targetPos.z:F0})");

            // Force-spawn a local missile directly from the unit's container so that
            // OnLocalSpawn (triggered by CommonLaunchSettings Postfix) matches instantly.
            // In PvP, this fires at the correct time (spawn message sent at fire time) with
            // the correct target, replacing the too-early engage-task puppet fire.
            Vector3? launchDir = (launchDirX != 0f || launchDirY != 0f || launchDirZ != 0f)
                ? new Vector3(launchDirX, launchDirY, launchDirZ)
                : (Vector3?)null;
            var targetObjArg = hasTarget ? targetObject : null;
            var targetPosArg = hasTarget ? targetPos : (Vector3?)null;
            if (!TryForceSpawn(sourceUnitId, ammoName, targetObjArg, targetPosArg, launchDir))
            {
                // Queue for retry — container may be reloading, unit may become available
                _forceSpawnRetries.Enqueue(new RetryEntry
                {
                    RetryAt     = Time.unscaledTime + ForceSpawnRetryDelay,
                    HostId      = hostProjectileId,
                    SourceUnitId = sourceUnitId,
                    AmmoName    = ammoName,
                    TargetObj   = targetObjArg,
                    TargetPos   = targetPosArg,
                    LaunchDir   = launchDir,
                    RetryCount  = 1,
                });
            }
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
        /// Directly launch a missile from an available container on the source unit,
        /// bypassing all engage-task checks (target detection, ammo channel limits, etc.).
        /// Called after queueing a host spawn entry so OnLocalSpawn can immediately match it.
        /// If targetObject/targetPosition are provided the missile is guided; otherwise it
        /// flies toward the unit's own position (acceptable for non-guided weapons or dummies).
        /// </summary>
        private static bool TryForceSpawn(int sourceUnitId, string ammoName,
            ObjectBase targetObject = null, Vector3? targetPosition = null, Vector3? launchDir = null)
        {
            var unit = StateSerializer.FindById(sourceUnitId);
            if (unit == null || unit._obp == null)
            {
                Plugin.Log.LogWarning($"[ForceSpawn] Unit {sourceUnitId} not found for ammo={ammoName}");
                return false;
            }

            // Use provided target position, or fall back to launcher position (for non-guided).
            var launchTarget = targetObject;
            var launchPos    = targetPosition ?? unit.transform.position;

            // First pass: non-empty containers (fast path — no reload needed)
            foreach (var ws in unit._obp._weaponSystems)
            {
                var launcher = ws as WeaponSystemLauncher;
                if (launcher == null) continue;

                for (int i = 0; i < launcher._containers.Count; i++)
                {
                    var container = launcher._containers[i];
                    if (container.IsEmpty() || container._weapons.Count == 0) continue;

                    var weapon = container._weapons[0];
                    if (weapon?._ap?._ammunitionFileName != ammoName) continue;

                    var weaponRef = weapon; // capture before launch() removes it from _weapons
                    launcher.launch(i, launchTarget, launchPos, Vector3.zero);
                    ApplyLaunchDirection(weaponRef, launchDir, ammoName, sourceUnitId);
                    Plugin.Log.LogInfo($"[ForceSpawn] Force-spawned ammo={ammoName} unit={sourceUnitId} container={i}");
                    return true;
                }
            }

            // Second pass: empty containers whose last-loaded ammo matches.
            // This handles the reload-cycle gap: the container was fired (now empty) and hasn't
            // finished reloading yet, but the host already fired another missile of the same type.
            // We force-load one round directly so the match can proceed.
            foreach (var ws in unit._obp._weaponSystems)
            {
                var launcher = ws as WeaponSystemLauncher;
                if (launcher == null) continue;

                // Magazine is shared across all containers in the launcher system
                var magazine = launcher._vwp?._associatedMagazine;

                for (int i = 0; i < launcher._containers.Count; i++)
                {
                    var container = launcher._containers[i];
                    if (!container.IsEmpty()) continue;

                    // _loadedAmmunition is set from the last load (magazine or non-magazine).
                    // _initialAmmunition is set for non-magazine containers only.
                    // If both are null (first-use, never loaded), fall through to the magazine
                    // dictionary lookup below rather than skipping this container.
                    var ammoRef = container._loadedAmmunition ?? container._initialAmmunition;
                    if (ammoRef != null && ammoRef._ap._ammunitionFileName != ammoName) continue;

                    bool loaded = false;
                    if (magazine != null)
                    {
                        // Magazine-based: temporarily top up by 1 so container.load() succeeds,
                        // then load decrements it back — net effect: weapon created, magazine unchanged.
                        // If ammoRef is still null (container never loaded), look it up from the magazine
                        // dictionary directly so we handle the first-use case at the start of an engagement.
                        var loadAmmo = ammoRef ?? magazine.getAmmunitionByName(ammoName);
                        if (loadAmmo != null)
                        {
                            magazine.increaseAmmunitionCount(loadAmmo._ap._ammunitionFileName, 1);
                            container.load(loadAmmo);
                            loaded = !container.IsEmpty();
                        }
                    }
                    else if (container._initialAmmunition != null)
                    {
                        loaded = container.LoadAmmo(1) > 0;
                    }

                    if (!loaded) continue;

                    var weaponRef = container._weapons[0]; // capture before launch() removes it
                    launcher.launch(i, launchTarget, launchPos, Vector3.zero);
                    ApplyLaunchDirection(weaponRef, launchDir, ammoName, sourceUnitId);
                    Plugin.Log.LogInfo($"[ForceSpawn] Force-spawned (reload) ammo={ammoName} unit={sourceUnitId} container={i}");
                    return true;
                }
            }

            Plugin.Log.LogWarning($"[ForceSpawn] No loaded container for ammo={ammoName} unit={sourceUnitId}");
            return false;
        }

        /// <summary>
        /// After launcher.launch(), the missile exists but faces the container's rest direction.
        /// Rotate it to match the host's launch direction and rebuild waypoints so the flight
        /// plan uses the correct StartAngle (derived from transform.forward.y).
        /// weaponRef must be captured from container._weapons[0] BEFORE launch() removes it.
        /// </summary>
        private static void ApplyLaunchDirection(WeaponBase weaponRef, Vector3? launchDir,
            string ammoName, int sourceUnitId)
        {
            if (!launchDir.HasValue || launchDir.Value == Vector3.zero) return;
            if (weaponRef == null) return;

            weaponRef.transform.rotation = Quaternion.LookRotation(launchDir.Value);

            if (weaponRef is Missile missile)
            {
                missile.CreateWaypoints();
                Plugin.Log.LogInfo($"[ForceSpawn] Applied launch direction ({launchDir.Value.x:F2},{launchDir.Value.y:F2},{launchDir.Value.z:F2}) and rebuilt waypoints for {ammoName} unit={sourceUnitId}");
            }
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
        /// Purges stale entries from both pending queues, and drains entries
        /// whose source unit has been destroyed (no spawn can ever match).
        /// </summary>
        public static void PurgeStaleEntries()
        {
            float now = Time.unscaledTime;

            foreach (var queue in _pendingHostSpawns.Values)
                SkipStaleEntries(queue, now, revokeAuth: true, isHostQueue: true);
            foreach (var queue in _pendingLocalSpawns.Values)
                SkipStaleEntries(queue, now, revokeAuth: false, isHostQueue: false);

            // PvP: drain host spawn entries whose source unit is destroyed — no local
            // projectile can ever spawn from a dead unit, so waiting is pointless.
            if (Plugin.Instance.CfgPvP.Value)
                DrainDestroyedSourceEntries();

            ProcessRetryQueue(now);
        }

        private static void DrainDestroyedSourceEntries()
        {
            foreach (var kvp in _pendingHostSpawns)
            {
                var queue = kvp.Value;
                while (queue.Count > 0)
                {
                    var entry = queue.Peek();
                    var src = StateSerializer.FindById(entry.SourceUnitId);
                    if (src != null && !src.IsDestroyed) break;

                    queue.Dequeue();
                    _pendingHostTotal--;
                    _pendingHostIds.Remove(entry.Id);
                    SpawnFailureCount++;
                    PvPFireAuth.Revoke(entry.SourceUnitId, 1);
                    Plugin.Log.LogWarning($"[IdMapper] Drained orphan entry: id={entry.Id} source={entry.SourceUnitId} (unit destroyed)");
                }
            }
        }

        private static void ProcessRetryQueue(float now)
        {
            int count = _forceSpawnRetries.Count;
            for (int i = 0; i < count; i++)
            {
                var entry = _forceSpawnRetries.Dequeue();

                // Already matched by another path (e.g. local spawn arrived in the meantime)?
                if (_hostToLocal.ContainsKey(entry.HostId)) continue;

                if (now < entry.RetryAt)
                {
                    _forceSpawnRetries.Enqueue(entry);
                    continue;
                }

                if (TryForceSpawn(entry.SourceUnitId, entry.AmmoName, entry.TargetObj, entry.TargetPos, entry.LaunchDir))
                {
                    Plugin.Log.LogInfo($"[IdMapper] ForceSpawn retry {entry.RetryCount} succeeded: host={entry.HostId} ammo={entry.AmmoName}");
                    continue;
                }

                if (entry.RetryCount < MaxForceSpawnRetries)
                {
                    entry.RetryCount++;
                    entry.RetryAt = now + ForceSpawnRetryDelay;
                    _forceSpawnRetries.Enqueue(entry);
                }
                else
                {
                    SpawnFailureCount++;
                    Plugin.Log.LogWarning($"[IdMapper] ForceSpawn failed after {MaxForceSpawnRetries} retries: host={entry.HostId} source={entry.SourceUnitId} ammo={entry.AmmoName}");
                }
            }
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

            bool isPvP = Plugin.Instance.CfgPvP.Value;
            var activeHostIds = new HashSet<int>(msg.Projectiles.Count);

            foreach (var entry in msg.Projectiles)
            {
                activeHostIds.Add(entry.HostId);

                // Already mapped? Good.
                if (_hostToLocal.ContainsKey(entry.HostId)) continue;

                // Already locked from cycling? Skip.
                if (_lockedHostIds.Contains(entry.HostId)) continue;

                ObjectBase matched = null;

                if (isPvP)
                {
                    // PvP: match by (SourceUnitId, AmmoName) — positions use different
                    // floating origins so proximity matching is unreliable.
                    matched = FindUnmappedBySourceAndAmmo(entry.SourceUnitId, entry.AmmoName);
                }
                else
                {
                    // Co-op: proximity match as a second chance
                    var hostPos = new UnityEngine.Vector3(entry.X, entry.Y, entry.Z);
                    float bestDist = float.MaxValue;
                    FindClosestUnmapped<SeaPower.Missile>(hostPos, ref matched, ref bestDist);
                    FindClosestUnmapped<SeaPower.Torpedo>(hostPos, ref matched, ref bestDist);
                    if (bestDist >= 200f) matched = null;
                }

                if (matched != null)
                {
                    Register(entry.HostId, matched.UniqueID);
                    _reassignmentCount.Remove(entry.HostId);
                    Plugin.Log.LogInfo($"[IdMapper] Reconciliation matched: remote {entry.HostId} → local {matched.UniqueID} (source={entry.SourceUnitId}, ammo={entry.AmmoName})");
                }
                else
                {
                    // Track as remote-only (no local counterpart)
                    _hostOnlyProjectiles.Add(entry.HostId);
                }
            }

            // Clean up host-only tracking: remove IDs no longer in remote's active list
            _hostOnlyProjectiles.RemoveWhere(id => !activeHostIds.Contains(id));
        }

        /// <summary>
        /// PvP reconciliation: find a local unmapped projectile matching the given
        /// source unit and ammo name. Returns the first match found, or null.
        /// </summary>
        private static ObjectBase FindUnmappedBySourceAndAmmo(int sourceUnitId, string ammoName)
        {
            if (string.IsNullOrEmpty(ammoName)) return null;

            ObjectBase result = null;
            result = SearchUnmapped<Missile>(sourceUnitId, ammoName);
            if (result != null) return result;
            result = SearchUnmapped<Torpedo>(sourceUnitId, ammoName);
            return result;
        }

        private static ObjectBase SearchUnmapped<T>(int sourceUnitId, string ammoName) where T : WeaponBase
        {
            System.Collections.Generic.IReadOnlyList<T> list;
            if (typeof(T) == typeof(Missile))
                list = (System.Collections.Generic.IReadOnlyList<T>)(object)UnitRegistry.Missiles;
            else
                list = (System.Collections.Generic.IReadOnlyList<T>)(object)UnitRegistry.Torpedoes;

            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p == null || p.IsDestroyed) continue;
                // Already mapped? Skip.
                if (_localToHost.ContainsKey(p.UniqueID)) continue;
                // Check source unit
                var launcher = StateSerializer.GetLaunchPlatform(p);
                if (launcher == null || launcher.UniqueID != sourceUnitId) continue;
                // Check ammo type
                if (p._ap?._ammunitionFileName != ammoName) continue;
                return p;
            }
            return null;
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
