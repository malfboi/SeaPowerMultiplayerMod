using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    public static class StateSerializer
    {
        public static int GetUniqueId(ObjectBase obj) => obj.UniqueID;

        // Compiled delegates for hot-path reflected fields (avoids FieldInfo.GetValue per-unit)
        private static readonly Func<Vessel, float> _getRudderAngle;
        private static readonly Func<WeaponBase, ObjectBase> _getLaunchPlatform;

        private static readonly StateUpdateMessage _pooledMsg = new();

        static StateSerializer()
        {
            var rudderField = AccessTools.Field(typeof(Vessel), "_setRudderAngle");
            if (rudderField != null)
            {
                var param = Expression.Parameter(typeof(Vessel));
                var access = Expression.Field(param, rudderField);
                _getRudderAngle = Expression.Lambda<Func<Vessel, float>>(access, param).Compile();
            }
            else
            {
                _getRudderAngle = _ => 0f;
            }

            var launchField = AccessTools.Field(typeof(WeaponBase), "_launchPlatform");
            if (launchField != null)
            {
                var param = Expression.Parameter(typeof(WeaponBase));
                var access = Expression.Field(param, launchField);
                var cast = Expression.TypeAs(access, typeof(ObjectBase));
                _getLaunchPlatform = Expression.Lambda<Func<WeaponBase, ObjectBase>>(cast, param).Compile();
            }
            else
            {
                _getLaunchPlatform = _ => null;
            }
        }

        /// <summary>
        /// Public accessor for the compiled _launchPlatform delegate, so other classes
        /// (e.g. StateBroadcaster) can use it without their own FieldInfo reflection.
        /// </summary>
        public static ObjectBase GetLaunchPlatform(WeaponBase wb) => _getLaunchPlatform(wb);

        /// <summary>
        /// Find any ObjectBase by UniqueID. Uses SceneCreator's fast dictionary first,
        /// falls back to global search for dynamically spawned objects (missiles, torpedoes)
        /// that aren't in the mission-file dictionary.
        /// </summary>
        public static ObjectBase FindById(int id)
        {
            var obj = Singleton<SceneCreator>.Instance.FindObjectById(id);
            if (obj != null) return obj;
            return SceneCreator.FindGlobalObjectById(id);
        }

        public static StateUpdateMessage Capture(Taskforce filterTaskforce = null,
            HashSet<int> includeEntityIds = null)
        {
            var msg = _pooledMsg;
            msg.Reset();
            msg.Timestamp   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            msg.GameSeconds = Singleton<SeaPower.Environment>.Instance.Hour * 3600f
                            + Singleton<SeaPower.Environment>.Instance.Minutes * 60f
                            + Singleton<SeaPower.Environment>.Instance.Seconds;

            AddRegistryUnits(msg, UnitType.Vessel,     UnitRegistry.Vessels,      filterTaskforce, includeEntityIds);
            AddRegistryUnits(msg, UnitType.Submarine,  UnitRegistry.Submarines,   filterTaskforce, includeEntityIds);
            AddRegistryUnits(msg, UnitType.Aircraft,   UnitRegistry.AircraftList, filterTaskforce, includeEntityIds);
            AddRegistryUnits(msg, UnitType.Helicopter, UnitRegistry.Helicopters,  filterTaskforce, includeEntityIds);
            AddRegistryUnits(msg, UnitType.LandUnit,   UnitRegistry.LandUnits,    filterTaskforce, includeEntityIds);
            // Biologics excluded — ambient sonar contacts, not gameplay units

            if (filterTaskforce == null)
            {
                // Co-op: all projectiles (host-authoritative)
                AddRegistryProjectiles(msg, 0, UnitRegistry.Missiles);
                AddRegistryProjectiles(msg, 1, UnitRegistry.Torpedoes);
            }
            else
            {
                // PvP: owned projectiles only (owner-authoritative)
                AddRegistryOwnedProjectiles(msg, 0, UnitRegistry.Missiles,  filterTaskforce);
                AddRegistryOwnedProjectiles(msg, 1, UnitRegistry.Torpedoes, filterTaskforce);
            }

            if (Plugin.Instance.CfgVerboseDebug.Value)
                Plugin.Log.LogDebug($"[Serialize] {msg.Units.Count} units, {msg.Projectiles.Count} projectiles");
            return msg;
        }

        private static void AddRegistryUnits<T>(StateUpdateMessage msg, UnitType kind,
            IReadOnlyList<T> units, Taskforce filterTf = null,
            HashSet<int> includeEntityIds = null) where T : ObjectBase
        {
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null) continue;
                if (filterTf != null && unit._taskforce != filterTf) continue;
                if (includeEntityIds != null && !includeEntityIds.Contains(GetUniqueId(unit))) continue;

                // Encode as absolute GeoPosition (longitude/latitude/height) so
                // positions are independent of each machine's floating origin.
                // In PvP each side centers on its own fleet, so transform.position
                // is relative to different origins on each machine.
                var geo = Utils.worldPositionFromUnityToLongLat(
                    unit.transform.position, Globals._currentCenterTile);
                float rudder = unit is Vessel vessel ? _getRudderAngle(vessel) : 0f;

                float desiredAlt = 0f;
                if (unit is Aircraft || unit is Helicopter || unit is Submarine)
                    desiredAlt = (float)unit.DesiredAltitude.Value;

                msg.Units.Add(new UnitState
                {
                    EntityId         = GetUniqueId(unit),
                    Kind             = kind,
                    X                = (float)geo._longitude,
                    Y                = (float)geo._height,
                    Z                = (float)geo._latitude,
                    Heading          = unit.transform.eulerAngles.y,
                    Speed            = unit._velocityInKnots,
                    IsDestroyed      = unit.IsDestroyed,
                    IsSinking        = unit.Compartments != null && unit.Compartments._isSinking,
                    RudderAngle      = rudder,
                    Telegraph        = unit.getTelegraph(),
                    DesiredAltitude  = desiredAlt,
                    Pitch            = unit.transform.eulerAngles.x,
                    Roll             = unit.transform.eulerAngles.z,
                    IntegrityPercent = unit.Compartments?.IntegrityPercentage ?? 100f,
                });
            }
        }

        private static void AddRegistryProjectiles<T>(StateUpdateMessage msg, byte kind,
            IReadOnlyList<T> projectiles) where T : ObjectBase
        {
            for (int i = 0; i < projectiles.Count; i++)
            {
                var p = projectiles[i];
                if (p == null || p.IsDestroyed) continue;

                // Fix #50: Skip chaff/countermeasures — not worth serializing.
                // Same pattern as Fix #40 (tracking) and Fix #49 (order routing).
                if (p is WeaponBase wb && wb._ap != null &&
                    (wb._ap._type == Ammunition.Type.Chaff || wb._ap._type == Ammunition.Type.Noisemaker))
                    continue;

                var pos = p.transform.position;
                msg.Projectiles.Add(new ProjectileState
                {
                    EntityId = GetUniqueId(p),
                    Kind     = kind,
                    X        = pos.x,
                    Y        = pos.y,
                    Z        = pos.z,
                    Heading  = p.transform.eulerAngles.y,
                });
            }
        }

        private static void AddRegistryOwnedProjectiles<T>(StateUpdateMessage msg, byte kind,
            IReadOnlyList<T> projectiles, Taskforce ownerTf) where T : ObjectBase
        {
            for (int i = 0; i < projectiles.Count; i++)
            {
                var p = projectiles[i];
                if (p == null || p.IsDestroyed) continue;
                var launcher = (p is WeaponBase wb) ? _getLaunchPlatform(wb) : null;
                if (launcher == null || launcher._taskforce != ownerTf) continue;

                // Encode as GeoPosition (floating-origin safe)
                var geo = Utils.worldPositionFromUnityToLongLat(
                    p.transform.position, Globals._currentCenterTile);

                msg.Projectiles.Add(new ProjectileState
                {
                    EntityId = GetUniqueId(p),
                    Kind     = kind,
                    X        = (float)geo._longitude,
                    Y        = (float)geo._height,
                    Z        = (float)geo._latitude,
                    Heading  = p.transform.eulerAngles.y,
                    Speed    = (p is WeaponBase wb2) ? wb2._velocityInKnots : 0f,
                    Pitch    = p.transform.eulerAngles.x,
                });
            }
        }
    }

    public static class StateApplier
    {
        /// <summary>
        /// Decompose total-seconds-since-midnight into Hour/Minutes/Seconds
        /// and assign all three to the Environment, avoiding minute/hour
        /// boundary bugs from setting Seconds alone.
        /// </summary>
        internal static void SetGameTime(SeaPower.Environment env, float totalSeconds)
        {
            if (totalSeconds < 0f) totalSeconds = 0f;
            int total = (int)totalSeconds;
            env.Hour    = (total / 3600) % 24;
            env.Minutes = (total % 3600) / 60;
            env.Seconds = totalSeconds - (total / 60) * 60f; // preserve fractional seconds
        }

        // ── Hybrid correction constants ────────────────────────────────────
        // Local sim runs, remote owner's state provides position/heading/speed corrections.
        // Ship/Submarine
        private const float ShipSnapThreshold = 75f;
        private const float ShipPosLerp       = 0.7f;
        private const float ShipHeadingLerp   = 0.8f;
        private const float ShipSpeedLerp     = 0.7f;
        // Aircraft/Helicopter
        private const float AirSnapThreshold  = 150f;
        private const float AirPosLerp        = 0.75f;
        private const float AirHeadingLerp    = 0.85f;
        private const float AirSpeedLerp      = 0.7f;

        // Track projectile IDs across state updates for disappearance detection.
        // If host's state update no longer includes a projectile, it was destroyed.
        private static HashSet<int> _prevProjectileIds = new HashSet<int>();
        private static HashSet<int> _currProjectileIds = new HashSet<int>();

        // ── Stats (read by UI) ──────────────────────────────────────────────

        public static int LastRemoteUnitCount => _lastSeenRemoteUnitIds.Count;
        public static int OrphanCandidateCount => _missedUpdateCount.Count;
        public static int ProjectilesDestroyedByTimeout { get; private set; }

        // Per-category drift (computed each Apply cycle)
        public static float ShipDriftAvg { get; private set; }
        public static float ShipDriftMax { get; private set; }
        public static float AirDriftAvg { get; private set; }
        public static float AirDriftMax { get; private set; }

        // Per-frame drift accumulators
        private static float _shipDriftSum, _shipDriftMaxAcc;
        private static int _shipCount;
        private static float _airDriftSum, _airDriftMaxAcc;
        private static int _airCount;

        // Drift logging timer
        private static float _nextDriftLogTime;

        // ── PvP missile disappearance tracking ──────────────────────────────
        private static readonly Dictionary<int, int> _missedProjectileCount = new();
        private const int MissedProjectileUpdatesBeforeDestroy = 30; // ~3s at 10Hz — generous grace for timing desync

        // ── PvP orphan cleanup ────────────────────────────────────────────────
        // Track which remote unit IDs appear in incoming state updates.
        // Units missing from multiple cleanup cycles get destroyed.
        private static readonly HashSet<int> _lastSeenRemoteUnitIds = new();
        private static readonly Dictionary<int, int> _missedUpdateCount = new(); // unitId → consecutive misses
        private const int MissedCyclesBeforeDestroy = 12; // ~120s (12 cleanup cycles × 10s)

        // ── State-sync-driven ID alignment ─────────────────────────────────
        private static bool _pendingAlignment;

        /// <summary>Called by SessionManager.OnSceneReady on the client to defer
        /// alignment until the first state update from the host arrives.</summary>
        internal static void SetPendingAlignment() => _pendingAlignment = true;

        // ── Per-frame projectile smoothing ──────────────────────────────────
        // Instead of snapping missiles to host positions every 500ms (visible jumps),
        // store the target and interpolate toward it each frame. The client's own
        // missile simulation provides the baseline movement; we only nudge for drift.
        private struct ProjectileTarget
        {
            public int    LocalId;
            public Vector3 Position;
            public float   Heading;
        }
        private static readonly Dictionary<int, ProjectileTarget> _projectileTargets = new();
        private static readonly List<int> _staleTargets = new();

        // Projectile smoothing constants
        private const float ProjDriftIgnore = 2f;     // ignore drift under this
        private const float ProjSnapThreshold = 500f;  // teleport above this
        private const float ProjSmoothSpeed = 8f;      // base lerp speed (multiplied by deltaTime)

        // Proximity-based correction: ramp up correction as projectile approaches target
        private const float ProxStartDist = 500f;  // start ramping correction at this distance to target
        private const float ProxMaxDist = 50f;      // full snap correction at this distance to target
        private const float ProxMaxLerp = 1f;        // lerp multiplier at closest range (instant snap)

        public static void Apply(StateUpdateMessage msg)
        {
            bool isHost = Plugin.Instance.CfgIsHost.Value;
            bool isPvP  = Plugin.Instance.CfgPvP.Value;

            // Co-op: only client applies (unchanged)
            if (!isPvP && isHost) return;

            // Don't process state updates until scene has loaded
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;

            // Client: run ID alignment on the first state update after scene load.
            // The host has live positions, so this is more accurate than save-file positions.
            if (_pendingAlignment)
            {
                _pendingAlignment = false;
                RunAlignmentFromStateUpdate(msg);
                return;
            }

            // PvP: need a valid _playerTaskforce to filter — bail if not set yet
            if (isPvP && Globals._playerTaskforce == null) return;

            // Reset per-frame drift accumulators. Populated below, surfaced by UI.
            _shipDriftSum = _shipDriftMaxAcc = 0f; _shipCount = 0;
            _airDriftSum = _airDriftMaxAcc = 0f; _airCount = 0;

            foreach (var state in msg.Units)
            {
                if (isPvP) _lastSeenRemoteUnitIds.Add(state.EntityId);
                var unit = StateSerializer.FindById(state.EntityId);
                if (unit == null)
                {
                    // Detect missing remote aircraft for recovery spawning
                    if (isPvP && (state.Kind == UnitType.Aircraft || state.Kind == UnitType.Helicopter))
                    {
                        bool isRemoteAircraft = isHost
                            ? state.EntityId >= 3_000_001
                            : state.EntityId >= 2_000_001 && state.EntityId < 3_000_000;
                        if (isRemoteAircraft)
                            FlightOpsHandler.OnMissingRemoteAircraft(state.EntityId);
                    }
                    continue;
                }

                // Skip projectiles found by coincidental ID collision with a unit ID
                if (unit is WeaponBase) continue;

                // PvP: skip own units — we are authoritative for them
                if (isPvP && unit._taskforce == Globals._playerTaskforce) continue;

                // PvP: skip aircraft still in the flight deck pipeline
                // (prevents state updates from fighting with elevator/taxi/takeoff positioning)
                if (isPvP && FlightOpsHandler.ShouldSkipStateUpdate(state.EntityId, unit)) continue;

                // Sinking sync: if remote is sinking, trigger sinking locally
                var comps = unit.Compartments;
                if (state.IsSinking && comps != null && !comps._isSinking)
                {
                    Plugin.Log.LogDebug($"[StateApplier] Unit {state.EntityId} sinking on remote — triggering local sink");
                    comps.Sink(Compartments.SinkFocus.All, false);
                    continue;
                }

                // If already sinking locally, let the animation play — don't hard-destroy
                if (comps != null && comps._isSinking) continue;

                // Instant destruction (explosion, no sinking phase)
                if (state.IsDestroyed && !unit.IsDestroyed)
                {
                    Plugin.Log.LogDebug($"[StateApplier] Unit {state.EntityId} destroyed on remote — destroying locally");
                    CombatEventHandler.DestroyFromNetwork(unit);
                    continue;
                }

                // Convert absolute GeoPosition back to local transform coordinates
                var geo = new GeoPosition
                {
                    _longitude = state.X,
                    _latitude  = state.Z,
                    _height    = state.Y,
                };
                Vector2 local = Utils.longLatToLocal(geo, Globals._currentCenterTile);
                Vector3 hostPos = new Vector3(local.x, state.Y, local.y);

                // Submarines: in co-op, depth is order-synced via setDepth() on the
                // same host, so don't let state updates fight the local depth physics.
                // In PvP, the owner is authoritative for depth — use their value.
                if (state.Kind == UnitType.Submarine && !isPvP)
                    hostPos.y = unit.transform.position.y;

                // PvP submarines: sync desired depth so local physics targets the
                // owner's commanded depth instead of fighting position corrections.
                if (state.Kind == UnitType.Submarine && isPvP)
                {
                    if (state.DesiredAltitude != 0f)
                        unit.DesiredAltitude.Value = state.DesiredAltitude;
                }

                // Fix #51: Aircraft position interpolation buffer (replaces Fix #46)
                // Three-tier correction: accept, lerp, or snap based on divergence magnitude.
                if (state.Kind == UnitType.Aircraft || state.Kind == UnitType.Helicopter)
                {
                    // Always sync DesiredAltitude so flight physics targets the right altitude
                    if (state.DesiredAltitude > 0f)
                        unit.DesiredAltitude.Value = state.DesiredAltitude;

                    bool isOnDeck = state.Y < 2.0f;

                    if (isOnDeck)
                    {
                        // On-deck/landing: snap precisely (taxiing, takeoff, landing)
                        hostPos.y = state.Y;
                    }
                    else
                    {
                        float yDrift = Mathf.Abs(unit.transform.position.y - state.Y);
                        float xzDrift = Vector2.Distance(
                            new Vector2(unit.transform.position.x, unit.transform.position.z),
                            new Vector2(hostPos.x, hostPos.z));

                        // Tier 1: Accept zone (<50 units / ~11,000 feet)
                        // Client position is close enough — local physics is accurate.
                        // Keep client's local position entirely.
                        if (yDrift < 50f && xzDrift < 50f)
                        {
                            hostPos = unit.transform.position;
                        }
                        // Tier 2: Smooth correction (50-500 units)
                        // Moderate divergence — lerp toward host position over multiple frames.
                        // LerpFactor 0.15 = ~15% correction per update at 2 Hz → smooth over ~3 updates.
                        else if (yDrift < 500f && xzDrift < 500f)
                        {
                            hostPos = Vector3.Lerp(unit.transform.position, hostPos, 0.15f);
                            hostPos.y = Mathf.Lerp(unit.transform.position.y, state.Y, 0.15f);
                        }
                        // Tier 3: Hard snap (>500 units)
                        // Extreme divergence — snap immediately to re-establish sync.
                        else
                        {
                            hostPos.y = state.Y;
                            Plugin.Log.LogWarning($"[StateApplier] Aircraft {unit.name} drift " +
                                $"Y={yDrift:F0} XZ={xzDrift:F0} exceeded 500, force-snapped");
                        }
                    }
                }

                // Hybrid correction: local physics simulates, owner's state provides corrections.
                bool isAir = state.Kind == UnitType.Aircraft || state.Kind == UnitType.Helicopter;
                float snapThresh = isAir ? AirSnapThreshold : ShipSnapThreshold;
                float posLerp    = isAir ? AirPosLerp       : ShipPosLerp;
                float hdgLerp    = isAir ? AirHeadingLerp   : ShipHeadingLerp;
                float spdLerp    = isAir ? AirSpeedLerp     : ShipSpeedLerp;

                float drift = Vector3.Distance(unit.transform.position, hostPos);

                if (isAir)
                {
                    _airDriftSum += drift;
                    if (drift > _airDriftMaxAcc) _airDriftMaxAcc = drift;
                    _airCount++;
                }
                else
                {
                    _shipDriftSum += drift;
                    if (drift > _shipDriftMaxAcc) _shipDriftMaxAcc = drift;
                    _shipCount++;
                }

                if (drift > snapThresh)
                {
                    unit.transform.position = hostPos;
                    unit.transform.eulerAngles = new Vector3(state.Pitch, state.Heading, state.Roll);
                    unit._velocityInKnots = state.Speed;
                }
                else
                {
                    unit.transform.position = Vector3.Lerp(unit.transform.position, hostPos, posLerp);
                    float heading = Mathf.LerpAngle(unit.transform.eulerAngles.y, state.Heading, hdgLerp);
                    float pitch = isAir
                        ? Mathf.LerpAngle(unit.transform.eulerAngles.x, state.Pitch, hdgLerp)
                        : unit.transform.eulerAngles.x;
                    float roll = isAir
                        ? Mathf.LerpAngle(unit.transform.eulerAngles.z, state.Roll, hdgLerp)
                        : unit.transform.eulerAngles.z;
                    unit.transform.eulerAngles = new Vector3(pitch, heading, roll);
                    unit._velocityInKnots = Mathf.Lerp(unit._velocityInKnots, state.Speed, spdLerp);
                }
            }

            // Finalize per-category drift stats
            ShipDriftAvg = _shipCount > 0 ? _shipDriftSum / _shipCount : 0f;
            ShipDriftMax = _shipDriftMaxAcc;
            AirDriftAvg  = _airCount  > 0 ? _airDriftSum  / _airCount  : 0f;
            AirDriftMax  = _airDriftMaxAcc;

            if (Time.time >= _nextDriftLogTime)
            {
                _nextDriftLogTime = Time.time + 30f;
                Plugin.Log.LogInfo($"[Drift] Ships: avg={ShipDriftAvg:F1}m max={ShipDriftMax:F1}m ({_shipCount} units) | Air: avg={AirDriftAvg:F1}m max={AirDriftMax:F1}m ({_airCount} units)");
            }

            // ── Game time drift correction (host is time authority) ──────────
            if (!isHost && msg.GameSeconds > 0f)
            {
                float rttSec = NetworkManager.Instance.LastRttMs / 2000f; // one-way delay
                float tc = GameTime.IsPaused() ? 0f : GameTime.TimeCompression;
                float estimatedHostTime = msg.GameSeconds + rttSec * tc;

                var env = Singleton<SeaPower.Environment>.Instance;
                float localTime = env.Hour * 3600f + env.Minutes * 60f + env.Seconds;
                float drift = estimatedHostTime - localTime;

                if (Mathf.Abs(drift) > 10f)
                {
                    // Hard snap: decompose total seconds into hour/min/sec
                    SetGameTime(env, estimatedHostTime);
                    Plugin.Log.LogWarning($"[TimeSync] Hard snap: drift={drift:F2}s");
                }
                else if (Mathf.Abs(drift) > 0.1f)
                {
                    // Soft correction: add fraction of drift to seconds.
                    // Environment.OnUpdate() handles rollover on next tick.
                    float correction = drift * 0.2f;
                    env.Seconds += correction;
                }
            }

            // ── Projectile sync ──────────────────────────────────────────────────
            if (isPvP)
            {
                // PvP: track enemy projectile IDs for disappearance detection.
                // Do NOT apply position corrections — both sides force-spawn enemy
                // missiles locally and run identical physics. Position overrides at
                // 10 Hz fight with local sim and cause visible jitter. Combat outcomes
                // (hits, intercepts, jamming) are synced via CombatEventMessage and
                // MissileStateSyncMessage, not position.
                _currProjectileIds.Clear();
                foreach (var proj in msg.Projectiles)
                {
                    _currProjectileIds.Add(proj.EntityId);
                }

                // Disappearance tracking: grace period for missed projectiles
                foreach (int id in _prevProjectileIds)
                {
                    if (_currProjectileIds.Contains(id))
                    {
                        _missedProjectileCount.Remove(id);
                        continue;
                    }

                    _missedProjectileCount.TryGetValue(id, out int misses);
                    misses++;
                    _missedProjectileCount[id] = misses;

                    if (misses >= MissedProjectileUpdatesBeforeDestroy)
                    {
                        var obj = StateSerializer.FindById(id);
                        if (obj != null && !obj.IsDestroyed)
                        {
                            CombatEventHandler.RunAsNetworkEvent(() => obj.notifyOfExternalDestruction());
                            ProjectilesDestroyedByTimeout++;
                            Plugin.Log.LogDebug($"[StateApplier] PvP: Missile {id} disappeared from owner's updates — destroyed");
                        }
                        _missedProjectileCount.Remove(id);
                    }
                }

                var temp = _prevProjectileIds;
                _prevProjectileIds = _currProjectileIds;
                _currProjectileIds = temp;
            }
            else
            {
                // Co-op: client-only projectile sync (unchanged)
                if (isHost) return;

                ProjectileIdMapper.CacheSceneProjectiles();
                try
                {
                    _currProjectileIds.Clear();
                    foreach (var proj in msg.Projectiles)
                    {
                        _currProjectileIds.Add(proj.EntityId);

                        var projWorldPos = new Vector3(proj.X, proj.Y, proj.Z);
                        ProjectileIdMapper.UpdateMapping(proj.EntityId, projWorldPos);

                        // Co-op: do NOT store position targets for local projectiles.
                        // Both sides run identical physics — force-spawned missiles fly
                        // on local sim. Position overrides cause jitter. We only need
                        // the ID mapping and destruction detection (below).
                    }

                    foreach (int id in _prevProjectileIds)
                    {
                        if (_currProjectileIds.Contains(id)) continue;
                        _projectileTargets.Remove(id);
                        var obj = ProjectileIdMapper.FindByHostId(id);
                        if (obj == null || obj.IsDestroyed) continue;

                        CombatEventHandler.RunAsNetworkEvent(() => obj.notifyOfExternalDestruction());
                        ProjectileIdMapper.OnProjectileDestroyed(obj.UniqueID);
                        ProjectilesDestroyedByTimeout++;
                        Plugin.Log.LogDebug($"[StateApplier] Projectile {id} disappeared from host update — destroyed local {obj.UniqueID}");
                    }

                    var temp = _prevProjectileIds;
                    _prevProjectileIds = _currProjectileIds;
                    _currProjectileIds = temp;
                }
                finally
                {
                    ProjectileIdMapper.ClearSceneCache();
                }
            }
        }

        private static void RunAlignmentFromStateUpdate(StateUpdateMessage msg)
        {
            int savedUid = Singleton<SceneCreator>.Instance._UID;
            var reassignments = new List<(ObjectBase obj, int hostId)>();

            foreach (var state in msg.Units)
            {
                if (StateSerializer.FindById(state.EntityId) != null)
                    continue; // already aligned

                var geo = new GeoPosition
                {
                    _longitude = state.X,
                    _latitude  = state.Z,
                    _height    = state.Y,
                };
                Vector2 local = Utils.longLatToLocal(geo, Globals._currentCenterTile);
                Vector3 worldPos = new Vector3(local.x, state.Y, local.y);

                var best = FindLocalByPosition(worldPos, state.Kind);
                if (best != null && best.UniqueID != state.EntityId)
                    reassignments.Add((best, state.EntityId));
            }

            if (reassignments.Count == 0)
            {
                Plugin.Log.LogInfo("[StateApplier] Alignment: all IDs already match");
                return;
            }

            // Pass 1: temp IDs to avoid collisions
            for (int i = 0; i < reassignments.Count; i++)
                reassignments[i].obj.SetUniqueId(-(i + 1));

            // Pass 2: assign host IDs
            foreach (var (obj, hostId) in reassignments)
                obj.SetUniqueId(hostId);

            Singleton<SceneCreator>.Instance._UID = savedUid;
            Plugin.Log.LogInfo($"[StateApplier] Alignment: {reassignments.Count} units remapped from first state update");
        }

        private static ObjectBase FindLocalByPosition(Vector3 worldPos, UnitType kind)
        {
            ObjectBase best = null;
            float bestDist = float.MaxValue;
            var all = UnitRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var obj = all[i];
                if (obj == null || obj.IsDestroyed) continue;
                if (!KindMatches(obj, kind)) continue;
                float d = (obj.transform.position - worldPos).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = obj; }
            }
            return best;
        }

        private static bool KindMatches(ObjectBase obj, UnitType kind) => kind switch
        {
            UnitType.Vessel     => obj is Vessel,
            UnitType.Submarine  => obj is Submarine,
            UnitType.Aircraft   => obj is Aircraft,
            UnitType.Helicopter => obj is Helicopter,
            UnitType.LandUnit   => obj is LandUnit,
            _                   => false,
        };

        /// <summary>
        /// Called every frame (from StateBroadcaster.Update) to smoothly interpolate
        /// projectile positions toward host targets instead of snapping at 2 Hz.
        /// Correction strength ramps up as the projectile approaches its target
        /// to ensure accurate impact location.
        /// </summary>
        public static void SmoothProjectiles()
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (_projectileTargets.Count == 0) return;

            float baseT = Mathf.Clamp01(Time.deltaTime * ProjSmoothSpeed);

            _staleTargets.Clear();
            foreach (var kvp in _projectileTargets)
            {
                var target = kvp.Value;
                var p = StateSerializer.FindById(target.LocalId);
                if (p == null || p.IsDestroyed)
                {
                    _staleTargets.Add(kvp.Key);
                    continue;
                }

                float drift = Vector3.Distance(p.transform.position, target.Position);
                if (drift < ProjDriftIgnore) continue;

                // Calculate proximity multiplier: ramp correction as projectile nears its target.
                // At >500 units from target: use base lerp.
                // At <50 units from target: snap to host position (lerp=1).
                float proxMul = 1f;
                if (p is SeaPower.WeaponBase wb && wb.CurrentIntendedTargetObject != null)
                {
                    float distToTarget = Vector3.Distance(p.transform.position,
                        wb.CurrentIntendedTargetObject.transform.position);
                    if (distToTarget < ProxStartDist)
                    {
                        // InverseLerp: 500→0 maps to 0→1, then lerp base→max
                        float proxT = 1f - Mathf.Clamp01((distToTarget - ProxMaxDist) / (ProxStartDist - ProxMaxDist));
                        proxMul = Mathf.Lerp(1f, ProxMaxLerp / Mathf.Max(baseT, 0.01f), proxT);
                    }
                }

                float t = Mathf.Clamp01(baseT * proxMul);

                if (drift > ProjSnapThreshold || t >= 0.99f)
                {
                    p.transform.position = target.Position;
                    p.transform.eulerAngles = new Vector3(
                        p.transform.eulerAngles.x, target.Heading, p.transform.eulerAngles.z);
                }
                else
                {
                    p.transform.position = Vector3.Lerp(p.transform.position, target.Position, t);
                    float heading = Mathf.LerpAngle(p.transform.eulerAngles.y, target.Heading, t);
                    p.transform.eulerAngles = new Vector3(
                        p.transform.eulerAngles.x, heading, p.transform.eulerAngles.z);
                }
            }

            foreach (int id in _staleTargets)
                _projectileTargets.Remove(id);
        }

        private static int _cachedLocalUnitCount;
        private static float _lastUnitCountTime;
        private const float UnitCountCacheInterval = 1f;

        private static int CountLocalUnits(Taskforce filterTf = null)
        {
            if (filterTf == null && Time.unscaledTime - _lastUnitCountTime < UnitCountCacheInterval)
                return _cachedLocalUnitCount;

            int count = 0;
            CountRegistryType(UnitRegistry.Vessels, filterTf, ref count);
            CountRegistryType(UnitRegistry.Submarines, filterTf, ref count);
            CountRegistryType(UnitRegistry.AircraftList, filterTf, ref count);
            CountRegistryType(UnitRegistry.Helicopters, filterTf, ref count);
            CountRegistryType(UnitRegistry.LandUnits, filterTf, ref count);

            if (filterTf == null)
            {
                _cachedLocalUnitCount = count;
                _lastUnitCountTime = Time.unscaledTime;
            }
            return count;
        }

        private static void CountRegistryType<T>(IReadOnlyList<T> units, Taskforce filterTf, ref int count) where T : ObjectBase
        {
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u != null && (filterTf == null || u._taskforce == filterTf)) count++;
            }
        }

        /// <summary>
        /// PvP orphan cleanup: destroy local enemy units that the remote side is
        /// no longer reporting. Uses a grace period to handle packet loss.
        /// Called periodically from StateBroadcaster.
        /// Note: only checks units, NOT projectiles — projectile disappearance
        /// is tracked separately in the Apply() PvP projectile processing path.
        /// </summary>
        public static void CleanupOrphans()
        {
            if (!Plugin.Instance.CfgPvP.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;
            if (_lastSeenRemoteUnitIds.Count == 0) return;

            var enemyTf = Globals._enemyTaskforce;
            if (enemyTf == null) return;

            CheckRegistryOrphans(UnitRegistry.Vessels, enemyTf);
            CheckRegistryOrphans(UnitRegistry.Submarines, enemyTf);
            CheckRegistryOrphans(UnitRegistry.AircraftList, enemyTf);
            CheckRegistryOrphans(UnitRegistry.Helicopters, enemyTf);
            CheckRegistryOrphans(UnitRegistry.LandUnits, enemyTf);

            // Clear after processing — start fresh for next cleanup window.
            // With staggered updates, IDs accumulate across messages between windows.
            _lastSeenRemoteUnitIds.Clear();
        }

        private static void CheckRegistryOrphans<T>(IReadOnlyList<T> units, Taskforce enemyTf) where T : ObjectBase
        {
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null || unit.IsDestroyed) continue;
                if (unit._taskforce != enemyTf) continue;

                // Skip aircraft managed by the flight ops pipeline (awaiting ID remap
                // or still in pipeline after remap). Their local IDs won't appear in
                // state updates until remap completes and they become airborne.
                if ((unit is Aircraft || unit is Helicopter) &&
                    FlightOpsHandler.IsProtectedFromOrphanCleanup(unit))
                    continue;

                int id = StateSerializer.GetUniqueId(unit);
                if (_lastSeenRemoteUnitIds.Contains(id))
                {
                    _missedUpdateCount.Remove(id);
                    continue;
                }

                _missedUpdateCount.TryGetValue(id, out int misses);
                misses++;
                _missedUpdateCount[id] = misses;

                if (misses >= MissedCyclesBeforeDestroy)
                {
                    Plugin.Log.LogWarning($"[OrphanCleanup] Destroying {typeof(T).Name} {id} — missing from {misses} cleanup cycles");
                    CombatEventHandler.DestroyFromNetwork(unit);
                    _missedUpdateCount.Remove(id);
                }
            }
        }

        /// <summary>Clear orphan tracking state (call on disconnect/scene change).</summary>
        public static void ResetOrphanTracking()
        {
            _lastSeenRemoteUnitIds.Clear();
            _missedUpdateCount.Clear();
            _pendingAlignment = false;
            ProjectilesDestroyedByTimeout = 0;
            ShipDriftAvg = ShipDriftMax = 0f;
            AirDriftAvg  = AirDriftMax  = 0f;
            ChangeTracker.Clear();
        }
    }

    public static class OrderHandler
    {
        /// <summary>
        /// True while applying an order received from the network.
        /// Checked by Harmony Prefixes to avoid re-sending orders back.
        /// </summary>
        internal static bool ApplyingFromNetwork;

        private static int _orderNotFoundCount;
        private static float _lastOrderNotFoundLogTime;

        private static readonly Dictionary<(int, Messages.OrderType), (float lastLogTime, int suppressedCount)> _logThrottle = new();
        private const float LogInterval = 10f;

        public static void ClearLogThrottle() => _logThrottle.Clear();

        private static Vehicle FindVehicleForUnit(ObjectBase unit)
        {
            if (Globals._playerTaskforce == null) return null;
            return Globals._playerTaskforce.PlottingTable?.VehicleForObject(unit);
        }

        public static void Apply(PlayerOrderMessage msg)
        {
            if (SessionManager.SceneLoading || SimSyncManager.CurrentState != SimState.Synchronized) return;

            var unit = StateSerializer.FindById(msg.SourceEntityId);
            if (unit == null)
            {
                _orderNotFoundCount++;
                if (Time.unscaledTime - _lastOrderNotFoundLogTime > 10f)
                {
                    Plugin.Log.LogWarning($"[Order] id={msg.SourceEntityId} not found (order={msg.Order}) — {_orderNotFoundCount} total missed");
                    _orderNotFoundCount = 0;
                    _lastOrderNotFoundLogTime = Time.unscaledTime;
                }
                return;
            }

            var logKey = (msg.SourceEntityId, msg.Order);
            if (_logThrottle.TryGetValue(logKey, out var throttle) && Time.unscaledTime - throttle.lastLogTime < LogInterval)
            {
                _logThrottle[logKey] = (throttle.lastLogTime, throttle.suppressedCount + 1);
            }
            else
            {
                string suffix = (throttle.suppressedCount > 0) ? $" (suppressed {throttle.suppressedCount} similar)" : "";

                // Fix #47: Skip generic log for orders that have their own specific logging
                if (msg.Order != Messages.OrderType.ReturnToBase
                    && msg.Order != Messages.OrderType.SetAltitude
                    && msg.Order != Messages.OrderType.ClassifyContact)
                {
                    Plugin.Log.LogInfo($"[Order] entity={msg.SourceEntityId} order={msg.Order} unit={unit.name}{suffix}");
                }
                _logThrottle[logKey] = (Time.unscaledTime, 0);
            }

            ApplyingFromNetwork = true;
            OrderDeduplicator.UpdateCache(msg); // track received values so local patches won't re-send
            try
            {
                switch (msg.Order)
                {
                    case Messages.OrderType.SetSpeed:
                        if (unit is Vessel v) v.setTelegraph((int)msg.Speed);
                        break;

                    // SetHeading removed — setRudderAngle patch was semantically wrong
                    // (sent rudder angle as heading). Heading syncs via waypoints + state corrections.

                    case Messages.OrderType.MoveTo:
                        unit.setWaypointTask(new GeoPosition
                        {
                            _longitude = msg.DestX,
                            _latitude  = msg.DestZ,
                            _height    = msg.DestY,
                        });
                        break;

                    case Messages.OrderType.FireWeapon:
                    {
                        // PvP: authorize enemy ship to spawn weapons from this order
                        if (Plugin.Instance.CfgPvP.Value
                            && unit._taskforce != Globals._playerTaskforce)
                        {
                            PvPFireAuth.Authorize(unit.UniqueID, msg.ShotsToFire);
                            Patch_ObjectBase_HandleEngageTasks.MarkNetworkOrdered(unit.UniqueID);
                            Plugin.Log.LogInfo($"[PvPAuth] FireWeapon: authorized {msg.ShotsToFire} shots for unit {unit.UniqueID} ({unit.name}), total auth now={PvPFireAuth.ActiveAuthForUnit(unit.UniqueID)}");
                        }

                        ObjectBase? target = null;
                        if (msg.TargetEntityId > 0)
                        {
                            // Try direct ID first (works for units with stable IDs),
                            // then IdMapper (for projectiles with divergent IDs)
                            target = StateSerializer.FindById(msg.TargetEntityId)
                                  ?? ProjectileIdMapper.FindByHostId(msg.TargetEntityId);
                        }
                        var targetPos = new Vector3(msg.TargetX, msg.TargetY, msg.TargetZ);
                        // PvP: coordinates are always GeoPosition — always convert back.
                        // This is needed even when target is found, because the game falls
                        // back to targetPosition if the target is destroyed mid-flight.
                        if (Plugin.Instance.CfgPvP.Value)
                        {
                            var geo = new GeoPosition { _longitude = msg.TargetX, _latitude = msg.TargetZ, _height = msg.TargetY };
                            Vector2 local = Utils.longLatToLocal(geo, Globals._currentCenterTile);
                            targetPos = new Vector3(local.x, msg.TargetY, local.y);
                        }
                        if (target != null)
                            unit.AddEngageTask(new EngageTask(msg.AmmoId, target,       unit, msg.ShotsToFire));
                        else
                            unit.AddEngageTask(new EngageTask(msg.AmmoId, targetPos, unit, msg.ShotsToFire));
                        break;
                    }

                    case Messages.OrderType.AutoFireWeapon:
                    {
                        long recvMs = AIAutoFireState.DiagMs;

                        // PvP: authorize enemy ship to spawn weapons from this order.
                        // The actual missile launch is handled by TryForceSpawn when the
                        // ProjectileSpawnMessage arrives (at the correct time — when the remote
                        // side actually fires). We skip InsertEngageTask to prevent the puppet
                        // from firing too early (engage task with _engageDelay=0 fires before
                        // the remote side's reaction delay expires).
                        if (Plugin.Instance.CfgPvP.Value
                            && unit._taskforce != Globals._playerTaskforce)
                        {
                            PvPFireAuth.Authorize(unit.UniqueID, msg.ShotsToFire);
                            Patch_ObjectBase_HandleEngageTasks.MarkNetworkOrdered(unit.UniqueID);
                            Plugin.Log.LogInfo($"[PvPAuth] AutoFire: authorized {msg.ShotsToFire} shots for unit {unit.UniqueID} ({unit.name}), total auth now={PvPFireAuth.ActiveAuthForUnit(unit.UniqueID)}");
                            break; // TryForceSpawn via ProjectileSpawnMessage fires at the right time
                        }

                        ObjectBase? target = null;
                        if (msg.TargetEntityId > 0)
                        {
                            target = StateSerializer.FindById(msg.TargetEntityId)
                                  ?? ProjectileIdMapper.FindByHostId(msg.TargetEntityId);
                        }
                        var targetPos = new Vector3(msg.TargetX, msg.TargetY, msg.TargetZ);
                        // PvP: coordinates are always GeoPosition — always convert back.
                        // This is needed even when target is found, because the game falls
                        // back to targetPosition if the target is destroyed mid-flight.
                        if (Plugin.Instance.CfgPvP.Value)
                        {
                            var geo = new GeoPosition { _longitude = msg.TargetX, _latitude = msg.TargetZ, _height = msg.TargetY };
                            Vector2 local = Utils.longLatToLocal(geo, Globals._currentCenterTile);
                            targetPos = new Vector3(local.x, msg.TargetY, local.y);
                        }
                        int priority = (int)msg.Heading;

                        // Log weapon system state BEFORE inserting the engage task
                        string wsState = "";
                        if (unit._obp != null)
                        {
                            foreach (var ws in unit._obp._weaponSystems)
                            {
                                wsState += $"\n    executing={ws._executingEngageTask} " +
                                    $"autoEngaging={ws._isAutoEngaging} delay={ws._engageDelay:F3}s";
                            }
                        }
                        if (Plugin.Instance.CfgVerboseDebug.Value)
                            Plugin.Log.LogDebug($"[AutoFire DIAG] t={recvMs}ms RECV_APPLY " +
                                $"unit={unit.UniqueID} name={unit.name} ammo={msg.AmmoId} " +
                                $"target={msg.TargetEntityId} targetFound={target != null} " +
                                $"targetName={target?.name ?? "pos"} shots={msg.ShotsToFire} " +
                                $"priority={priority} isHost={Plugin.Instance.CfgIsHost.Value}" +
                                $"{wsState}");

                        unit.InsertEngageTask(msg.AmmoId, target, targetPos, msg.ShotsToFire,
                                              priority, true, false, false);

                        // Log weapon system state AFTER inserting
                        string wsStateAfter = "";
                        if (unit._obp != null)
                        {
                            foreach (var ws in unit._obp._weaponSystems)
                            {
                                if (ws._executingEngageTask)
                                {
                                    wsStateAfter += $"\n    delay={ws._engageDelay:F3}s " +
                                        $"autoEngaging={ws._isAutoEngaging}";
                                }
                            }
                        }
                        if (wsStateAfter.Length > 0 && Plugin.Instance.CfgVerboseDebug.Value)
                            Plugin.Log.LogDebug($"[AutoFire DIAG] t={AIAutoFireState.DiagMs}ms POST_INSERT " +
                                $"unit={unit.UniqueID}{wsStateAfter}");
                        break;
                    }

                    case Messages.OrderType.SetDepth:
                        if (unit is Submarine sub) sub.setDepth(msg.Speed);
                        break;

                    case Messages.OrderType.CeaseFire:
                    {
                        // PvP: suppress radio report for enemy units so players
                        // don't hear the other side's comms
                        bool report = !(Plugin.Instance.CfgPvP.Value
                                     && unit._taskforce != Globals._playerTaskforce);
                        unit.CeaseFire(report, true, true, false, true, true);
                        break;
                    }

                    case Messages.OrderType.SetWeaponStatus:
                        unit.SetWeaponStatus((ObjectBase.WeaponStatus)(int)msg.Speed, false);
                        break;

                    case Messages.OrderType.SetEMCON:
                        unit.setEMCON(msg.Speed > 0f, false);
                        break;

                    case Messages.OrderType.SensorToggle:
                    {
                        int group = (int)msg.Heading;
                        bool enable = msg.Speed > 0f;
                        switch (group)
                        {
                            case 0: if (enable) unit.EnableAirSearchRadars(); else unit.DisableAirSearchRadars(); break;
                            case 1: if (enable) unit.EnableSurfaceSearchRadars(); else unit.DisableSurfaceSearchRadars(); break;
                            case 2: if (enable) unit.EnableActiveSonars(); else unit.DisableActiveSonars(); break;

                        }
                        break;
                    }

                    case Messages.OrderType.SubmarineMast:
                    {
                        if (unit is Submarine mastSub)
                        {
                            switch ((int)msg.Heading)
                            {
                                case 0: mastSub.toggleSnorkelMast(); break;
                                case 1: mastSub.togglePeriscopeMast(); break;
                                case 2: mastSub.toggleRadarMast(); break;
                                case 3: mastSub.toggleESMMast(); break;
                            }
                        }
                        break;
                    }

                    case Messages.OrderType.RemoveWaypoints:
                        unit.RemoveWaypoints();
                        break;

                    case Messages.OrderType.DeleteWaypoint:
                    {
                        int wpIndex = (int)msg.Speed;
                        var root = unit._userRoot;
                        if (root != null && wpIndex >= 0 && wpIndex < root.TaskViewModels.Count)
                            root.DeleteTask(root.TaskViewModels[wpIndex].Task);
                        break;
                    }

                    case Messages.OrderType.EditWaypoint:
                    {
                        int wpIdx = (int)msg.Speed;
                        var root = unit._userRoot;
                        if (root != null && wpIdx >= 0 && wpIdx < root.TaskViewModels.Count)
                        {
                            if (root.TaskViewModels[wpIdx].Task is GoToWaypointTask wp)
                                wp._waypointGeoPos.value = new GeoPosition
                                {
                                    _longitude = msg.DestX,
                                    _latitude  = msg.DestZ,
                                    _height    = msg.DestY,
                                };
                        }
                        break;
                    }

                    case Messages.OrderType.DropSonobuoy:
                    {
                        // PvP: authorize the enemy helicopter to spawn a weapon (sonobuoy Bomb)
                        if (Plugin.Instance.CfgPvP.Value
                            && unit._taskforce != Globals._playerTaskforce)
                        {
                            PvPFireAuth.Authorize(unit.UniqueID, 1);
                            Patch_ObjectBase_HandleEngageTasks.MarkNetworkOrdered(unit.UniqueID);
                        }

                        Vector3 dropPos;
                        if (Plugin.Instance.CfgPvP.Value)
                        {
                            // PvP: coordinates are GeoPosition (floating-origin safe)
                            var geo = new GeoPosition { _longitude = msg.DestX, _latitude = msg.DestZ, _height = msg.DestY };
                            Vector2 local = Utils.longLatToLocal(geo, Globals._currentCenterTile);
                            dropPos = new Vector3(local.x, msg.DestY, local.y);
                        }
                        else
                        {
                            // Co-op: coordinates are already in local Unity space (shared origin)
                            dropPos = new Vector3(msg.DestX, msg.DestY, msg.DestZ);
                        }

                        unit.AddEngageTask(new EngageTask(msg.AmmoId, dropPos, unit, 1));
                        Plugin.Log.LogInfo($"[Sonobuoy] Applied drop: unit={unit.UniqueID} ammo={msg.AmmoId}");
                        break;
                    }

                    case Messages.OrderType.SetAltitude:
                    {
                        int preset = (int)msg.Speed;
                        bool updateAlt = msg.Heading > 0.5f;

                        if (unit is Aircraft aircraft)
                        {
                            OrderHandler.ApplyingFromNetwork = true;
                            try { aircraft.setPresetHeight(preset, updateAlt); }
                            finally { OrderHandler.ApplyingFromNetwork = false; }
                        }
                        else if (unit is Helicopter helicopter)
                        {
                            OrderHandler.ApplyingFromNetwork = true;
                            try { helicopter.setPresetHeight(preset, updateAlt); }
                            finally { OrderHandler.ApplyingFromNetwork = false; }
                        }
                        Plugin.Log.LogInfo($"[Order] Applied SetAltitude for {unit?.name} (id={msg.SourceEntityId}): preset={preset}, updateWaypoints={updateAlt}");
                        break;
                    }

                    case Messages.OrderType.ReturnToBase:
                    {
                        ObjectBase homeBase = null;
                        if (msg.TargetEntityId != 0)
                            homeBase = StateSerializer.FindById(msg.TargetEntityId);

                        OrderHandler.ApplyingFromNetwork = true;
                        try { unit.setOrder(Order.Type.ReturnToBase, homeBase, displayOrderText: true); }
                        finally { OrderHandler.ApplyingFromNetwork = false; }
                        Plugin.Log.LogInfo($"[Order] Applied ReturnToBase for {unit?.name} (id={msg.SourceEntityId}): homeBase={homeBase?.name ?? "null"}");
                        break;
                    }

                    case Messages.OrderType.ClassifyContact:
                    {
                        RelationsState classification = (RelationsState)(int)msg.Speed;

                        Vehicle vehicle = FindVehicleForUnit(unit);
                        if (vehicle != null)
                        {
                            OrderHandler.ApplyingFromNetwork = true;
                            try { vehicle.OverrideRelationship(classification); }
                            finally { OrderHandler.ApplyingFromNetwork = false; }

                            // Fix #53: Force UI refresh for relationship change.
                            // MapUnitViewModel subscribes to property changes but has no subscription
                            // for relationship/classification changes. Trigger a property notification
                            // to force the radar display to re-render with the new classification color.
                            try
                            {
                                var onPropChanged = typeof(Vehicle).GetMethod("OnPropertyChanged",
                                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                                onPropChanged?.Invoke(vehicle, new object[] { "CurrentRelationship" });
                            }
                            catch (Exception ex)
                            {
                                Plugin.Log.LogDebug($"[Order] ClassifyContact UI refresh failed (non-critical): {ex.Message}");
                            }

                            Plugin.Log.LogInfo($"[Order] Applied ClassifyContact for {unit.name} (id={msg.SourceEntityId}): " +
                                              $"classification={classification}");
                        }
                        else
                        {
                            Plugin.Log.LogWarning($"[Order] ClassifyContact: No Vehicle found for {unit.name} (id={msg.SourceEntityId}), " +
                                                 $"classification={classification}");
                        }
                        break;
                    }

                    default:
                        Plugin.Log.LogWarning($"[Order] unhandled: {msg.Order}");
                        break;
                }
            }
            finally
            {
                ApplyingFromNetwork = false;
            }
        }
    }

    public static class GameEventHandler
    {
        public static void Apply(GameEventMessage msg)
        {
            Plugin.Log.LogInfo($"[Event] {msg.EventType}  src={msg.SourceEntityId}  tgt={msg.TargetEntityId}  param={msg.Param}");

            switch (msg.EventType)
            {
                case GameEventType.TimeChanged:
                    if (Plugin.Instance.CfgIsHost.Value)
                    {
                        TimeSyncManager.OnHostReceivedRequest(msg.Param);
                    }
                    else
                    {
                        float hostSeconds = System.BitConverter.ToSingle(
                            System.BitConverter.GetBytes(msg.SourceEntityId), 0);
                        TimeSyncManager.OnClientReceivedConfirm(msg.Param, hostSeconds);
                    }
                    break;

                case GameEventType.TaskforceAssigned:
                    if (!Plugin.Instance.CfgIsHost.Value)
                        TaskforceAssignmentManager.OnAssignmentReceived(msg.Param);
                    break;

                case GameEventType.HardSyncRequest:
                    if (Plugin.Instance.CfgIsHost.Value)
                    {
                        Plugin.Log.LogWarning("[HardSync] Client requested manual resync");
                        SessionManager.CaptureAndSend();
                    }
                    break;

                case GameEventType.TimeProposal:
                    TimeSyncManager.OnProposalReceived(msg.Param, fromHost: !Plugin.Instance.CfgIsHost.Value);
                    break;

                case GameEventType.TimeProposalResponse:
                    TimeSyncManager.OnProposalResponseReceived(msg.Param);
                    break;

                case GameEventType.UnitSelected:
                    UnitLockManager.OnRemoteSelected((int)msg.Param);
                    break;

                case GameEventType.UnitDeselected:
                    UnitLockManager.OnRemoteDeselected();
                    break;
            }
        }
    }
}
