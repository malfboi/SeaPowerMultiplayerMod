using System.Collections.Generic;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Handles flight ops sync in PvP mode.
    ///
    /// Only the carrier owner runs PendingLaunchTask. When launchVehicle fires,
    /// the authoritative side sends a SpawnVehicle message. The remote side calls
    /// launchVehicle directly, skipping PendingLaunchTask entirely. Both sides
    /// then run the LaunchTask pipeline (elevator → taxi → takeoff) in parallel
    /// from the same starting moment, achieving tight takeoff synchronization.
    ///
    /// After spawning, the aircraft is added to PipelineAircraftIds. StateApplier
    /// skips position corrections for these IDs until the local aircraft is
    /// airborne (_isInFlight == true), preventing state updates from fighting
    /// with the pipeline's local positioning.
    /// </summary>
    public static class FlightOpsHandler
    {
        /// <summary>
        /// True while applying a network-received launch operation.
        /// Checked by Harmony patches to allow through and skip re-sending.
        /// </summary>
        internal static bool ApplyingFromNetwork;

        /// <summary>
        /// Vessel IDs that have network-synced launches in progress.
        /// Patch_FlightDeck_LaunchVehicle.Prefix checks this to allow
        /// the pipeline's launchVehicle call through for enemy carriers.
        /// </summary>
        internal static readonly HashSet<int> NetworkSyncedVessels = new();

        /// <summary>
        /// Aircraft IDs currently going through the flight deck pipeline.
        /// StateApplier skips position corrections for these IDs to prevent
        /// state updates from fighting with the pipeline's local positioning.
        /// Removed once the local aircraft's _isInFlight becomes true.
        /// </summary>
        internal static readonly HashSet<int> PipelineAircraftIds = new();

        // ── Stats (read by UI) ──────────────────────────────────────────────

        public static int PipelineCount => PipelineAircraftIds.Count;
        public static int SyncedCarrierCount => NetworkSyncedVessels.Count;
        public static int DeferredSpawnCount => _deferredSpawns.Count;

        // ── Safe ID range (prevents cross-side collisions) ──────────────────
        // Both sides start the same scenario → same UniqueID counter.
        // Simultaneous launches produce identical IDs. We separate ranges:
        //   Host aircraft: 2,000,001+    Client aircraft: 3,000,001+
        private static int _nextSafeId;
        private static bool _idRangeInitialized;

        /// <summary>
        /// Returns a UniqueID guaranteed not to collide with other players' aircraft.
        /// Each player gets a 1M ID range: 2M + playerId * 1M.
        /// </summary>
        internal static int NextSafeId()
        {
            if (!_idRangeInitialized)
            {
                int playerId = PlayerRegistry.LocalPlayerId;
                _nextSafeId = 2_000_000 + playerId * 1_000_000;
                _idRangeInitialized = true;
            }
            return ++_nextSafeId;
        }

        /// <summary>
        /// SpawnVehicle messages deferred because all elevators were busy.
        /// Retried each frame via Tick() in FIFO order.
        /// </summary>
        private static readonly List<FlightOpsMessage> _deferredSpawns = new();

        public static void Apply(FlightOpsMessage msg)
        {
            switch (msg.OpsType)
            {
                case FlightOpsType.Launch:
                    ApplyLaunch(msg);
                    break;
                case FlightOpsType.SpawnVehicle:
                    if (!ApplySpawnVehicle(msg))
                        _deferredSpawns.Add(msg);
                    break;
            }
        }

        /// <summary>
        /// Launch is now notification-only. The remote side logs the event
        /// but does NOT call createLaunchTask. The actual spawn happens when
        /// SpawnVehicle arrives.
        /// </summary>
        private static void ApplyLaunch(FlightOpsMessage msg)
        {
            Plugin.Log.LogInfo($"[FlightOps] Enemy carrier {msg.VesselId} preparing launch: " +
                $"vehicle={msg.VehicleIndex} loadout={msg.LoadoutIndex} " +
                $"squadron={msg.SquadronIndex} count={msg.LaunchCount}");
        }

        /// <summary>
        /// Assigns a safe UniqueID to an aircraft without polluting the game's
        /// global _UID counter. SetUniqueId bumps _UID into our safe range,
        /// which causes subsequently game-spawned objects to get IDs that
        /// collide with our safe IDs. We save/restore _UID to prevent this.
        /// Also checks for ID collisions and bumps any existing object first.
        /// </summary>
        private static void SafeSetUniqueId(ObjectBase aircraft, int newId)
        {
            // Check for ID collision — another object may already have this ID
            // because SetUniqueId previously bumped _UID into our safe range
            var existing = StateSerializer.FindById(newId);
            if (existing != null && existing != aircraft)
            {
                int bumpId = NextSafeId();
                Plugin.Log.LogWarning($"[FlightOps] ID collision: {newId} already used by " +
                    $"{existing._type} — bumping existing to {bumpId}");

                int savedUid = Singleton<SceneCreator>.Instance._UID;
                existing.SetUniqueId(bumpId);
                Singleton<SceneCreator>.Instance._UID = savedUid;
            }

            // Save and restore _UID to prevent counter pollution
            int prevUid = Singleton<SceneCreator>.Instance._UID;
            aircraft.SetUniqueId(newId);
            Singleton<SceneCreator>.Instance._UID = prevUid;
        }

        /// <summary>
        /// Called when the authoritative side's launchVehicle fires.
        /// Directly calls launchVehicle on the remote side, skipping PendingLaunchTask.
        /// Returns true if the spawn succeeded, false if deferred.
        /// </summary>
        private static bool ApplySpawnVehicle(FlightOpsMessage msg)
        {
            var vessel = StateSerializer.FindById(msg.VesselId);
            if (vessel?._obp?._flightDeck == null)
            {
                Plugin.Log.LogWarning($"[FlightOps] SpawnVehicle: vessel {msg.VesselId} not found or has no flight deck");
                return true; // don't defer, it won't get better
            }

            var fd = vessel._obp._flightDeck;

            // Bounds checks
            if (msg.VehicleIndex < 0 || msg.VehicleIndex >= fd._vehiclesOnBoard.Count)
            {
                Plugin.Log.LogWarning($"[FlightOps] SpawnVehicle: vehicleIndex {msg.VehicleIndex} out of range (count={fd._vehiclesOnBoard.Count})");
                return true;
            }
            var vehicle = fd._vehiclesOnBoard[msg.VehicleIndex];
            if (msg.LoadoutIndex < 0 || msg.LoadoutIndex >= vehicle.Loadouts.Count)
            {
                Plugin.Log.LogWarning($"[FlightOps] SpawnVehicle: loadoutIndex {msg.LoadoutIndex} out of range");
                return true;
            }
            if (msg.SquadronIndex < 0 || msg.SquadronIndex >= vehicle.Squadrons.Count)
            {
                Plugin.Log.LogWarning($"[FlightOps] SpawnVehicle: squadronIndex {msg.SquadronIndex} out of range");
                return true;
            }

            // Select elevator: prefer the one used by authoritative side, fall back to others.
            // Helipad-only vessels (frigates, destroyers) have no elevators — the game
            // calls launchVehicle with null elevator for helicopter launches from these ships.
            Elevator elevator = null;
            bool hasElevators = fd._elevators != null && fd._elevators.Count > 0;
            if (hasElevators)
            {
                if (msg.ElevatorIndex >= 0 && msg.ElevatorIndex < fd._elevators.Count
                    && !fd._elevators[msg.ElevatorIndex].IsBusy)
                {
                    elevator = fd._elevators[msg.ElevatorIndex];
                }
                else
                {
                    // Try any non-busy elevator
                    for (int i = 0; i < fd._elevators.Count; i++)
                    {
                        if (!fd._elevators[i].IsBusy)
                        {
                            elevator = fd._elevators[i];
                            break;
                        }
                    }
                }

                if (elevator == null)
                {
                    // All elevators busy — defer and retry next frame
                    Plugin.Log.LogInfo($"[FlightOps] SpawnVehicle deferred: all elevators busy on vessel {msg.VesselId} " +
                        $"(safeId={msg.SpawnedUnitId}, deferred count={_deferredSpawns.Count})");
                    return false;
                }
            }

            // Mark vessel so launchVehicle prefix allows it through
            NetworkSyncedVessels.Add(msg.VesselId);

            // Build LaunchTaskParameters
            var ltp = new LaunchTaskParameters
            {
                _vehicleIndex  = msg.VehicleIndex,
                _squadronIndex = msg.SquadronIndex,
                _loadoutIndex  = msg.LoadoutIndex,
                _callsignIndex = msg.CallsignIndex,
                _launchCount   = 1,
                _missionType   = (FlightDeckTask.MissionType)msg.MissionType,
            };

            ApplyingFromNetwork = true;
            bool prevCheat = SeaPower.Globals._cheatFlightDeckAmmo;
            SeaPower.Globals._cheatFlightDeckAmmo = true;
            try
            {
                UnitFormation formation = null;
                var result = fd.launchVehicle(elevator, msg.VehicleIndex, msg.LoadoutIndex,
                    msg.SquadronIndex, msg.CallsignIndex, ref formation, msg.IsMultipleLaunch, ltp);

                if (result == null)
                {
                    Plugin.Log.LogWarning($"[FlightOps] launchVehicle returned null for vessel {msg.VesselId}");
                }
                else
                {
                    // Assign the authoritative safe ID using collision-safe method
                    int localId = result.UniqueID;
                    SafeSetUniqueId(result, msg.SpawnedUnitId);
                    PipelineAircraftIds.Add(msg.SpawnedUnitId);
                    Plugin.Log.LogInfo($"[FlightOps] SpawnVehicle applied: vessel={msg.VesselId} " +
                        $"aircraft={localId}->{msg.SpawnedUnitId} elevator={msg.ElevatorIndex} " +
                        $"type={vehicle._type}");
                }
            }
            finally
            {
                SeaPower.Globals._cheatFlightDeckAmmo = prevCheat;
                ApplyingFromNetwork = false;
            }

            return true;
        }

        /// <summary>
        /// Called from Plugin.Update() to process deferred spawns.
        /// Processes in FIFO order to preserve authoritative spawn sequence.
        /// </summary>
        internal static void Tick()
        {
            if (_deferredSpawns.Count == 0) return;

            // Process from front (oldest first) to maintain spawn order.
            // Only process one per frame to avoid elevator contention within
            // the same tick (launchVehicle marks elevator busy immediately).
            if (ApplySpawnVehicle(_deferredSpawns[0]))
                _deferredSpawns.RemoveAt(0);
        }

        /// <summary>
        /// Reset all flight ops state. Called on disconnect.
        /// </summary>
        internal static void Clear()
        {
            NetworkSyncedVessels.Clear();
            PipelineAircraftIds.Clear();
            _deferredSpawns.Clear();
            _idRangeInitialized = false;
            _nextSafeId = 0;
        }

        /// <summary>
        /// Returns true if the given unit is a pipeline-managed aircraft that
        /// should NOT be destroyed by orphan cleanup.
        /// </summary>
        internal static bool IsProtectedFromOrphanCleanup(ObjectBase unit)
        {
            return PipelineAircraftIds.Contains(unit.UniqueID);
        }

        /// <summary>
        /// Called from StateApplier to check if a unit should skip state update
        /// positioning. Returns true if the unit is still in the flight deck
        /// pipeline and should not be repositioned.
        /// </summary>
        internal static bool ShouldSkipStateUpdate(int entityId, ObjectBase unit)
        {
            if (!PipelineAircraftIds.Contains(entityId))
                return false;

            // Check if the local aircraft is now airborne
            bool inFlight = false;
            if (unit is Aircraft ac)
                inFlight = ac._isInFlight;
            else if (unit is Helicopter heli)
                inFlight = heli._isInFlight;

            if (inFlight)
            {
                // Aircraft is airborne — remove protection, allow state updates
                PipelineAircraftIds.Remove(entityId);
                Plugin.Log.LogInfo($"[FlightOps] Aircraft {entityId} is airborne — " +
                    $"removing pipeline protection, state updates active");
                return false;
            }

            // Still in pipeline — skip state update positioning
            return true;
        }
    }
}
