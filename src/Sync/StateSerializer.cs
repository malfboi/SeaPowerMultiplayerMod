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
        // Compiled delegates for hot-path reflected fields (avoids FieldInfo.GetValue per-unit)
        private static readonly Func<Vessel, float> _getRudderAngle;
        private static readonly Func<Submarine, float> _getSubRudderAngle;
        private static readonly Action<Vessel, float> _setVesselRudderAngle;
        private static readonly Action<Submarine, float> _setSubRudderAngle;
        private static readonly Func<WeaponBase, ObjectBase> _getLaunchPlatform;

        /// <summary>Compile a get/set pair for a float field. Vessel and Submarine each
        /// declare their OWN _setRudderAngle (Submarine is not a Vessel), and
        /// ObjectBase.setRudderAngle is a no-op that only Vessel overrides - so the
        /// manual-rudder order has to reach the field directly, per concrete type.</summary>
        private static void CompileFloatField<T>(string fieldName,
            out Func<T, float> getter, out Action<T, float> setter)
        {
            var field = AccessTools.Field(typeof(T), fieldName);
            if (field == null)
            {
                getter = _ => 0f;
                setter = (_, __) => { };
                return;
            }
            var obj = Expression.Parameter(typeof(T));
            var val = Expression.Parameter(typeof(float));
            var access = Expression.Field(obj, field);
            getter = Expression.Lambda<Func<T, float>>(access, obj).Compile();
            setter = Expression.Lambda<Action<T, float>>(Expression.Assign(access, val), obj, val).Compile();
        }

        static StateSerializer()
        {
            CompileFloatField<Vessel>("_setRudderAngle", out _getRudderAngle, out _setVesselRudderAngle);
            CompileFloatField<Submarine>("_setRudderAngle", out _getSubRudderAngle, out _setSubRudderAngle);

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

        /// <summary>Public accessor for the compiled _setRudderAngle reader (v2 streamer).</summary>
        public static float GetRudderAngle(Vessel v) => _getRudderAngle(v);

        /// <summary>Commanded rudder angle of any steerable unit (0 if it has none).</summary>
        public static float GetRudderAngle(ObjectBase unit) => unit switch
        {
            Vessel v    => _getRudderAngle(v),
            Submarine s => _getSubRudderAngle(s),
            _           => 0f,
        };

        /// <summary>Apply a commanded rudder angle (manual A/D steering).</summary>
        public static void SetRudderAngle(ObjectBase unit, float angle)
        {
            switch (unit)
            {
                case Vessel v:    _setVesselRudderAngle(v, angle); break;
                case Submarine s: _setSubRudderAngle(s, angle);    break;
            }
        }

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

        // ── Drift stats (published by UnitReplicaDriver, read by UI) ─────────
        public static float ShipDriftAvg { get; private set; }
        public static float ShipDriftMax { get; private set; }
        public static float AirDriftAvg { get; private set; }
        public static float AirDriftMax { get; private set; }

        internal static void ReportDrift(float shipAvg, float shipMax, float airAvg, float airMax)
        {
            ShipDriftAvg = shipAvg;
            ShipDriftMax = shipMax;
            AirDriftAvg  = airAvg;
            AirDriftMax  = airMax;
        }

        internal static void RunAlignmentFromUnitStates(List<UnitState> units)
        {
            int savedUid = Singleton<SceneCreator>.Instance._UID;
            var reassignments = new List<(ObjectBase obj, int hostId)>();

            // PvP: the state update contains the remote player's units. After PvP
            // taskforce swap, those units are in our EnemyTaskforce locally. Only
            // match against enemy units to prevent accidentally remapping our own
            // aircraft to remote IDs (which would make our aircraft invisible to
            // the remote side).
            bool isPvP = Plugin.Instance.CfgPvP.Value;
            Taskforce alignFilter = isPvP ? GetEnemyTaskforce() : null;

            foreach (var state in units)
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

                var best = FindLocalByPosition(worldPos, state.Kind, alignFilter);
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

        /// <summary>
        /// Returns the enemy taskforce (the remote player's taskforce).
        /// In PvP, the state update contains the remote player's units,
        /// which are in our local EnemyTaskforce after the PvP swap.
        /// </summary>
        private static Taskforce GetEnemyTaskforce()
        {
            var playerTf = Globals._playerTaskforce;
            if (playerTf == null || !Singleton<TaskforceManager>.InstanceExists(false))
                return null;

            foreach (var tf in Singleton<TaskforceManager>.Instance._taskForces)
            {
                if (tf != playerTf)
                    return tf;
            }
            return null;
        }

        private static ObjectBase FindLocalByPosition(Vector3 worldPos, UnitType kind,
            Taskforce taskforceFilter = null)
        {
            ObjectBase best = null;
            float bestDist = float.MaxValue;
            var all = UnitRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var obj = all[i];
                if (obj == null || obj.IsDestroyed) continue;
                if (!KindMatches(obj, kind)) continue;
                if (taskforceFilter != null && obj._taskforce != taskforceFilter) continue;
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

        /// <summary>Reset drift stats (call on disconnect/scene change).</summary>
        public static void ResetOrphanTracking()
        {
            ShipDriftAvg = ShipDriftMax = 0f;
            AirDriftAvg  = AirDriftMax  = 0f;
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
                        // setTelegraph is virtual - dispatches to the Submarine/
                        // Aircraft overrides too. (The old `is Vessel` cast silently
                        // dropped every submarine speed order.)
                        unit.setTelegraph((int)msg.Speed);
                        break;

                    case Messages.OrderType.LaunchChaff:
                        unit.LaunchChaff(false);
                        break;

                    case Messages.OrderType.LaunchNoisemaker:
                        // launchNoisemaker is declared on Submarine/Vessel (not
                        // ObjectBase); it queues a noisemaker EngageTask the host
                        // fires natively, replicating the decoy back to the client.
                        if (unit is Submarine subNm) subNm.launchNoisemaker();
                        else if (unit is Vessel vesNm) vesNm.launchNoisemaker();
                        break;

                    case Messages.OrderType.ManualGunFire:
                    {
                        // v2: client gun trigger - aim the mount with the client's
                        // solution and fire the real gun host-side
                        int mountIdx = (int)msg.Heading;
                        var systems = unit._obp?._weaponSystems;
                        if (systems != null && mountIdx >= 0 && mountIdx < systems.Count
                            && systems[mountIdx] is WeaponSystemGun gun)
                        {
                            var dir = new Vector3(msg.TargetX, msg.TargetY, msg.TargetZ);
                            if (dir.sqrMagnitude > 0.01f)
                                gun._solutionVector = dir.normalized;
                            if (!string.IsNullOrEmpty(msg.AmmoId))
                            {
                                var gunAmmo = gun._vwp?._associatedMagazine?.getAmmunitionByName(msg.AmmoId)
                                              ?? gun.getOnWeaponAmmunition();
                                if (gunAmmo != null) gun._ammoForEngage = gunAmmo;
                            }
                            gun.fire();
                        }
                        break;
                    }

                    case Messages.OrderType.LaunchAircraft:
                    {
                        // v2: client carrier launch intent - host runs the deck pipeline;
                        // the spawned aircraft replicates back via createAircraft capture
                        var fd = unit._obp?._flightDeck;
                        if (fd == null)
                        {
                            Plugin.Log.LogWarning($"[Order] LaunchAircraft: unit {unit.UniqueID} has no flight deck");
                            break;
                        }
                        int vehicleIdx  = (int)msg.Speed;
                        int loadoutIdx  = (int)msg.Heading;
                        int squadronIdx = (int)msg.DestX;
                        int callsignIdx = (int)msg.DestY;
                        int launchCount = Mathf.Max(1, (int)msg.DestZ);
                        if (vehicleIdx < 0 || vehicleIdx >= fd._vehiclesOnBoard.Count) break;

                        var vehicle  = fd._vehiclesOnBoard[vehicleIdx];
                        var loadout  = (loadoutIdx >= 0 && loadoutIdx < vehicle.Loadouts.Count)
                            ? vehicle.Loadouts[loadoutIdx] : null;
                        var squadron = (squadronIdx >= 0 && squadronIdx < vehicle.Squadrons.Count)
                            ? vehicle.Squadrons[squadronIdx] : null;
                        string callsign = (squadron != null && callsignIdx >= 0 && callsignIdx < squadron.Callsigns.Count)
                            ? squadron.Callsigns[callsignIdx] : "";

                        var ltp = new LaunchTaskParameters
                        {
                            _launchCount = launchCount,
                            _missionType = (FlightDeckTask.MissionType)msg.ShotsToFire,
                        };
                        fd.createLaunchTask(vehicle, loadout, squadron, callsign, ltp, msg.TargetEntityId == 1);
                        Plugin.Log.LogInfo($"[Order] LaunchAircraft: carrier={unit.UniqueID} vehicle={vehicleIdx} count={launchCount}");
                        break;
                    }

                    case Messages.OrderType.AbortLaunch:
                    {
                        // v2: client aborted a pending carrier launch - cancel it host-side;
                        // the removal + restored stores stream back via FlightDeckState.
                        var fd = unit._obp?._flightDeck;
                        if (fd != null && System.Guid.TryParse(msg.AmmoId, out var abortUid))
                        {
                            fd.abortLaunchTask(abortUid);
                            Plugin.Log.LogInfo($"[Order] AbortLaunch: carrier={unit.UniqueID} uid={abortUid}");
                        }
                        break;
                    }

                    case Messages.OrderType.AllowLaunch:
                    {
                        // v2: client pressed LAUNCH on a readied pending task - release it
                        // host-side; the state change streams back via FlightDeckState.
                        var fd = unit._obp?._flightDeck;
                        if (fd != null && System.Guid.TryParse(msg.AmmoId, out var allowUid))
                        {
                            var tasks = fd.FlightDeckTasks;
                            for (int t = 0; t < tasks.Count; t++)
                            {
                                if (!(tasks[t] is PendingLaunchTask plt) || plt._uid != allowUid) continue;
                                // AllowLaunchFunc removes Commands[1] - only valid while the
                                // task is parked in AwaitSpawn, which is what added it.
                                if (plt._stateMachine?.CurrentState is HandleAwaitSpawnTask)
                                    plt.AllowLaunchFunc(null);
                                Plugin.Log.LogInfo($"[Order] AllowLaunch: carrier={unit.UniqueID} uid={allowUid}");
                                break;
                            }
                        }
                        break;
                    }

                    // SetHeading removed - setRudderAngle patch was semantically wrong
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
                        ObjectBase? target = null;
                        if (msg.TargetEntityId > 0)
                            target = StateSerializer.FindById(msg.TargetEntityId);
                        var targetPos = new Vector3(msg.TargetX, msg.TargetY, msg.TargetZ);
                        // PvP: coordinates are always GeoPosition - always convert back.
                        // This is needed even when target is found, because the game falls
                        // back to targetPosition if the target is destroyed mid-flight.
                        if (Plugin.Instance.CfgPvP.Value)
                        {
                            var geo = new GeoPosition { _longitude = msg.TargetX, _latitude = msg.TargetZ, _height = msg.TargetY };
                            Vector2 local = Utils.longLatToLocal(geo, Globals._currentCenterTile);
                            targetPos = new Vector3(local.x, msg.TargetY, local.y);
                        }
                        if (target != null)
                        {
                            // The remote player's attack decision is authoritative -
                            // exempt this (unit, target) pair from the host-side crew
                            // contact-processing gate (AI.IsProcessed) or a submerged
                            // sub whose own sonar never held the target sits on the
                            // task forever (and ripple-fires it when the gate flips).
                            Patch_ObjectBase_HandleEngageTasks.MarkNetworkOrderedTarget(
                                unit.UniqueID, target.UniqueID);
                            unit.AddEngageTask(new EngageTask(msg.AmmoId, target,       unit, msg.ShotsToFire));
                        }
                        else
                            unit.AddEngageTask(new EngageTask(msg.AmmoId, targetPos, unit, msg.ShotsToFire));
                        Plugin.Log.LogInfo($"[Order] FireWeapon: engage task queued on {unit.UniqueID} ({unit.name}) " +
                            $"ammo={msg.AmmoId} target={msg.TargetEntityId} shots={msg.ShotsToFire} " +
                            $"tasks={unit._currentEngageTasks.Count} controllable={unit.IsControllable}");
                        break;
                    }

                    case Messages.OrderType.SetDepth:
                        if (unit is Submarine sub) sub.setDepth(msg.Speed);
                        break;

                    // Manual rudder (A/D). InputHandler flags the player override
                    // before every step - without it the receiving side's autopilot
                    // (setRudderBasedOnCourse) overwrites the commanded angle on the
                    // next tick and the turn never happens.
                    case Messages.OrderType.SetRudder:
                        unit.setPlayerCommandOverride(true);
                        StateSerializer.SetRudderAngle(unit, msg.Speed);
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

                case GameEventType.MissionEnd:
                    // v2: mission end is host-decided; the client's own trigger
                    // evaluation is suppressed, so this is the only way it ends.
                    if (!Plugin.Instance.CfgIsHost.Value
                        && Singleton<MissionManager>.InstanceExists(false))
                    {
                        using (Authority.Allowed())
                            Singleton<MissionManager>.Instance.CalculateEndMissionData();
                    }
                    break;
            }
        }
    }
}
