using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using LiteNetLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Host pauses, saves state, sends to client. Client loads and runs locally.
    /// Both sides run full physics independently after load.
    /// </summary>
    public static class SessionManager
    {
        public static bool IsReceiving { get; private set; }

        /// <summary>True while the client is loading a scene. Suppresses patches that crash during load.</summary>
        public static bool SceneLoading { get; private set; }

        /// <summary>True after OnSceneReady completes — means we have a live scene that must be unloaded before reloading.</summary>
        private static bool _inGame;

        private static int _pendingRngSeed;
        private static float _pendingGameSeconds;

        /// <summary>When > 0, a resync retry is pending. Counts down each Update frame.</summary>
        private static float _retrySendAt;
        private static int _retryCount;
        private const int MaxRetries = 3;
        private const float RetryDelaySec = 2f;

        private static ManualLogSource Log => Plugin.Log;

        // ── Host side ─────────────────────────────────────────────────────────

        /// <summary>Called from Plugin.Update() to check for pending resync retries.</summary>
        public static void TickRetry()
        {
            if (_retrySendAt <= 0f) return;
            if (Time.unscaledTime < _retrySendAt) return;
            _retrySendAt = 0f;
            Log.LogInfo($"[Session] Retry #{_retryCount}/{MaxRetries} — re-sending session sync");
            CaptureAndSend();
        }

        public static void CaptureAndSend()
        {
            if (SceneLoading)
            {
                Log.LogWarning("[Session] CaptureAndSend skipped — SceneLoading=true");
                return;
            }

            Log.LogInfo("[Session] CaptureAndSend starting...");

            // Pause and set sync state before saving
            SceneLoading = true; // suppress broadcasts during pause+save
            Log.LogInfo("[Session] Pausing game...");
            GameTime.Pause();
            SimSyncManager.Reset();
            SimSyncManager.CurrentState = SimState.WaitingForClient;
            SceneLoading = false; // host isn't actually loading a scene

            // Reset sync health counters on host side too
            UnitRegistry.Clear();
            UnitRegistry.PopulateFromScene();
            CombatEventHandler.ResetCounters();
            MissileStateSyncHandler.ResetCounters();
            StateApplier.ResetOrphanTracking();
            PvPFireAuth.Clear();
            Patch_ObjectBase_HandleEngageTasks.Reset();
            Patch_Blastzone_OnHitUnit.ClearMissileImpacts();
            Patch_Blastzone_OnHitWeapon.ClearInterceptions();
            CombatEventHandler.ClearDeathWatch();
            Patch_ObjectBase_NotifyDestroyed_PvP.Clear();
            Patch_WeaponBase_CommonLaunchSettings.ClearSpawnTimes();
            ProjectileIdMapper.Clear();
            Patch_Submarine_SetDepth.Reset();
            OrderDeduplicator.Clear();

            // PvP: flush stale engage tasks on enemy puppet units so they don't
            // consume auth tokens when the remote player issues fire orders.
            if (Plugin.Instance.CfgPvP.Value)
                FlushEnemyEngageTasks();

            // SaveGame does not check IsSavingAllowed, but set it true to be safe
            bool wasAllowed = SaveLoadManager.IsSavingAllowed;
            SaveLoadManager.IsSavingAllowed = true;

            Log.LogInfo("[Session] Saving game to MPSession.sav...");
            string savePath = SaveLoadManager.SaveGame("MPSession.sav");

            SaveLoadManager.IsSavingAllowed = wasAllowed;

            if (string.IsNullOrEmpty(savePath))
            {
                Log.LogWarning("[Session] SaveGame returned empty path — aborting sync.");
                SimSyncManager.Reset();
                return;
            }

            Log.LogInfo($"[Session] Save path: {savePath}");

            // Read save data from IniHandler's in-memory cache instead of disk.
            // SaveLoadManager.WriteMissionToFile populates the IniHandler synchronously
            // but writes to disk via Task.Run — reading the file would race against
            // that async write and return stale/old data.
            var ini = IniHandler.get(savePath);
            if (ini?.Data == null || ini.Data.Count == 0)
            {
                Log.LogWarning("[Session] IniHandler cache empty for save — aborting sync.");
                SimSyncManager.Reset();
                return;
            }
            string saveContent = SerializeIni(ini.Data);
            Log.LogInfo($"[Session] Save size (from cache): {saveContent.Length} chars");

            // Compute deterministic RNG seed from save content
            int rngSeed = saveContent.GetHashCode();

            // Parse BaseFile= from the save to locate the source mission .ini
            string missionFileName    = "";
            string missionFileContent = "";

            var match = Regex.Match(saveContent, @"(?im)^\s*BaseFile\s*=\s*(.+?)\s*$");
            if (match.Success)
            {
                string relPath = match.Groups[1].Value.Trim();
                string fullPath = Singleton<FileManager>.Instance.GetFile(relPath, null);
                if (fullPath != null && File.Exists(fullPath))
                {
                    missionFileName    = Path.GetFileName(fullPath);
                    missionFileContent = File.ReadAllText(fullPath);
                }
                else
                {
                    missionFileName = Path.GetFileName(relPath);
                }
            }

            if (string.IsNullOrEmpty(missionFileName))
                missionFileName = "MPMission.ini";

            float gameSeconds = Singleton<SeaPower.Environment>.Instance.Seconds;

            var msg = new SessionSyncMessage
            {
                SaveFileContent    = saveContent,
                MissionFileName    = missionFileName,
                MissionFileContent = missionFileContent,
                RngSeed            = rngSeed,
                GameSeconds        = gameSeconds,
            };

            Log.LogInfo($"[Session] Broadcasting SessionSync: save={saveContent.Length}ch, mission={missionFileName} ({missionFileContent.Length}ch), rngSeed={rngSeed}");
            NetworkManager.Instance.BroadcastToClients(msg, DeliveryMethod.ReliableOrdered);

            if (NetworkManager.Instance.LastSendFailed)
            {
                if (_retryCount < MaxRetries)
                {
                    _retryCount++;
                    _retrySendAt = Time.unscaledTime + RetryDelaySec;
                    Log.LogWarning($"[Session] Send failed — scheduling retry #{_retryCount}/{MaxRetries} in {RetryDelaySec}s");
                    return;
                }
                else
                {
                    Log.LogError($"[Session] Send failed after {MaxRetries} retries — session sync could not be delivered. Save may be too large ({saveContent.Length} chars).");
                    _retryCount = 0;
                    SimSyncManager.Reset();
                    return;
                }
            }

            // Success — reset retry counter
            _retryCount = 0;

            // Seed host RNG to match what client will use
            RngSeeder.SeedAll(rngSeed);

            Log.LogInfo($"[Session] State sent. SimState={SimSyncManager.CurrentState}, GamePaused={GameTime.IsPaused()}");
        }

        /// <summary>
        /// Serialize an IniHandler Data dictionary to INI-format string.
        /// Matches the format IniHandler.saveFile() writes: [Section]\r\nKey=Value\r\n
        /// </summary>
        private static string SerializeIni(Dictionary<string, Dictionary<string, string>> data)
        {
            var sb = new StringBuilder();
            foreach (var section in data)
            {
                sb.Append('[').Append(section.Key).Append("]\r\n");
                foreach (var kvp in section.Value)
                    sb.Append(kvp.Key).Append('=').Append(kvp.Value).Append("\r\n");
            }
            return sb.ToString();
        }

        // ── Client side ───────────────────────────────────────────────────────

        public static void ApplyReceivedSession(SessionSyncMessage msg)
        {
            Log.LogInfo($"[Session] Received SessionSync: loadByName={msg.LoadByName}, mission={msg.MissionFileName}, save={msg.SaveFileContent?.Length ?? 0}ch, rngSeed={msg.RngSeed}");
            IsReceiving = true;

            try
            {
                // Clear state and projectile ID mappings from previous session
                UnitRegistry.Clear();
                ProjectileIdMapper.Clear();
                PvPDeathNotifications.Clear();
                PvPFireAuth.Clear();
                Patch_ObjectBase_HandleEngageTasks.Reset();
                Patch_Blastzone_OnHitUnit.ClearMissileImpacts();
                Patch_Blastzone_OnHitWeapon.ClearInterceptions();
                Patch_ObjectBase_NotifyDestroyed_PvP.Clear();
                Patch_WeaponBase_CommonLaunchSettings.ClearSpawnTimes();
                Patch_Submarine_SetDepth.Reset();
                OrderDeduplicator.Clear();

                _pendingRngSeed = msg.RngSeed;
                _pendingGameSeconds = msg.GameSeconds;

                if (msg.LoadByName)
                    ApplyByName(msg);
                else
                    ApplyBySaveFile(msg);
            }
            finally
            {
                IsReceiving = false;
            }
        }

        private static void ApplyByName(SessionSyncMessage msg)
        {
            string missionPath = msg.MissionFileName;

            if (!File.Exists(missionPath))
            {
                string fileName = Path.GetFileName(missionPath);
                string resolved = Singleton<FileManager>.Instance?.GetFile(
                    "missions/" + fileName, null) ?? "";
                if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                    missionPath = resolved;
                else
                {
                    Log.LogWarning($"[Session] Cannot find mission locally: {msg.MissionFileName}");
                    return;
                }
            }

            Globals.currentMissionFilePath = missionPath;
            SceneLoading = true;
            DoUnloadAndLoad();
            Log.LogInfo($"[Session] Loading mission by name: {Path.GetFileName(missionPath)}");
        }

        private static void ApplyBySaveFile(SessionSyncMessage msg)
        {
            string savesDir    = Path.Combine(Application.persistentDataPath, "Saves");
            // Use a separate filename so the host's async SaveGame("MPSession.sav")
            // Task.Run write doesn't overwrite our modified file on the same machine.
            string saveFileName = Plugin.Instance.CfgIsHost.Value ? "MPSession.sav" : "MPSession_client.sav";
            string savePath    = Path.Combine(savesDir, saveFileName);
            string missionDir  = Path.Combine(Application.persistentDataPath, "MPMission");
            string missionPath = Path.Combine(missionDir, msg.MissionFileName);

            Log.LogInfo($"[Session] Writing save to: {savePath}");
            Log.LogInfo($"[Session] Mission file: {missionPath}");

            string patchedSave = msg.SaveFileContent;
            if (!string.IsNullOrEmpty(msg.MissionFileContent))
            {
                Directory.CreateDirectory(missionDir);
                File.WriteAllText(missionPath, msg.MissionFileContent);
                Log.LogInfo($"[Session] Wrote mission file ({msg.MissionFileContent.Length} chars)");

                patchedSave = Regex.Replace(
                    patchedSave,
                    @"(?im)^(\s*BaseFile\s*=\s*).*$",
                    m => m.Groups[1].Value + missionPath.Replace("\\", "/"));
            }

            // PvP: swap PlayerTaskforce ↔ EnemyTaskforce so client controls the opposing side
            if (Plugin.Instance.CfgPvP.Value)
            {
                patchedSave = SwapTaskforceSides(patchedSave, "save");

                // Also swap in the mission file so BaseFile reads are consistent
                if (!string.IsNullOrEmpty(msg.MissionFileContent) && File.Exists(missionPath))
                {
                    string swappedMission = SwapTaskforceSides(File.ReadAllText(missionPath), "mission");
                    File.WriteAllText(missionPath, swappedMission);
                }
            }

            Directory.CreateDirectory(savesDir);
            File.WriteAllText(savePath, patchedSave);
            Log.LogInfo($"[Session] Wrote save file ({patchedSave.Length} chars)");

            // Invalidate IniHandler cache so the game reads our modified files from disk
            // instead of returning stale cached data from a previous load.
            IniHandler.invalidateCache();

            Globals.currentMissionFilePath = savePath;
            Log.LogInfo($"[Session] Set Globals.currentMissionFilePath = {savePath}");
            SceneLoading = true;
            Log.LogInfo("[Session] SceneLoading=true, calling MissionManager.DoLoad...");
            DoUnloadAndLoad();
            Log.LogInfo("[Session] MissionManager.DoLoad called — waiting for scene...");
        }

        /// <summary>
        /// Swap PlayerTaskforce ↔ EnemyTaskforce values in an INI-format string.
        /// </summary>
        private static string SwapTaskforceSides(string content, string label)
        {
            var playerMatch = Regex.Match(content, @"(?im)^(\s*PlayerTaskforce\s*=\s*)(.+?)\s*$");
            var enemyMatch  = Regex.Match(content, @"(?im)^(\s*EnemyTaskforce\s*=\s*)(.+?)\s*$");

            if (!playerMatch.Success || !enemyMatch.Success)
            {
                Log.LogWarning($"[Session] PvP: could not find PlayerTaskforce/EnemyTaskforce in {label}");
                return content;
            }

            string playerVal = playerMatch.Groups[2].Value;
            string enemyVal  = enemyMatch.Groups[2].Value;

            content = Regex.Replace(content,
                @"(?im)^(\s*PlayerTaskforce\s*=\s*).+$",
                m => m.Groups[1].Value + enemyVal);
            content = Regex.Replace(content,
                @"(?im)^(\s*EnemyTaskforce\s*=\s*).+$",
                m => m.Groups[1].Value + playerVal);

            Log.LogInfo($"[Session] PvP: swapped sides in {label} — Player={enemyVal}, Enemy={playerVal}");
            return content;
        }

        // ── Scene load helpers ────────────────────────────────────────────────

        /// <summary>
        /// If already in-game, unload the old scene before loading the new one.
        /// Uses the game's own DoUnload() → DoLoad() pipeline to properly tear down
        /// terrain, listeners, textures, and ObjectsManager before loading the new scene.
        /// Without this, loading scene 2 on top of an existing scene 2 causes NREs.
        /// </summary>
        private static void DoUnloadAndLoad()
        {
            if (_inGame)
            {
                // Already in-game: use the game's proper unload-then-load path.
                // DoUnload (99999) tears down terrain/textures, unloads scene 2, and
                // triggers Resources.UnloadUnusedAssets in SceneManager_missionUnloaded.
                // ClearAudioManager (99998) runs AFTER DoUnload to destroy the persistent
                // EnvironmentAudioManager whose _mixer reference goes stale. Must happen
                // AFTER DoUnload so the AudioMixer asset isn't garbage-collected by
                // UnloadUnusedAssets (it's still referenced while the instance lives).
                // Do NOT clear TerrainManager — DoLoad() captures its WaitForDemData/
                // WaitForTerrainChunks coroutines eagerly, and destroying the instance
                // would make those coroutines hang forever.
                Log.LogInfo("[Session] In-game reload: unloading old scene first");
                _inGame = false; // will be set true again in OnSceneReady
                MissionManager.DoLoad(new List<LoadAction>
                {
                    new LoadAction(99999, "UnloadOldMission", MissionManager.DoUnload(), 1),
                    new LoadAction(99998, "ClearAudioManager", ClearAudioManagerCoroutine(), 1),
                });
            }
            else
            {
                // Loading from menu: no scene to unload, but clear stale singletons
                Log.LogInfo("[Session] Loading from menu: no scene to unload");
                ClearPersistentSingletons();
                MissionManager.DoLoad(null);
            }
        }

        /// <summary>
        /// Call setInitialized() on all units after save-file load.
        /// The game's save-load path doesn't reliably call setInitialized(),
        /// leaving _canUpdate=false which gates OnFixedUpdate — ships won't move.
        /// This is idempotent (just sets _canUpdate=true).
        /// </summary>
        private static void InitializeAllUnits()
        {
            int count = 0;
            foreach (var v in UnitRegistry.Vessels)
            { if (v != null) { v.setInitialized(); count++; } }
            foreach (var s in UnitRegistry.Submarines)
            { if (s != null) { s.setInitialized(); count++; } }
            foreach (var a in UnitRegistry.AircraftList)
            { if (a != null) { a.setInitialized(); count++; } }
            foreach (var h in UnitRegistry.Helicopters)
            { if (h != null) { h.setInitialized(); count++; } }
            Log.LogInfo($"[Session] Called setInitialized() on {count} units (_canUpdate=true)");
        }

        /// <summary>
        /// Destroy persistent singletons that carry stale Unity references
        /// across scene transitions. Nulls the static _instance field via
        /// reflection so the new scene's Awake() creates a fresh instance.
        /// </summary>
        private static void ClearPersistentSingletons()
        {
            ClearSingleton<EnvironmentAudioManager>();
            ClearSingleton<TerrainManager>();
        }

        /// <summary>
        /// Coroutine that clears only EnvironmentAudioManager. Used as a LoadAction
        /// in the in-game reload path (after DoUnload, before scene reload).
        /// </summary>
        private static IEnumerator ClearAudioManagerCoroutine()
        {
            ClearSingleton<EnvironmentAudioManager>();
            yield return null;
        }

        private static void ClearSingleton<T>() where T : MonoBehaviour
        {
            var field = typeof(Singleton<T>).GetField("_instance",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
            {
                Log.LogWarning($"[Session] Could not find _instance field on Singleton<{typeof(T).Name}>");
                return;
            }

            var instance = field.GetValue(null) as T;
            if (instance != null)
            {
                Log.LogInfo($"[Session] Destroying persistent singleton {typeof(T).Name}");
                Object.Destroy(instance.gameObject);
                field.SetValue(null, null);
            }
            else
            {
                Log.LogInfo($"[Session] Singleton<{typeof(T).Name}> already null, nothing to destroy");
            }
        }

        /// <summary>
        /// Called from Plugin.Update once SceneCreator.IsLoadingDone stays true
        /// for enough frames. Finalizes the scene load.
        /// </summary>
        public static void OnSceneReady()
        {
            if (!SceneLoading)
            {
                Log.LogWarning("[Session] OnSceneReady called but SceneLoading=false, ignoring");
                return;
            }

            Log.LogInfo("[Session] OnSceneReady — finalizing scene load");
            SceneLoading = false;
            _inGame = true;
            DriftDetector.Reset();
            StateApplier.ResetOrphanTracking();
            PvPDeathNotifications.Clear();
            PvPFireAuth.Clear();
            CombatEventHandler.ResetCounters();
            MissileStateSyncHandler.ResetCounters();
            ProjectileIdMapper.Clear();
            OrderDeduplicator.Clear();

            // Defer ID alignment until the first state update from the host arrives.
            // The host has live positions — more accurate than save-file positions —
            // and this completely avoids name-prefix matching issues.
            if (!Plugin.Instance.CfgIsHost.Value)
                StateApplier.SetPendingAlignment();

            // PvP: flush pre-existing engage tasks on enemy puppet units.
            // The save file may contain active engage tasks that bypass all Harmony
            // suppression layers (AddEngageTask, InsertEngageTask) because they're
            // deserialized directly into the unit's weapon queue. Without this,
            // those tasks fire unauthorized missiles that get suppressed by PvPFireAuth,
            // wasting ammo and corrupting the projectile ID mapper.
            if (Plugin.Instance.CfgPvP.Value)
            {
                FlushEnemyEngageTasks();
            }

            // Populate registry as fallback for units that spawned before Harmony patches were active
            UnitRegistry.PopulateFromScene();

            InitializeAllUnits();

            // Restore sub-minute precision the save format drops
            if (_pendingGameSeconds > 0f)
            {
                Singleton<SeaPower.Environment>.Instance.Seconds = _pendingGameSeconds;
                Log.LogInfo($"[Session] Restored Environment.Seconds = {_pendingGameSeconds:F1}");
            }

            // Seed RNG identically to host
            Log.LogInfo($"[Session] Seeding RNG with {_pendingRngSeed}");
            RngSeeder.SeedAll(_pendingRngSeed);

            // Pause locally (host already paused)
            Log.LogInfo($"[Session] Calling GameTime.Pause() — currently paused={GameTime.IsPaused()}, TimeCompression={GameTime.TimeCompression}");
            GameTime.Pause();
            Log.LogInfo($"[Session] After Pause: paused={GameTime.IsPaused()}, TimeCompression={GameTime.TimeCompression}");

            bool isHost = Plugin.Instance.CfgIsHost.Value;
            Log.LogInfo($"[Session] IsHost={isHost}, IsConnected={NetworkManager.Instance.IsConnected}");

            // PvP post-load: clear detection data so sides must re-detect through sensors
            if (Plugin.Instance.CfgPvP.Value && !isHost)
            {
                ClearDetectionData();
            }

            // Center camera on first player unit (fixes PvP camera starting on wrong side)
            if (!isHost)
            {
                CenterCameraOnPlayerUnit();
            }

            if (!isHost)
            {
                // Notify host we're ready
                SimSyncManager.CurrentState = SimState.Synchronized;
                Log.LogInfo($"[Session] SimState set to {SimSyncManager.CurrentState}");
                NetworkManager.Instance.SendToServer(new SessionReadyMessage { IsReady = true });
                Log.LogInfo("[Session] Sent SessionReady to host — waiting for unpause");
            }
            else
            {
                Log.LogInfo("[Session] Host scene ready — paused, unpause to start");
            }
        }

        /// <summary>
        /// PvP: call CeaseFire on all enemy puppet units to flush any engage tasks
        /// that were deserialized from the save file. These tasks bypass Harmony patches
        /// (AddEngageTask/InsertEngageTask) because they're restored directly into the
        /// weapon system queue, leading to unauthorized missile spawns.
        /// </summary>
        private static void FlushEnemyEngageTasks()
        {
            int flushed = 0;
            foreach (var obj in UnitRegistry.All)
            {
                if (obj == null) continue;
                if (obj._taskforce == Globals._playerTaskforce) continue;
                if (obj.IsDestroyed) continue;

                // CeaseFire clears all active engage tasks and weapon system queues.
                // Args: report=false (no radio chatter), clearEngageTasks=true,
                // clearWeapons=true, clearSonar=false, clearAutoAttack=true, clearGuns=true
                OrderHandler.ApplyingFromNetwork = true;
                try { obj.CeaseFire(false, true, true, false, true, true); }
                finally { OrderHandler.ApplyingFromNetwork = false; }
                flushed++;
            }
            Log.LogInfo($"[Session] PvP: flushed engage tasks on {flushed} enemy puppet units");
        }

        /// <summary>
        /// PvP: clear all pre-existing detection/contact data so sides must re-detect
        /// through sensors. Runs on the client after loading the swapped save file.
        /// Without this, the client inherits the Enemy AI's full sensor intel about the
        /// host's units, giving the client perfect knowledge of enemy positions.
        /// </summary>
        private static void ClearDetectionData()
        {
            int totalSpotted = 0;
            int totalContacts = 0;

            if (!Singleton<TaskforceManager>.InstanceExists(false))
            {
                Log.LogWarning("[Session] PvP: TaskforceManager not available, skipping detection clear");
                return;
            }

            foreach (var tf in Singleton<TaskforceManager>.Instance._taskForces)
            {
                // Clear spotted objects list
                totalSpotted += tf._spottedObjects.Count;
                tf._spottedObjects.Clear();

                // Clear foreign contacts from PlottingTable (keep own-unit entries)
                var pt = tf.PlottingTable;
                if (pt == null) continue;

                var foreignContacts = pt.LocalVehicles
                    .Where(kvp => kvp.Key != null && kvp.Key._taskforce != tf)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var obj in foreignContacts)
                {
                    if (pt.LocalVehicles.TryGetValue(obj, out var vehicle))
                    {
                        vehicle.NotifyDeletion();
                        pt.Vehicles.Remove(vehicle);
                        pt.LocalVehicles.Remove(obj);
                        totalContacts++;
                    }
                }
            }

            Log.LogInfo($"[Session] PvP: cleared {totalSpotted} spotted objects and {totalContacts} foreign contacts");
        }

        /// <summary>
        /// Center camera on first player vessel after scene load.
        /// Replicates ObjectsManager.SetInitialActiveObject logic to ensure the camera
        /// is on the correct side after PvP side swap.
        /// </summary>
        private static void CenterCameraOnPlayerUnit()
        {
            var objMgr = Singleton<ObjectsManager>.Instance;
            if (objMgr == null) return;

            // Find first player vessel (prefer Vessel, fall back to any player unit)
            ObjectBase target = null;
            foreach (var v in UnitRegistry.Vessels)
            {
                if (v != null && v._taskforce == Globals._playerTaskforce)
                {
                    target = v;
                    break;
                }
            }
            if (target == null)
            {
                // Try submarines
                foreach (var s in UnitRegistry.Submarines)
                {
                    if (s != null && s._taskforce == Globals._playerTaskforce)
                    {
                        target = s;
                        break;
                    }
                }
            }

            if (target == null)
            {
                Log.LogWarning("[Session] No player unit found for camera centering");
                return;
            }

            objMgr.setActiveObject(target);
            Singleton<CameraManager>.Instance.setDistanceToPivot(target.getDefaultCameraDistanceToTarget());
            Singleton<RenderPosition>.Instance.switchToObject(target, false, false, true);
            Singleton<RenderPosition>.Instance.setGlobalCurrentTilePos(target.getGeoPosition());
            Singleton<RenderPosition>.Instance.setGlobalPosition(target.getGeoPosition(), true);

            if (Globals._mainGameViewModel?.Map?.DisplayMap != null)
                Globals._mainGameViewModel.Map.DisplayMap.Center = target.Position.Value;

            Log.LogInfo($"[Session] Camera centered on player unit: {target.name}");
        }
    }
}
