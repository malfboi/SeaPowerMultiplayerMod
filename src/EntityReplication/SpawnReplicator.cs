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

        /// <summary>Assign the host's id.
        ///
        /// This used to save and restore SceneCreator._UID around the call, to keep the
        /// client's counter from being dragged up by a host id. That kept the counter
        /// monotonic but disabled the game's ONLY defence against exactly the collision
        /// it was worried about: SetUniqueId advances _UID past any id it adopts
        /// (ObjectBase.cs:1638-1645), which is what stops a later local allocation
        /// landing on an id already in use. Putting it back meant the guest went on
        /// handing its own chaff and decoys numbers the host was replicating into.
        ///
        /// The restore is gone and the defence is left alone. It costs nothing in the
        /// normal case - GuestIdFloor holds the counter above ClientUidBase, so an
        /// incoming host id is far below it and SetUniqueId's comparison never fires -
        /// and it is the backstop for any path where the floor is not armed.</summary>
        private static void AssignHostId(ObjectBase obj, int hostId) => obj.SetUniqueId(hostId);

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
                    && (msg.UnitFlags & EntitySpawnMessage.UnitFlagDeckPhase) == 0)
                {
                    if (DeckPuppetDriver.IsDeckPuppet(msg.EntityId))
                        DeckPuppetDriver.FlipToAirborne(existing, msg);

                    // The wheels-up re-send is the only message carrying the launch's
                    // callsign and formation. Apply it after the flip (a formation
                    // swaps the motion controller, which a parented deck puppet must
                    // not have) and also when the puppet was already flipped from a
                    // stream sample, where the branch above no-ops.
                    UnitIdentityApplier.Apply(existing, msg);
                    return;
                }

                // Still flagged deck-phase, and now naming a leader: the host's
                // formation capture re-sending the launch's flight the moment
                // launchVehicle forms it up. Everything about the puppet stays as it
                // is - this only carries identity - so it must not reach the flip
                // above, which is why it is tested after it and not folded in.
                //
                // Safe on a parented puppet: joining a formation only matters to an
                // aircraft once InAirFormation() says so, and that returns false while
                // _hasControl is false, which is exactly the deck phase. The station
                // keeper switches on with the airborne flip, by which time
                // Patch_FormationFlightPhysics_OnFixedUpdate governs it.
                if (msg.Kind == SpawnKind.Unit && msg.FormationLeaderId != 0)
                {
                    UnitIdentityApplier.Apply(existing, msg);
                    Telemetry.Count("v2.spawnDeckFormation");
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
            Taskforce? tf = homeBase?._taskforce ?? ResolveTaskforce(msg.TaskforceSide);
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

                // Airborne already: carries the host's callsign and formation (census
                // replays the ledger entry the wheels-up capture updated). Deck-phase
                // spawns get theirs on the wheels-up re-send instead.
                UnitIdentityApplier.Apply(result, msg);
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

        /// <summary>Take the round that just launched off its rail.
        ///
        /// THE ASYMMETRY THIS EXISTS FOR: on the host, the object sitting on the rail IS
        /// the missile - the launch flies the very object spawnWeapons() put there, so
        /// the rail empties by itself. On a client the missile arrives as a separate
        /// replica, so a rail loaded by WeaponHatchHandler would still be holding its
        /// round while the replica flies away. Two missiles, one shot.
        ///
        /// There is no host event to key off: WeaponContainer.unload appears only in
        /// destroyWeapons() and RefillAmmo, never on the launch path. So the pairing is
        /// inferred from the spawn itself - the round is leaving a launcher on this
        /// shooter that is holding this ammunition, and ONE container is emptied, the
        /// first that matches.
        ///
        /// Conservative on purpose. A launcher that has no matching loaded round is left
        /// alone, so a VLS or any other system this does not model cannot be disturbed:
        /// the failure mode is a rail that stays full for a moment longer, not one that
        /// empties when it should not.</summary>
        private static void ClearRailFor(ObjectBase shooter, string ammoName)
        {
            var systems = shooter._obp?._weaponSystems;
            if (systems == null || string.IsNullOrEmpty(ammoName)) return;

            for (int s = 0; s < systems.Count; s++)
            {
                if (!(systems[s] is WeaponSystemLauncher launcher)) continue;
                var containers = launcher._containers;
                if (containers == null) continue;

                for (int c = 0; c < containers.Count; c++)
                {
                    var container = containers[c];
                    if (container?._loadedAmmunition?._ap?._ammunitionFileName != ammoName) continue;

                    container.unload(returnAmmo: false);
                    return; // one round, one rail
                }
            }
        }

        /// <summary>Which local taskforce a replicated spawn belongs to, given the side
        /// byte the HOST stamped on it.
        ///
        /// TWO BUGS LIVED IN THE OLD ONE-LINE VERSION, which matched the enum by name
        /// against TaskforceManager's list.
        ///
        /// (1) The byte is the side as the HOST sees it, and PvP hands the guest a save
        /// with PlayerTaskforce and EnemyTaskforce swapped
        /// (SessionManager.SwapTaskforceSides). Matching Player to Player therefore gave
        /// a host-owned aircraft to the guest's OWN taskforce: it rendered friendly,
        /// could not be controlled (it is still a replica), and every missile it fired
        /// inherited the same wrong side, because SpawnWeaponReplica copies
        /// objectBase._taskforce straight across. Playtest 28 reported both halves. The
        /// fallback is reached whenever HomeBaseId is 0 or its object has no local
        /// replica yet, so it is not a rare path.
        ///
        /// (2) TaskforceManager reparents itself to Globals._missionSingletonsParent in
        /// Awake exactly as ObjectsManager does, but MissionManager's unload hand-destroys
        /// only ObjectsManager - so Singleton's static _instance survives the mission,
        /// _taskForces has no Clear anywhere in the assembly, and SceneCreator APPENDS
        /// the next mission's taskforces to it. The first match for a side, after a
        /// second battle in one lobby, is a DEAD taskforce from the first one.
        ///
        /// Resolving against Globals closes both: those three fields are rebuilt per
        /// mission, so they cannot be stale, and the PvP inversion is applied explicitly
        /// instead of being implied by a name. Done here rather than by sending
        /// "mine/theirs" on the wire because the receiver is the only side that knows
        /// whether its own save was swapped - and it costs no protocol change.</summary>
        private static Taskforce? ResolveTaskforce(byte hostSide)
        {
            var side = (Taskforce.TfType)hostSide;

            // Only the guest's save is rewritten, and only in PvP. The host's own view
            // is never swapped, so its side byte means exactly what it says there.
            bool swapped = Plugin.Instance.CfgPvP.Value && !Plugin.Instance.CfgIsHost.Value;

            switch (side)
            {
                case Taskforce.TfType.Player:
                    return swapped ? Globals._enemyTaskforce : Globals._playerTaskforce;

                case Taskforce.TfType.Enemy:
                    return swapped ? Globals._playerTaskforce : Globals._enemyTaskforce;

                case Taskforce.TfType.Neutral:
                    return Globals._neutralTaskforce;

                default:
                    // Ally (and None) have no Globals anchor, so these still take the
                    // scan and still carry bug (2)'s stale risk. Left rather than
                    // guessed at: a mission with a separate Ally taskforce spawning
                    // replicated units through this fallback is the only way to reach
                    // it, and inventing an anchor for it would be worse than the scan.
                    return ScanTaskforcesForSide(side);
            }
        }

        /// <summary>Last resort - see ResolveTaskforce's default branch for why this is
        /// still here and what it can get wrong.</summary>
        private static Taskforce? ScanTaskforcesForSide(Taskforce.TfType side)
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
                ClearRailFor(shooter, ap._ammunitionFileName);
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

            if (Plugin.Instance.VerboseEffective)
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
            if (!Plugin.Instance.VerboseEffective) return;
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
            if (MotionTrace.IsTracing(msg.WeaponId))
                MotionTrace.TerminalEvent("IMPACT", msg.WeaponId, obj, pos,
                    $"hitUnit={msg.HitUnitId}");

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

            var replica = ReplicaRegistry.Find(msg.EntityId);
            var obj = replica ?? StateSerializer.FindById(msg.EntityId);
            if (MotionTrace.IsTracing(msg.EntityId))
                MotionTrace.TerminalEvent("DESPAWN", msg.EntityId, obj,
                    Utils.longLatToLocalV3(new GeoPosition(msg.LatDeg, msg.LonDeg, msg.HeightM),
                        Globals._currentCenterTile),
                    $"cause={msg.Cause}");

            // A weapon that is not one of OUR replicas and was never launched here is
            // the pooled instance the id merely resolved to, not the thing the host is
            // despawning. Chaff clouds are pre-pooled per weapon system and the replica
            // launch path can decline to launch one (decoyNoAttacher / decoyAttacherStuck),
            // so an ordinary chaff expiry could land on an untouched pool object -
            // and ChaffCloud.destroyObject dereferences a _collider that only the launch
            // path ever creates. The NRE escaped the whole main-thread drain.
            // A weapon that never ran a local launch has no _weaponInstance either, and
            // the teardown overrides dereference it as bare as ChaffCloud does its
            // collider - Torpedo.destroyObject goes straight to
            // _weaponInstance._propAudioSource. Same class of object, one weapon type
            // further along, and the same answer: it is not ours to tear down.
            if (obj is WeaponBase unlaunched && replica == null
                && (!unlaunched.isLaunched() || unlaunched._weaponInstance == null))
            {
                Telemetry.Count("v2.despawnUnlaunched");
            }
            else if (obj is WeaponBase wb)
            {
                // Whatever the teardown does, the bookkeeping below has to run: a throw
                // here used to leave the replica registered under an id the host has
                // already retired, so every later message for it resolved onto a corpse.
                try
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
                catch (System.Exception ex)
                {
                    Telemetry.Count("v2.despawnTeardownThrew");
                    Plugin.Log.LogWarning($"[V2] Despawn teardown threw for id={msg.EntityId} " +
                                          $"({wb.GetType().Name}): {ex.Message}");
                }
            }
            else if (obj is Aircraft || obj is Helicopter)
            {
                // Landed/stored/scripted removal - quiet local removal, no VFX
                using (Authority.Allowed())
                    obj.destroyObject(false, false, TacView.TCEvent.Destroyed);
                AircraftReplicaDriver.Forget(msg.EntityId);
            }
            EvictFromFormations(obj);
            WeaponReplicaDriver.Forget(msg.EntityId);
            DeckPuppetDriver.Forget(msg.EntityId);
            ReplicaRegistry.Unregister(msg.EntityId);
            Tombstone(msg.EntityId);
        }

        /// <summary>Make sure no local formation is left seating a unit this despawn
        /// retired. Only the two teardown branches above run destroyObject, whose first
        /// act is the formation detach - every other path (a Vessel/Submarine/LandUnit
        /// despawn, an unlaunched pooled weapon, or a lookup that missed entirely) fell
        /// straight through to the bookkeeping and left the station seated.
        ///
        /// A stranded station is not inert. UnitFormation.get_InFormationSummary is a
        /// Noesis binding, and it filters on <c>UnitObject != null</c> - which is Unity's
        /// operator, so it screens out a DESTROYED object but not a live pooled one that
        /// has been recycled out from under the station. It then dereferences
        /// <c>UnitObject._obp._typeAbbr</c>, and a pooled object carries no _obp. That
        /// throws on EVERY UI frame from then on, which is the engine's "repeated
        /// exceptions - degraded performance" warning after aircraft recover to a deck.
        ///
        /// Two passes because the id is not always enough. When the despawn resolved to
        /// its own object, DetachUnit is the complete operation and it runs. When it
        /// resolved to nothing (tombstoned, or an id that landed on the wrong object)
        /// there is no handle to detach, so the corpse has to be recognised by the same
        /// thing that trips the binding - a seated unit with no _obp. That scan is
        /// bounded by formations x stations and only runs on a despawn, not per frame.
        ///
        /// The corpse is evicted by emptying its seat rather than through DetachUnit,
        /// which would dereference the dead object again on the way out
        /// (CleanUpStation reads obj._taskforce before it clears the seat, and calls
        /// RemoveWaypoints after) - trading a repeating exception for a one-shot inside
        /// the message drain is not a fix. An EMPTY station is a state the engine
        /// already handles everywhere: AddUnit reuses it, InFormationSummary filters it,
        /// OnUpdate's station-keeping skips it, and UnitOnStationNotValid reports it, so
        /// a corpse that was the LEADER is handed over by the formation's own next
        /// update rather than needing anything here.</summary>
        private static void EvictFromFormations(ObjectBase? obj)
        {
            if (obj != null)
            {
                obj.Formation?.DetachUnit(obj);
                return;
            }

            ScanForCorpses(Globals._playerTaskforce);
            ScanForCorpses(Globals._enemyTaskforce);
            ScanForCorpses(Globals._neutralTaskforce);
        }

        private static void ScanForCorpses(Taskforce? tf)
        {
            var formations = tf?.Formations;
            if (formations == null) return;

            for (int f = formations.Count - 1; f >= 0; f--)
                EvictCorpses(formations[f]);
        }

        /// <summary>The per-formation half of the scan above.</summary>
        internal static void EvictCorpses(UnitFormation? formation)
        {
            var stations = formation?.Stations;
            if (stations == null) return;

            for (int s = stations.Count - 1; s >= 0; s--)
            {
                var station = stations[s];
                // Unity's operator, so a DESTROYED object reads as null here and is
                // already screened out by the binding - the one that gets through is
                // a live pooled object recycled out from under the station.
                if (station?.UnitObject == null || station.UnitObject._obp != null) continue;

                Plugin.Log.LogWarning($"[V2] Formation '{formation!.Name}' was seating a " +
                    "recycled object with no parameters - emptying the station.");
                station.UnitObject = null;
            }
        }

        /// <summary>Clear a formation's corpses and report whether it can safely take an
        /// AddUnit right now. Call before every replicated join.
        ///
        /// AddUnit mutates Stations, and TrulyObservableCollection answers that with
        /// Stations_CollectionChanged, which re-reads InFormationSummary through Noesis
        /// AND calls CalculateAirUnitNames. Both walk the station list dereferencing
        /// <c>UnitObject._obp</c> with no guard of their own (UnitFormation.cs:169
        /// and :311), so one recycled corpse still seated anywhere in the formation
        /// takes the join down - and with it the rest of whatever order or spawn was
        /// being applied. That is the second of playtest 28's two client NRE stacks,
        /// reported from both the order path and the spawn path, ~12 a battle.
        ///
        /// The leader test is the reason this returns a bool rather than just sweeping.
        /// Emptying a corpse's seat can leave the LEADER's seat empty, and
        /// CalculateAirUnitNames opens by dereferencing
        /// <c>LeaderStation.UnitObject._obp._objectName</c> - so joining straight after
        /// a sweep that unseated the leader trades one NRE for another. The formation
        /// hands the lead over itself on its next update (UnitOnStationNotValid →
        /// AssignNewLeaderFromAvailableUnits); waiting a frame is free, because both
        /// callers already retry.</summary>
        internal static bool PrepareForJoin(UnitFormation? formation)
        {
            if (formation == null) return false;
            EvictCorpses(formation);

            var leader = formation.LeaderStation?.UnitObject;
            return leader != null && leader._obp != null;
        }

        public static void HandleDestroyEvent(DestroyEventMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            var unit = ReplicaRegistry.Find(msg.UnitId) ?? StateSerializer.FindById(msg.UnitId);
            if (MotionTrace.IsTracing(msg.UnitId))
                MotionTrace.TerminalEvent("DESTROY", msg.UnitId, unit,
                    unit != null ? unit.transform.position : Vector3.zero,
                    $"mode={msg.Mode} killerWeapon={msg.KillerWeaponId} killerUnit={msg.KillerUnitId}");
            if (unit == null || unit is WeaponBase) return;

            if (msg.Mode == DestroyEventMessage.ModeStartSinking)
            {
                var comps = unit.Compartments;
                if (comps != null && !comps._isSinking)
                    using (Authority.Allowed())
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
