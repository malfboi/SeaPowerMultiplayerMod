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

        public static StateUpdateMessage Capture(Taskforce filterTaskforce = null)
        {
            var msg = _pooledMsg;
            msg.Reset();
            msg.Timestamp   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            msg.GameSeconds = Singleton<SeaPower.Environment>.Instance.Hour * 3600f
                            + Singleton<SeaPower.Environment>.Instance.Minutes * 60f
                            + Singleton<SeaPower.Environment>.Instance.Seconds;

            AddRegistryUnits(msg, UnitType.Vessel,     UnitRegistry.Vessels,      filterTaskforce);
            AddRegistryUnits(msg, UnitType.Submarine,  UnitRegistry.Submarines,   filterTaskforce);
            AddRegistryUnits(msg, UnitType.Aircraft,   UnitRegistry.AircraftList, filterTaskforce);
            AddRegistryUnits(msg, UnitType.Helicopter, UnitRegistry.Helicopters,  filterTaskforce);
            AddRegistryUnits(msg, UnitType.LandUnit,   UnitRegistry.LandUnits,    filterTaskforce);
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

            Plugin.Log.LogDebug($"[Serialize] {msg.Units.Count} units, {msg.Projectiles.Count} projectiles");
            return msg;
        }

        private static void AddRegistryUnits<T>(StateUpdateMessage msg, UnitType kind,
            IReadOnlyList<T> units, Taskforce filterTf = null) where T : ObjectBase
        {
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null) continue;
                if (filterTf != null && unit._taskforce != filterTf) continue;

                // Encode as absolute GeoPosition (longitude/latitude/height) so
                // positions are independent of each machine's floating origin.
                // In PvP each side centers on its own fleet, so transform.position
                // is relative to different origins on each machine.
                var geo = Utils.worldPositionFromUnityToLongLat(
                    unit.transform.position, Globals._currentCenterTile);
                float rudder = unit is Vessel vessel ? _getRudderAngle(vessel) : 0f;

                float desiredAlt = 0f;
                if (unit is Aircraft || unit is Helicopter)
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

        private const float SnapThreshold  = 500f;  // teleport if drift exceeds this (co-op)

        // ── PvP hybrid correction constants ────────────────────────────────
        // Fixed strong corrections — owner is definitively authoritative.
        // Ship/Submarine
        private const float PvpShipSnapThreshold = 150f;
        private const float PvpShipPosLerp       = 0.5f;
        private const float PvpShipHeadingLerp   = 0.6f;
        private const float PvpShipSpeedLerp     = 0.5f;
        // Aircraft/Helicopter
        private const float PvpAirSnapThreshold  = 300f;
        private const float PvpAirPosLerp        = 0.6f;
        private const float PvpAirHeadingLerp    = 0.7f;
        private const float PvpAirSpeedLerp      = 0.5f;
        // Projectiles (missiles/torpedoes)
        private const float PvpProjSnapThreshold = 100f;
        private const float PvpProjPosLerp       = 0.7f;
        private const float PvpProjHeadingLerp   = 0.8f;
        private const float PvpProjSpeedLerp     = 0.6f;

        // Track projectile IDs across state updates for disappearance detection.
        // If host's state update no longer includes a projectile, it was destroyed.
        private static HashSet<int> _prevProjectileIds = new HashSet<int>();
        private static HashSet<int> _currProjectileIds = new HashSet<int>();

        // ── Stats (read by UI) ──────────────────────────────────────────────

        public static int LastRemoteUnitCount => _lastSeenRemoteUnitIds.Count;
        public static int OrphanCandidateCount => _missedUpdateCount.Count;
        public static int ProjectilesDestroyedByTimeout { get; private set; }

        // PvP per-category drift (computed each Apply cycle)
        public static float PvpShipDriftAvg { get; private set; }
        public static float PvpShipDriftMax { get; private set; }
        public static float PvpAirDriftAvg { get; private set; }
        public static float PvpAirDriftMax { get; private set; }

        // Per-frame PvP drift accumulators
        private static float _pvpShipDriftSum, _pvpShipDriftMaxAcc;
        private static int _pvpShipCount;
        private static float _pvpAirDriftSum, _pvpAirDriftMaxAcc;
        private static int _pvpAirCount;

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

            // Co-op: drift detection (PvP puppets have no drift)
            float lerpFactor = 0f, driftThreshold = 0f;
            bool correctSpeed = false;
            if (!isPvP)
            {
                DriftDetector.BeginFrame();
                lerpFactor = DriftDetector.EffectiveLerpFactor;
                driftThreshold = DriftDetector.EffectiveDriftThreshold;
                correctSpeed = DriftDetector.ShouldCorrectSpeed;
            }

            // PvP: rebuild remote unit ID set for orphan cleanup (populated in loop below)
            if (isPvP)
            {
                _lastSeenRemoteUnitIds.Clear();
                _pvpShipDriftSum = _pvpShipDriftMaxAcc = 0f; _pvpShipCount = 0;
                _pvpAirDriftSum = _pvpAirDriftMaxAcc = 0f; _pvpAirCount = 0;
            }

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

                // Submarines: depth is order-synced via setDepth(), don't let
                // state updates fight the local depth-change physics.
                if (state.Kind == UnitType.Submarine)
                    hostPos.y = unit.transform.position.y;

                if (isPvP)
                {
                    // PvP hybrid: local physics simulates, owner's state provides corrections
                    bool isAir = state.Kind == UnitType.Aircraft || state.Kind == UnitType.Helicopter;
                    float snapThresh = isAir ? PvpAirSnapThreshold : PvpShipSnapThreshold;
                    float posLerp    = isAir ? PvpAirPosLerp       : PvpShipPosLerp;
                    float hdgLerp    = isAir ? PvpAirHeadingLerp   : PvpShipHeadingLerp;
                    float spdLerp    = isAir ? PvpAirSpeedLerp     : PvpShipSpeedLerp;

                    float drift = Vector3.Distance(unit.transform.position, hostPos);

                    // Record per-category drift for UI
                    if (isAir)
                    {
                        _pvpAirDriftSum += drift;
                        if (drift > _pvpAirDriftMaxAcc) _pvpAirDriftMaxAcc = drift;
                        _pvpAirCount++;
                    }
                    else
                    {
                        _pvpShipDriftSum += drift;
                        if (drift > _pvpShipDriftMaxAcc) _pvpShipDriftMaxAcc = drift;
                        _pvpShipCount++;
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
                else
                {
                    // Co-op: drift-based correction (unchanged)
                    float drift = Vector3.Distance(unit.transform.position, hostPos);
                    float speedDrift = Mathf.Abs(unit._velocityInKnots - state.Speed);
                    float headingDrift = Mathf.Abs(Mathf.DeltaAngle(unit.transform.eulerAngles.y, state.Heading));
                    DriftDetector.RecordUnit(drift, speedDrift, headingDrift);

                    if (drift < driftThreshold) continue;

                    DriftDetector.RecordCorrection();

                    if (drift > SnapThreshold)
                    {
                        unit.transform.position = hostPos;
                        unit.transform.eulerAngles = new Vector3(
                            unit.transform.eulerAngles.x, state.Heading, unit.transform.eulerAngles.z);
                    }
                    else
                    {
                        unit.transform.position = Vector3.Lerp(unit.transform.position, hostPos, lerpFactor);
                        float correctedHeading = Mathf.LerpAngle(
                            unit.transform.eulerAngles.y, state.Heading, lerpFactor);
                        unit.transform.eulerAngles = new Vector3(
                            unit.transform.eulerAngles.x, correctedHeading, unit.transform.eulerAngles.z);
                    }

                    if (correctSpeed)
                        unit._velocityInKnots = Mathf.Lerp(unit._velocityInKnots, state.Speed, Mathf.Min(lerpFactor, 0.3f));
                }
            }

            // Finalize per-category drift stats for PvP
            if (isPvP)
            {
                PvpShipDriftAvg = _pvpShipCount > 0 ? _pvpShipDriftSum / _pvpShipCount : 0f;
                PvpShipDriftMax = _pvpShipDriftMaxAcc;
                PvpAirDriftAvg = _pvpAirCount > 0 ? _pvpAirDriftSum / _pvpAirCount : 0f;
                PvpAirDriftMax = _pvpAirDriftMaxAcc;
            }

            // DriftDetector: skip for PvP (puppets have no drift)
            if (!isPvP)
                DriftDetector.EndFrame(CountLocalUnits(), msg.Units.Count);

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
                // PvP: correct enemy missile/torpedo positions with authoritative lerp
                _currProjectileIds.Clear();
                foreach (var proj in msg.Projectiles)
                {
                    _currProjectileIds.Add(proj.EntityId);

                    var projObj = StateSerializer.FindById(proj.EntityId);
                    if (projObj == null || projObj.IsDestroyed) continue;

                    // Skip Bombs (sonobuoys) found by coincidental ID collision
                    // with a remote missile/torpedo — sonobuoys are stationary and
                    // not included in projectile state sync
                    if (projObj is Bomb) continue;

                    // Convert GeoPosition to local coords
                    var projGeo = new GeoPosition
                    {
                        _longitude = proj.X,
                        _latitude  = proj.Z,
                        _height    = proj.Y,
                    };
                    Vector2 projLocal = Utils.longLatToLocal(projGeo, Globals._currentCenterTile);
                    Vector3 projPos = new Vector3(projLocal.x, proj.Y, projLocal.y);

                    float projDrift = Vector3.Distance(projObj.transform.position, projPos);

                    if (projDrift > PvpProjSnapThreshold)
                    {
                        projObj.transform.position = projPos;
                        projObj.transform.eulerAngles = new Vector3(
                            proj.Pitch, proj.Heading, projObj.transform.eulerAngles.z);
                        if (projObj is WeaponBase wb)
                            wb._velocityInKnots = proj.Speed;
                    }
                    else if (projDrift > 2f)
                    {
                        projObj.transform.position = Vector3.Lerp(
                            projObj.transform.position, projPos, PvpProjPosLerp);
                        float heading = Mathf.LerpAngle(
                            projObj.transform.eulerAngles.y, proj.Heading, PvpProjHeadingLerp);
                        float pitch = Mathf.LerpAngle(
                            projObj.transform.eulerAngles.x, proj.Pitch, PvpProjHeadingLerp);
                        projObj.transform.eulerAngles = new Vector3(
                            pitch, heading, projObj.transform.eulerAngles.z);
                        if (projObj is WeaponBase wb)
                            wb._velocityInKnots = Mathf.Lerp(
                                wb._velocityInKnots, proj.Speed, PvpProjSpeedLerp);
                    }
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

                        var p = ProjectileIdMapper.FindByHostId(proj.EntityId);
                        if (p == null) continue;

                        _projectileTargets[proj.EntityId] = new ProjectileTarget
                        {
                            LocalId  = p.UniqueID,
                            Position = projWorldPos,
                            Heading  = proj.Heading,
                        };
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
            PvpShipDriftAvg = PvpShipDriftMax = 0f;
            PvpAirDriftAvg = PvpAirDriftMax = 0f;
        }
    }

    public static class OrderHandler
    {
        /// <summary>
        /// True while applying an order received from the network.
        /// Checked by Harmony Prefixes to avoid re-sending orders back.
        /// </summary>
        internal static bool ApplyingFromNetwork;

        private static readonly Dictionary<(int, Messages.OrderType), (float lastLogTime, int suppressedCount)> _logThrottle = new();
        private const float LogInterval = 10f;

        public static void ClearLogThrottle() => _logThrottle.Clear();

        public static void Apply(PlayerOrderMessage msg)
        {
            if (SessionManager.SceneLoading || SimSyncManager.CurrentState != SimState.Synchronized) return;

            var unit = StateSerializer.FindById(msg.SourceEntityId);
            if (unit == null)
            {
                Plugin.Log.LogWarning($"[Order] id={msg.SourceEntityId} not found (order={msg.Order})");
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
                Plugin.Log.LogInfo($"[Order] entity={msg.SourceEntityId} order={msg.Order} unit={unit.name}{suffix}");
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

                        // PvP: authorize enemy ship to spawn weapons from this order
                        if (Plugin.Instance.CfgPvP.Value
                            && unit._taskforce != Globals._playerTaskforce)
                        {
                            PvPFireAuth.Authorize(unit.UniqueID, msg.ShotsToFire);
                            Patch_ObjectBase_HandleEngageTasks.MarkNetworkOrdered(unit.UniqueID);
                            Plugin.Log.LogInfo($"[PvPAuth] AutoFire: authorized {msg.ShotsToFire} shots for unit {unit.UniqueID} ({unit.name}), total auth now={PvPFireAuth.ActiveAuthForUnit(unit.UniqueID)}");
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
                        if (wsStateAfter.Length > 0)
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
                        Plugin.Log.LogWarning("[DriftDetector] Client requested hard sync");
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
