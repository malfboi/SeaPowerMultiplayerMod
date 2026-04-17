using System.Collections.Generic;
using LiteNetLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

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

        // ── Spawn records (for recovery requests) ─────────────────────────

        internal struct AircraftSpawnRecord
        {
            public int CarrierVesselId;
            public string VehicleTypeName;
            public int LoadoutIndex;
            public int SquadronIndex;
            public int CallsignIndex;
            public byte MissionType;
            public bool IsMultipleLaunch;
        }

        private static readonly Dictionary<int, AircraftSpawnRecord> _spawnRecords = new();

        internal static void RecordSpawn(int safeId, AircraftSpawnRecord record)
        {
            _spawnRecords[safeId] = record;
        }

        // ── Aircraft recovery (detects missing remote aircraft, requests info, hard-spawns) ──

        private static readonly Dictionary<int, int> _missingAircraftSightings = new();
        private static readonly Dictionary<int, float> _recoveryRequested = new();
        private static readonly Dictionary<int, int> _recoveryAttempts = new();
        private static readonly HashSet<int> _recoveryInProgress = new();
        private static readonly HashSet<int> _recoveryAbandoned = new();
        private const int RecoveryDetectionThreshold = 3;
        private const float RecoveryCooldown = 2f;
        private const int MaxRecoveryAttempts = 10;
        private const float RecoveryLogInterval = 10f;
        private static readonly Dictionary<int, float> _recoveryLogTime = new();

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
        /// Returns a UniqueID guaranteed not to collide with the other side's aircraft.
        /// </summary>
        internal static int NextSafeId()
        {
            if (!_idRangeInitialized)
            {
                _nextSafeId = Plugin.Instance.CfgIsHost.Value ? 2_000_000 : 3_000_000;
                _idRangeInitialized = true;
            }
            return ++_nextSafeId;
        }

        /// <summary>
        /// SpawnVehicle messages deferred because all elevators were busy.
        /// Retried each frame via Tick() in FIFO order.
        /// </summary>
        private static readonly List<FlightOpsMessage> _deferredSpawns = new();

        /// <summary>Tracks when each deferred spawn was first added (by safe ID).</summary>
        private static readonly Dictionary<int, float> _deferredSpawnTime = new();

        /// <summary>Maximum time (seconds) to keep retrying a deferred spawn before discarding.</summary>
        private const float DeferredSpawnTimeout = 30f;

        /// <summary>Rate-limit deferred spawn logging: last log time per safe ID.</summary>
        private static readonly Dictionary<int, float> _deferredLogTime = new();
        private const float DeferredLogInterval = 5f;

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

            // Bounds checks — fall back to type-name matching if index is invalid
            if (msg.VehicleIndex < 0 || msg.VehicleIndex >= fd._vehiclesOnBoard.Count)
            {
                if (!string.IsNullOrEmpty(msg.VehicleTypeName))
                {
                    int fallback = FindVehicleIndexByType(fd, msg.VehicleTypeName);
                    if (fallback >= 0)
                    {
                        Plugin.Log.LogWarning($"[FlightOps] SpawnVehicle: vehicleIndex {msg.VehicleIndex} out of range " +
                            $"(count={fd._vehiclesOnBoard.Count}), using type-name fallback '{msg.VehicleTypeName}' -> index {fallback}");
                        msg.VehicleIndex = fallback;
                    }
                    else
                    {
                        Plugin.Log.LogWarning($"[FlightOps] SpawnVehicle: vehicleIndex {msg.VehicleIndex} out of range " +
                            $"and no '{msg.VehicleTypeName}' found on vessel {msg.VesselId}");
                        return true;
                    }
                }
                else
                {
                    Plugin.Log.LogWarning($"[FlightOps] SpawnVehicle: vehicleIndex {msg.VehicleIndex} out of range " +
                        $"(count={fd._vehiclesOnBoard.Count}), no type name available");
                    return true;
                }
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
                    // All elevators busy — defer and retry next frame (rate-limit logging)
                    float now = Time.unscaledTime;
                    if (!_deferredLogTime.TryGetValue(msg.SpawnedUnitId, out float lastLog)
                        || now - lastLog >= DeferredLogInterval)
                    {
                        _deferredLogTime[msg.SpawnedUnitId] = now;
                        Plugin.Log.LogInfo($"[FlightOps] SpawnVehicle deferred: all elevators busy on vessel {msg.VesselId} " +
                            $"(safeId={msg.SpawnedUnitId}, deferred count={_deferredSpawns.Count})");
                    }
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
        /// Discards entries that have been retrying beyond the timeout.
        /// </summary>
        internal static void Tick()
        {
            if (_deferredSpawns.Count == 0) return;

            var first = _deferredSpawns[0];
            int safeId = first.SpawnedUnitId;

            // Track when this spawn was first deferred
            float now = Time.unscaledTime;
            if (!_deferredSpawnTime.ContainsKey(safeId))
                _deferredSpawnTime[safeId] = now;

            // Timeout: discard if retrying too long
            if (now - _deferredSpawnTime[safeId] > DeferredSpawnTimeout)
            {
                Plugin.Log.LogWarning($"[FlightOps] SpawnVehicle timed out after {DeferredSpawnTimeout}s: " +
                    $"vessel={first.VesselId} safeId={safeId} — discarding");
                _deferredSpawns.RemoveAt(0);
                _deferredSpawnTime.Remove(safeId);
                _deferredLogTime.Remove(safeId);
                return;
            }

            // Process from front (oldest first) to maintain spawn order.
            // Only process one per frame to avoid elevator contention within
            // the same tick (launchVehicle marks elevator busy immediately).
            if (ApplySpawnVehicle(first))
            {
                _deferredSpawns.RemoveAt(0);
                _deferredSpawnTime.Remove(safeId);
                _deferredLogTime.Remove(safeId);
            }
        }

        /// <summary>
        /// Finds the first vehicle in the flight deck's _vehiclesOnBoard list
        /// whose _type matches the given type name. Returns -1 if not found.
        /// </summary>
        internal static int FindVehicleIndexByType(FlightDeck fd, string typeName)
        {
            for (int i = 0; i < fd._vehiclesOnBoard.Count; i++)
            {
                if (fd._vehiclesOnBoard[i]._type.ToString() == typeName)
                    return i;
            }
            return -1;
        }

        // ── Aircraft recovery: detection, request, response ─────────────────

        /// <summary>
        /// Called from StateApplier when a state update contains a remote aircraft
        /// ID that doesn't exist locally. Rate-limits and sends recovery requests.
        /// </summary>
        internal static void OnMissingRemoteAircraft(int aircraftId)
        {
            if (PipelineAircraftIds.Contains(aircraftId)) return;
            if (_recoveryInProgress.Contains(aircraftId)) return;
            if (_recoveryAbandoned.Contains(aircraftId)) return;

            _missingAircraftSightings.TryGetValue(aircraftId, out int count);
            count++;
            _missingAircraftSightings[aircraftId] = count;
            if (count < RecoveryDetectionThreshold) return;

            if (_recoveryRequested.TryGetValue(aircraftId, out float lastTime)
                && Time.unscaledTime - lastTime < RecoveryCooldown)
                return;

            // Check if we've exceeded max recovery attempts
            _recoveryAttempts.TryGetValue(aircraftId, out int attempts);
            if (attempts >= MaxRecoveryAttempts)
            {
                _recoveryAbandoned.Add(aircraftId);
                Plugin.Log.LogWarning($"[FlightOps] Recovery: giving up on aircraft {aircraftId} " +
                    $"after {MaxRecoveryAttempts} attempts");
                CleanupRecoveryTracking(aircraftId);
                return;
            }
            _recoveryAttempts[aircraftId] = attempts + 1;

            // Rate-limit logging
            float now = Time.unscaledTime;
            if (!_recoveryLogTime.TryGetValue(aircraftId, out float lastLog)
                || now - lastLog >= RecoveryLogInterval)
            {
                _recoveryLogTime[aircraftId] = now;
                Plugin.Log.LogInfo($"[FlightOps] Recovery: requesting info for missing aircraft {aircraftId} " +
                    $"(seen {count} times, attempt {attempts + 1}/{MaxRecoveryAttempts})");
            }

            var req = new AircraftRecoveryRequestMessage { MissingAircraftId = aircraftId };
            NetworkManager.Instance.SendToOther(req, DeliveryMethod.ReliableOrdered);
            _recoveryRequested[aircraftId] = now;
        }

        /// <summary>
        /// Authoritative side: handles a recovery request by looking up the aircraft's
        /// spawn record and sending back the details needed to recreate it.
        /// </summary>
        internal static void HandleRecoveryRequest(AircraftRecoveryRequestMessage msg)
        {
            int id = msg.MissingAircraftId;
            var aircraft = StateSerializer.FindById(id);

            // Aircraft doesn't exist or is destroyed — tell the requester to stop looking
            if (aircraft == null)
            {
                Plugin.Log.LogInfo($"[FlightOps] Recovery: aircraft {id} not found, sending NotFound");
                var resp = new AircraftRecoveryResponseMessage { AircraftId = id, NotFound = true };
                NetworkManager.Instance.SendToOther(resp, DeliveryMethod.ReliableOrdered);
                return;
            }

            // If not yet airborne, don't respond — requester will retry after cooldown
            bool inFlight = false;
            if (aircraft is Aircraft ac) inFlight = ac._isInFlight;
            else if (aircraft is Helicopter heli) inFlight = heli._isInFlight;
            if (!inFlight)
            {
                float now = Time.unscaledTime;
                if (!_recoveryLogTime.TryGetValue(id, out float lastLog)
                    || now - lastLog >= RecoveryLogInterval)
                {
                    _recoveryLogTime[id] = now;
                    Plugin.Log.LogInfo($"[FlightOps] Recovery: aircraft {id} not yet airborne, deferring response");
                }
                return;
            }

            // Look up spawn record
            if (!_spawnRecords.TryGetValue(id, out var record))
            {
                Plugin.Log.LogWarning($"[FlightOps] Recovery: no spawn record for aircraft {id}, sending NotFound");
                var resp = new AircraftRecoveryResponseMessage { AircraftId = id, NotFound = true };
                NetworkManager.Instance.SendToOther(resp, DeliveryMethod.ReliableOrdered);
                return;
            }

            Plugin.Log.LogInfo($"[FlightOps] Recovery: sending spawn info for aircraft {id} " +
                $"(carrier={record.CarrierVesselId}, type={record.VehicleTypeName})");

            var response = new AircraftRecoveryResponseMessage
            {
                AircraftId       = id,
                NotFound         = false,
                CarrierVesselId  = record.CarrierVesselId,
                VehicleTypeName  = record.VehicleTypeName,
                LoadoutIndex     = record.LoadoutIndex,
                SquadronIndex    = record.SquadronIndex,
                CallsignIndex    = record.CallsignIndex,
                MissionType      = record.MissionType,
                IsMultipleLaunch = record.IsMultipleLaunch,
            };
            NetworkManager.Instance.SendToOther(response, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Requesting side: receives spawn info for a missing aircraft and attempts
        /// to recreate it via launchVehicle with type-name-matched parameters.
        /// </summary>
        internal static void HandleRecoveryResponse(AircraftRecoveryResponseMessage msg)
        {
            int id = msg.AircraftId;

            if (msg.NotFound)
            {
                Plugin.Log.LogInfo($"[FlightOps] Recovery: aircraft {id} no longer exists on remote, cleaning up");
                CleanupRecoveryTracking(id);
                return;
            }

            // Race condition: aircraft appeared via normal spawn path
            if (StateSerializer.FindById(id) != null)
            {
                Plugin.Log.LogInfo($"[FlightOps] Recovery: aircraft {id} already exists locally, cleaning up");
                CleanupRecoveryTracking(id);
                return;
            }

            var vessel = StateSerializer.FindById(msg.CarrierVesselId);
            if (vessel?._obp?._flightDeck == null)
            {
                Plugin.Log.LogWarning($"[FlightOps] Recovery: carrier {msg.CarrierVesselId} not found for aircraft {id}");
                CleanupRecoveryTracking(id);
                return;
            }

            var fd = vessel._obp._flightDeck;
            int vehicleIndex = FindVehicleIndexByType(fd, msg.VehicleTypeName);
            if (vehicleIndex < 0)
            {
                float now = Time.unscaledTime;
                if (!_recoveryLogTime.TryGetValue(id, out float lastLog)
                    || now - lastLog >= RecoveryLogInterval)
                {
                    _recoveryLogTime[id] = now;
                    Plugin.Log.LogWarning($"[FlightOps] Recovery: no '{msg.VehicleTypeName}' found on carrier {msg.CarrierVesselId}");
                }
                CleanupRecoveryTracking(id);
                return;
            }

            Plugin.Log.LogInfo($"[FlightOps] Recovery: spawning aircraft {id} via carrier {msg.CarrierVesselId} " +
                $"(type={msg.VehicleTypeName}, vehicleIndex={vehicleIndex})");

            _recoveryInProgress.Add(id);

            var synthetic = new FlightOpsMessage
            {
                OpsType          = FlightOpsType.SpawnVehicle,
                VesselId         = msg.CarrierVesselId,
                VehicleIndex     = vehicleIndex,
                LoadoutIndex     = msg.LoadoutIndex,
                SquadronIndex    = msg.SquadronIndex,
                CallsignIndex    = msg.CallsignIndex,
                MissionType      = msg.MissionType,
                SpawnedUnitId    = id,
                ElevatorIndex    = -1,  // let ApplySpawnVehicle find any free elevator
                IsMultipleLaunch = msg.IsMultipleLaunch,
                VehicleTypeName  = msg.VehicleTypeName,
            };

            if (!ApplySpawnVehicle(synthetic))
                _deferredSpawns.Add(synthetic);

            _missingAircraftSightings.Remove(id);
        }

        private static void CleanupRecoveryTracking(int id)
        {
            _missingAircraftSightings.Remove(id);
            _recoveryRequested.Remove(id);
            _recoveryInProgress.Remove(id);
        }

        /// <summary>
        /// Reset all flight ops state. Called on disconnect.
        /// </summary>
        internal static void Clear()
        {
            NetworkSyncedVessels.Clear();
            PipelineAircraftIds.Clear();
            _deferredSpawns.Clear();
            _deferredSpawnTime.Clear();
            _deferredLogTime.Clear();
            _spawnRecords.Clear();
            _missingAircraftSightings.Clear();
            _recoveryRequested.Clear();
            _recoveryAttempts.Clear();
            _recoveryInProgress.Clear();
            _recoveryAbandoned.Clear();
            _recoveryLogTime.Clear();
            _idRangeInitialized = false;
            _nextSafeId = 0;
        }

        /// <summary>
        /// Returns true if the given unit is a pipeline-managed aircraft that
        /// should NOT be destroyed by orphan cleanup.
        /// </summary>
        internal static bool IsProtectedFromOrphanCleanup(ObjectBase unit)
        {
            int id = unit.UniqueID;
            return PipelineAircraftIds.Contains(id) || _recoveryInProgress.Contains(id);
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
                if (_recoveryInProgress.Remove(entityId))
                    _recoveryRequested.Remove(entityId);
                Plugin.Log.LogInfo($"[FlightOps] Aircraft {entityId} is airborne — " +
                    $"removing pipeline protection, state updates active");
                return false;
            }

            // Still in pipeline — skip state update positioning
            return true;
        }
    }
}
