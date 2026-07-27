using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Net2;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Client-side entity spawn/despawn executor for the v2 replication layer.
    ///
    /// Weapons are instantiated with ZERO dependence on the shooter's weapon
    /// containers (which may be empty/reloading locally - the root cause of v1's
    /// silent ForceSpawn failures): PoolManager clones the prefab from the ammo
    /// name, the DevModeUtils init recipe wires it up, the real
    /// CommonLaunchSettings registers it with taskforce/plotting (so local sensors
    /// and threat UI see it) and creates _proximityRadius (required by Destruction).
    /// The weapon stays fully "launched" - the map, radar and threat lists all
    /// skip un-launched weapons - but its brains are inert: the KinematicWeapon
    /// policy suppresses its OnFixedUpdate/OnUpdateEveryFrame (no seeker/guidance/
    /// fuse/motion). WeaponReplicaDriver moves it from the host stream.
    ///
    /// Sonobuoys (Bomb subtype) stay LIVE locally - their local sensing feeds the
    /// client's own sonar picture (contacts are per-machine by design).
    /// </summary>
    public static class SpawnReplicator
    {
        private static readonly Dictionary<string, AmmunitionParameters?> _ammoCache = new();

        // Tombstones: ids that died - late state/spawn packets for them are dropped
        private static readonly HashSet<int> _tombstones = new();
        private static readonly Queue<(int id, float realtime)> _tombstoneAge = new();
        private const float TombstoneRetainSec = 60f;

        public static bool IsTombstoned(int id) => _tombstones.Contains(id);

        public static void Tombstone(int id)
        {
            if (_tombstones.Add(id))
                _tombstoneAge.Enqueue((id, Time.realtimeSinceStartup));
            while (_tombstoneAge.Count > 0
                && Time.realtimeSinceStartup - _tombstoneAge.Peek().realtime > TombstoneRetainSec)
            {
                _tombstones.Remove(_tombstoneAge.Dequeue().id);
            }
        }

        public static void Reset()
        {
            _tombstones.Clear();
            _tombstoneAge.Clear();
            _spawnFailures.Clear();
        }

        /// <summary>Assign the host's id without polluting the client's UID counter
        /// (SetUniqueId bumps it when the assigned id is higher).</summary>
        private static void AssignHostId(ObjectBase obj, int hostId)
        {
            int savedUid = Singleton<SceneCreator>.Instance._UID;
            obj.SetUniqueId(hostId);
            Singleton<SceneCreator>.Instance._UID = savedUid;
        }

        // ── Spawn ─────────────────────────────────────────────────────────────

        public static void HandleSpawn(EntitySpawnMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            if (IsTombstoned(msg.EntityId)) { Telemetry.Count("v2.spawnTombstoned"); return; }

            var existing = ReplicaRegistry.Find(msg.EntityId);
            if (existing != null)
            {
                // A Unit spawn re-sent without the deck flag is the wheels-up flip
                // for an existing deck puppet (host's giveControl capture).
                if (msg.Kind == SpawnKind.Unit
                    && (msg.UnitFlags & EntitySpawnMessage.UnitFlagDeckPhase) == 0
                    && DeckPuppetDriver.IsDeckPuppet(msg.EntityId))
                {
                    DeckPuppetDriver.FlipToAirborne(existing, msg);
                    return;
                }
                Telemetry.Count("v2.spawnDuplicate");
                return;
            }

            try
            {
                switch (msg.Kind)
                {
                    case SpawnKind.Weapon: SpawnWeaponReplica(msg); break;
                    case SpawnKind.Unit:   SpawnUnitReplica(msg);   break;
                    case SpawnKind.Decoy:  SpawnDecoyReplica(msg);  break;
                }
                _spawnFailures.Remove(msg.EntityId);
            }
            catch (Exception ex)
            {
                Telemetry.Count("v2.spawnFailed");
                Plugin.Log.LogError($"[SpawnReplicator] Spawn failed id={msg.EntityId} kind={msg.Kind} " +
                    $"ammo={msg.AmmoName} ini={msg.UnitIniName} shooter={msg.ShooterId}: {ex}");

                // A spawn that throws (missing shooter, bad ammo ini, PlottingTable
                // NRE) fails identically on every census re-request: a PvP session
                // log showed five enemy sonobuoys retried every cycle for minutes -
                // 77 exceptions - until they expired host-side. A couple of retries
                // give a late-arriving dependency (the shooter's own spawn) a
                // chance; after that, tombstone so the census stops asking.
                _spawnFailures.TryGetValue(msg.EntityId, out int failures);
                _spawnFailures[msg.EntityId] = ++failures;
                if (failures >= MaxSpawnAttempts)
                {
                    _spawnFailures.Remove(msg.EntityId);
                    Tombstone(msg.EntityId);
                    Plugin.Log.LogWarning($"[SpawnReplicator] id={msg.EntityId} failed to spawn " +
                        $"{MaxSpawnAttempts} times - giving up (tombstoned)");
                }
            }
        }

        private static readonly Dictionary<int, int> _spawnFailures = new();
        private const int MaxSpawnAttempts = 3;

        /// <summary>Mirror a host aircraft/helicopter spawn (carrier launch, mission
        /// reinforcement) through the game's own creator, under the host's id.</summary>
        private static void SpawnUnitReplica(EntitySpawnMessage msg)
        {
            var homeBase = StateSerializer.FindById(msg.HomeBaseId);
            Taskforce? tf = homeBase?._taskforce ?? FindTaskforceBySide((Taskforce.TfType)msg.TaskforceSide);
            if (tf == null)
            {
                Telemetry.Count("v2.unitSpawnNoTaskforce");
                Plugin.Log.LogError($"[SpawnReplicator] Unit spawn {msg.EntityId}: no taskforce (side={msg.TaskforceSide})");
                return;
            }

            var geoCenter = Singleton<SceneCreator>.Instance.GeoCenterPosition;
            var spawnGeo  = new GeoPosition(msg.LatDeg, msg.LonDeg, msg.HeightM);
            var heading   = new Vector3(0f, GeoCodec.UnpackHeading(msg.HeadingQ), 0f);

            ObjectBase? result;
            using (Authority.Allowed())
            {
                if ((UnitType)msg.UnitKind == UnitType.Helicopter)
                {
                    result = Singleton<ObjectsManager>.Instance.createHelicopter(
                        msg.SquadronRef, msg.LoadoutVariant, homeBase, null, msg.UnitIniName,
                        msg.UnitNumber, geoCenter, spawnGeo, heading, tf,
                        "", true, false, msg.Nation, true);
                }
                else
                {
                    result = Singleton<ObjectsManager>.Instance.createAircraft(
                        msg.SquadronRef, msg.LoadoutVariant, homeBase, null, msg.UnitIniName,
                        msg.UnitNumber, geoCenter, spawnGeo, heading, tf,
                        true, true, "", true, false, msg.Nation, true);
                }
            }

            if (result == null)
            {
                Telemetry.Count("v2.unitSpawnFailed");
                Plugin.Log.LogError($"[SpawnReplicator] createAircraft/Helicopter returned null for {msg.UnitIniName}");
                return;
            }

            AssignHostId(result, msg.EntityId);

            ReplicaRegistry.Register(msg.EntityId, result, ReplicaPolicy.LocalMotionUnit);

            bool deckPhase = (msg.UnitFlags & EntitySpawnMessage.UnitFlagDeckPhase) != 0 && homeBase != null;
            if (deckPhase)
            {
                // Carrier deck launch: mirror the host's FlightDeck spawn state
                // (parented, flight sim off, colliders off) and hand the unit to
                // the deck-relative DeckState stream.
                result._correctAltitudeOnSpawn = false;
                DeckPuppetDriver.RegisterDeckSpawn(result, homeBase!);
            }
            else
            {
                // Airborne spawn (wheels-up or mission reinforcement): put the
                // replica in the same flight state the host's aircraft is in -
                // createAircraft with a homeBase parks it (setHomeBase: velocity 0,
                // no control), which left it dead in the air.
                float speedKts = GeoCodec.UnpackSpeedKts(msg.SpeedQ);
                if (result is Aircraft a)
                {
                    a.AircraftAnimation.setAnimsForFlight();
                    a.giveControl(speedKts);
                }
                else if (result is Helicopter h)
                {
                    h.GiveControl(speedKts);
                    h.setImmediateFlightConditions();
                }
            }

            Telemetry.Count("v2.spawnUnit");
            Plugin.Log.LogInfo($"[SpawnReplicator] Spawned {(UnitType)msg.UnitKind} replica id={msg.EntityId} " +
                $"ini={msg.UnitIniName} deck={deckPhase}");
        }

        /// <summary>Chaff clouds / noisemakers: cosmetic local instances that run
        /// their own bloom/drift/decay sim (LiveLocal) - host owns all guidance,
        /// so these only need to LOOK right and feed the local sensor picture.</summary>
        private static void SpawnDecoyReplica(EntitySpawnMessage msg)
        {
            var ap = GetAmmoParams(msg.AmmoName);
            if (ap == null) { Telemetry.Count("v2.spawnNoAmmoParams"); return; }

            var shooter = StateSerializer.FindById(msg.ShooterId);
            if (shooter == null)
            {
                // Both paths register through the shooter's taskforce - without it
                // CommonLaunchSettings NREs in the PlottingTable ctor. Cosmetic - skip.
                Telemetry.Count("v2.decoyNoShooter");
                return;
            }

            if (ap._type == Ammunition.Type.Noisemaker)
            {
                var go = Singleton<PoolManager>.Instance.getNoisemaker(ap._ammunitionFileName, null);
                var wb = go != null ? go.GetComponent<WeaponBase>() : null;
                if (wb == null) { Telemetry.Count("v2.spawnPoolFailed"); return; }
                wb.init(shooter, Vector3.zero, ap);
                var wi = wb.getWeaponInstance();
                if (wi != null) wb.setSensorData(wi._sensorData);

                wb.setName(ap._displayedName);
                wb.setObjectIniName(ap._ammunitionFileName);
                wb.inheritTaskforce(shooter);
                wb.setNation(shooter.Nation.Value);

                var spawnGeo = new GeoPosition(msg.LatDeg, msg.LonDeg, msg.HeightM);
                Vector3 pos = Utils.longLatToLocalV3(spawnGeo, Globals._currentCenterTile);
                wb.transform.position = pos;
                wb.transform.rotation = Quaternion.Euler(0f, GeoCodec.UnpackHeading(msg.HeadingQ), 0f);
                wb.gameObject.SetActive(true);

                using (Authority.Allowed())
                    wb.CommonLaunchSettings(null, pos, null, false);

                AssignHostId(wb, msg.EntityId);
                ReplicaRegistry.Register(msg.EntityId, wb, ReplicaPolicy.LiveLocal);
                ConsumeShooterStores(shooter, ap._ammunitionFileName);
                Telemetry.Count("v2.spawnDecoy");
            }
            else
            {
                // Chaff never runs CommonLaunchSettings in vanilla (launchChaff →
                // ChaffAttacher.launchChaffCloud → launchChaffEffect sets launch
                // state directly), and replaying it NREs: ChaffCloud has no
                // _weaponInstance. Launch through the shooter's own chaff attacher
                // instead - the exact path the host ran (the client's chaff system
                // OnUpdate is suppressed, so its pre-pooled clouds are only ever
                // consumed here, staying 1:1 with the host's launches).
                var sys = FindChaffSystem(shooter, msg.AmmoName);
                var attacher = sys != null && _chaffRef != null ? _chaffRef(sys) : null;
                var clouds = attacher != null && _chaffCloudsRef != null ? _chaffCloudsRef(attacher) : null;
                if (clouds == null || clouds.Count == 0) { Telemetry.Count("v2.decoyNoAttacher"); return; }

                var cloud = clouds[0];
                if (cloud.isLaunched()) { Telemetry.Count("v2.decoyAttacherStuck"); return; }

                using (Authority.Allowed())
                    attacher!.launchChaffCloud(); // launchChaffEffect + taskforce registration + SetActive

                // The host's launchChaff() decremented its loaded count before
                // launching the cloud; mirror that here or the client's chaff
                // readout never moves (WeaponSystemChaff.OnUpdate and its reload
                // bookkeeping are suppressed client-side, so this count is the
                // only state that matters).
                sys!.decreaseLoadedAmmoCount(sys._ammoInUse?._ap?._ammunitionFileName ?? msg.AmmoName);

                AssignHostId(cloud, msg.EntityId);
                ReplicaRegistry.Register(msg.EntityId, cloud, ReplicaPolicy.LiveLocal);
                Telemetry.Count("v2.spawnDecoy");
            }
        }

        private static readonly AccessTools.FieldRef<WeaponSystemChaff, ChaffAttacher>? _chaffRef =
            AccessTools.FieldRefAccess<WeaponSystemChaff, ChaffAttacher>("_chaff");
        private static readonly AccessTools.FieldRef<ChaffAttacher, List<ChaffCloud>>? _chaffCloudsRef =
            AccessTools.FieldRefAccess<ChaffAttacher, List<ChaffCloud>>("_chaffClouds");

        private static WeaponSystemChaff? FindChaffSystem(ObjectBase shooter, string ammoName)
        {
            if (shooter._obp?._weaponSystems == null) return null;
            WeaponSystemChaff? fallback = null;
            foreach (var ws in shooter._obp._weaponSystems)
            {
                if (ws is WeaponSystemChaff c)
                {
                    if (c._ammoInUse?._ap?._ammunitionFileName == ammoName) return c;
                    fallback ??= c;
                }
            }
            return fallback;
        }

        private static Taskforce? FindTaskforceBySide(Taskforce.TfType side)
        {
            if (!Singleton<TaskforceManager>.InstanceExists(false)) return null;
            foreach (var tf in Singleton<TaskforceManager>.Instance._taskForces)
            {
                if (tf != null && tf.Side == side) return tf;
            }
            return null;
        }

        private static void SpawnWeaponReplica(EntitySpawnMessage msg)
        {
            var ap = GetAmmoParams(msg.AmmoName);
            if (ap == null)
            {
                Telemetry.Count("v2.spawnNoAmmoParams");
                Plugin.Log.LogError($"[SpawnReplicator] No ammo params for '{msg.AmmoName}'");
                return;
            }

            var go = Singleton<PoolManager>.Instance.getWeapon(ap._ammunitionFileName, ap._type, null, true);
            if (go == null)
            {
                Telemetry.Count("v2.spawnPoolFailed");
                Plugin.Log.LogError($"[SpawnReplicator] PoolManager could not instantiate '{msg.AmmoName}'");
                return;
            }

            var shooter = StateSerializer.FindById(msg.ShooterId);
            var wb = go.GetComponent<WeaponBase>();

            // DevModeUtils.createAmmunitionObjectInstance recipe
            wb.init(shooter, Vector3.zero, ap);
            wb.setSensorData(wb.getWeaponInstance()._sensorData);
            wb.setName(ap._displayedName);
            wb.setObjectIniName(ap._ammunitionFileName);
            if (shooter != null)
            {
                wb.setNation(shooter.Nation.Value);
                wb._taskforce = shooter._taskforce;
            }

            // Place at the streamed spawn point before launch settings run
            var spawnGeo = new GeoPosition(msg.LatDeg, msg.LonDeg, msg.HeightM);
            go.transform.position = Utils.longLatToLocalV3(spawnGeo, Globals._currentCenterTile);
            go.transform.rotation = Quaternion.Euler(
                GeoCodec.UnpackAngleCdeg(msg.PitchQ), GeoCodec.UnpackHeading(msg.HeadingQ), 0f);
            go.SetActive(true);

            // Resolve target - null for LandUnit targets (CommonLaunchSettings would
            // deref weaponSystem._currentTargetPoint, and we pass weaponSystem=null)
            var target = StateSerializer.FindById(msg.TargetId);
            if (target is LandUnit) target = null;

            var aimGeo = new GeoPosition(msg.AimLatDeg, msg.AimLonDeg, msg.AimHeightM);
            Vector3 aimUnity = Utils.longLatToLocalV3(aimGeo, Globals._currentCenterTile);

            bool isSub = (msg.Flags & EntitySpawnMessage.FlagSubmunition) != 0;
            bool liveLocal = ap._subType == Ammunition.Type.Sonobuoy && wb is Bomb;
            using (Authority.Allowed())
            {
                if (liveLocal)
                {
                    // LiveLocal sonobuoy: the local Bomb sim must fly it, and that
                    // sim only moves in FlightStage.DropFromAircraft - set by the
                    // real drop initializer Container_Launch (which seeds the fall
                    // velocity and deploy-depth state, then calls
                    // CommonLaunchSettings itself).
                    wb._moveOfLaunchPlatform = shooter != null ? shooter._velocityVecInUnity : Vector3.zero;
                    wb.Container_Launch(target, aimUnity, Vector3.zero, null);
                }
                else
                {
                    // Real initializer: _launchTime, parent detach, _proximityRadius,
                    // taskforce/plotting registration, target._incomingWeapons (threat UI)
                    wb.CommonLaunchSettings(target, aimUnity, null, isSub);
                }
            }

            AssignHostId(wb, msg.EntityId);

            if (liveLocal)
            {
                // Sonobuoy: full local sim (local sonar detection wanted)
                ReplicaRegistry.Register(msg.EntityId, wb, ReplicaPolicy.LiveLocal);
                Telemetry.Count("v2.spawnSonobuoy");
            }
            else
            {
                // Stays "launched" (map/radar/threat visibility) but inert: the
                // KinematicWeapon policy suppresses its per-frame brains; the
                // driver moves it and pumps its effects.
                wb._ignoreCollisions = true;

                ReplicaRegistry.Register(msg.EntityId, wb, ReplicaPolicy.KinematicWeapon);
                WeaponReplicaDriver.OnReplicaSpawned(wb, msg);
                Telemetry.Count("v2.spawnWeapon");
            }

            // The replica above is a fresh pool instance by design - nothing has
            // touched the shooter's own stores, so its pylon round and ammo count
            // are still those from the join save. Consume one round to match what
            // the host's launch() just did. Submunitions excluded: their "shooter"
            // is the platform, but no store was expended for them.
            if (!isSub)
                ConsumeShooterStores(shooter, ap._ammunitionFileName);

            if (Plugin.Instance.CfgVerboseDebug.Value)
                Plugin.Log.LogDebug($"[SpawnReplicator] Spawned replica id={msg.EntityId} " +
                    $"ammo={msg.AmmoName} shooter={msg.ShooterId} target={msg.TargetId} live={liveLocal}");
        }

        /// <summary>
        /// Mirror WeaponSystemHardpoint.launch()'s bookkeeping on the client: the
        /// host launch removed the mounted WeaponBase from its pylon and decremented
        /// the loaded-ammo count, but the client shooter is a replica whose weapon
        /// systems never fire locally - without this its loadout display and pylon
        /// models never change. For systems with no mounted instance (ship
        /// launchers, internal bays) only the count is adjusted; their visuals are
        /// hatch/reload animations the game drives separately.
        /// </summary>
        private static void ConsumeShooterStores(ObjectBase? shooter, string ammoName)
        {
            if (shooter == null || string.IsNullOrEmpty(ammoName))
            {
                Telemetry.Count("v2.storesNoShooter");
                return;
            }

            // NOTE: with copyList:false this returns NULL on a miss - TryGetValue
            // leaves the out-param null and the copy branch is skipped - so this
            // used to be a silent give-up that left the client's count untouched.
            var systems = shooter.GetWeaponSystemsForAmmunition(ammoName, copyList: false);
            if (systems == null || systems.Count == 0)
            {
                Telemetry.Count("v2.storesNoSystem");
                return;
            }

            // Prefer the system that visibly carries the round on a pylon.
            foreach (var ws in systems)
            {
                if (ws is not WeaponSystemHardpoint hp) continue;
                for (int i = 0; i < hp._weapons.Count; i++)
                {
                    var mounted = hp._weapons[i];
                    if (mounted == null || mounted.isLaunched()) continue;
                    if (mounted._ap?._ammunitionFileName != ammoName) continue;

                    hp._weapons.RemoveAt(i);
                    mounted.gameObject.SetActive(false);
                    if (hp._weapons.Count == 0) hp._isEmpty = true;
                    hp.decreaseLoadedAmmoCount(ammoName);
                    return;
                }
            }

            foreach (var ws in systems)
            {
                if (ws.getLoadedAmmoCount(ammoName) > 0)
                {
                    ws.decreaseLoadedAmmoCount(ammoName);
                    return;
                }
            }

            // Nothing here reports a loaded round of this ammo. The displayed count
            // is unaffected - that arrives from the host as an absolute total - so
            // this only means the local pylon/loaded bookkeeping could not be
            // attributed. Named candidates, because which system SHOULD have held it
            // is what distinguishes a client-side divergence from an ammo this
            // platform never tracks as "loaded".
            Telemetry.Count("v2.storesConsumeMissed");
            if (!Plugin.Instance.CfgVerboseDebug.Value) return;
            var seen = new System.Text.StringBuilder();
            foreach (var ws in systems)
            {
                if (seen.Length > 0) seen.Append(", ");
                seen.Append($"{ws._systemName}/{ws.GetType().Name} loaded={ws.getLoadedAmmoCount(ammoName)}");
            }
            Plugin.Log.LogDebug($"[Stores] {shooter.name}: no system holds a loaded '{ammoName}' ({seen})");
        }

        private static AmmunitionParameters? GetAmmoParams(string ammoName)
        {
            if (_ammoCache.TryGetValue(ammoName, out var cached)) return cached;
            AmmunitionParameters? ap = null;
            try { ap = new AmmunitionParameters(ammoName, 0, null); }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[SpawnReplicator] AmmunitionParameters('{ammoName}') failed: {ex.Message}");
            }
            _ammoCache[ammoName] = ap;
            return ap;
        }

        // ── Despawn / impact ─────────────────────────────────────────────────

        public static void HandleImpact(ImpactEventMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            var geo = new GeoPosition(msg.LatDeg, msg.LonDeg, msg.HeightM);
            Vector3 pos = Utils.longLatToLocalV3(geo, Globals._currentCenterTile);
            var rot = Quaternion.Euler(GeoCodec.UnpackAngleCdeg(msg.PitchQ), GeoCodec.UnpackHeading(msg.HeadingQ), 0f);

            var obj = ReplicaRegistry.Find(msg.WeaponId);
            if (obj is WeaponBase wb && !wb.IsDestroyed)
            {
                wb.transform.position = pos;
                wb.transform.rotation = rot;
                var hitUnit = StateSerializer.FindById(msg.HitUnitId);
                using (Authority.Allowed())
                {
                    // Game's own context-correct destruction VFX; createBlastzone=false
                    // → zero damage (DamageState carries that authoritatively)
                    wb.Destruction(pos, rot, hitUnit, false);
                }
                WeaponReplicaDriver.Forget(msg.WeaponId);
                ReplicaRegistry.Unregister(msg.WeaponId);
            }
            else
            {
                // Replica never existed here (spawn raced/dropped) - still show the bang
                Telemetry.Count("v2.impactUnknownWeapon");
            }
            Tombstone(msg.WeaponId);
        }

        public static void HandleDespawn(EntityDespawnMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            var obj = ReplicaRegistry.Find(msg.EntityId) ?? StateSerializer.FindById(msg.EntityId);
            if (obj is WeaponBase wb)
            {
                using (Authority.Allowed())
                {
                    if (!wb.IsDestroyed
                        && (msg.Cause == DespawnCause.Intercepted || msg.Cause == DespawnCause.FuelExpired
                            || msg.Cause == DespawnCause.Splashed))
                    {
                        var geo = new GeoPosition(msg.LatDeg, msg.LonDeg, msg.HeightM);
                        Vector3 pos = Utils.longLatToLocalV3(geo, Globals._currentCenterTile);
                        wb.Destruction(pos, wb.transform.rotation, null, false);
                    }
                    wb.destroyObject(false, false, TacView.TCEvent.Destroyed);
                }
            }
            else if (obj is Aircraft || obj is Helicopter)
            {
                // Landed/stored/scripted removal - quiet local removal, no VFX
                using (Authority.Allowed())
                    obj.destroyObject(false, false, TacView.TCEvent.Destroyed);
                AircraftReplicaDriver.Forget(msg.EntityId);
            }
            WeaponReplicaDriver.Forget(msg.EntityId);
            DeckPuppetDriver.Forget(msg.EntityId);
            ReplicaRegistry.Unregister(msg.EntityId);
            Tombstone(msg.EntityId);
        }

        public static void HandleDestroyEvent(DestroyEventMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            var unit = ReplicaRegistry.Find(msg.UnitId) ?? StateSerializer.FindById(msg.UnitId);
            if (unit == null || unit is WeaponBase) return;

            if (msg.Mode == DestroyEventMessage.ModeStartSinking)
            {
                var comps = unit.Compartments;
                if (comps != null && !comps._isSinking)
                    comps.Sink(Compartments.SinkFocus.All, false);
            }
            else if (!unit.IsDestroyed)
            {
                CombatEventHandler.DestroyFromNetwork(unit);
            }
        }

        // ── Resync demotion pass ─────────────────────────────────────────────
        // Save files CONTAIN in-flight weapons and SceneCreator.LaunchWeapons
        // re-launches them fully LIVE on load (verified: full Container_Launch).
        // After a session sync the client must demote them all to replicas or it
        // runs autonomous weapons with live seekers/fuses - double damage.
        public static void DemoteLoadedWeapons()
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            int demoted = 0;

            demoted += DemoteList(UnitRegistry.Missiles);
            demoted += DemoteList(UnitRegistry.Torpedoes);
            demoted += DemoteList(UnitRegistry.Bombs);

            if (demoted > 0)
                Plugin.Log.LogInfo($"[SpawnReplicator] Demoted {demoted} save-restored weapons to replicas");
        }

        private static int DemoteList<T>(IReadOnlyList<T> list) where T : WeaponBase
        {
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var wb = list[i];
                if (wb == null || wb.IsDestroyed) continue;
                // Mounted/racked weapons are NOT in flight: they're transform
                // children of their platform and must stay native - demoting them
                // made the stream drag them around in world space (weapons visibly
                // detached from their aircraft) and the census purge ate them.
                if (!wb.isLaunched()) continue;
                // Sonobuoys stay LiveLocal (local sensing feeds the client's picture)
                if (wb is Bomb && wb._ap?._subType == Ammunition.Type.Sonobuoy) continue;
                // Registering as KinematicWeapon is the demotion: the policy
                // suppresses the weapon's per-frame brains (it stays "launched"
                // so map/radar/threat visibility is preserved).
                wb._ignoreCollisions = true;
                ReplicaRegistry.Register(wb.UniqueID, wb, ReplicaPolicy.KinematicWeapon);
                WeaponReplicaDriver.OnReplicaDemoted(wb);
                n++;
            }
            return n;
        }
    }
}
