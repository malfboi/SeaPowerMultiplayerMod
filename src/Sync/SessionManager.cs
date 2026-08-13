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

        /// <summary>
        /// True when a mission scene is loaded and has finished loading.
        ///
        /// Everything a capture sends comes from a live scene: SaveGame writes the
        /// current mission, UnitRegistry reads it, Environment.Seconds is its clock.
        /// In the main menu none of that exists, so the host must not start a sync
        /// from there. Detected directly rather than tracked as a flag, for the same
        /// reason DoUnloadAndLoad does it - a mission the player loaded on their own
        /// never went through any of our code.
        /// </summary>
        public static bool MissionIsLive
            => Singleton<SceneCreator>.InstanceExists(false)
               && Singleton<SceneCreator>.Instance.IsLoadingDone;

        private static int _pendingRngSeed;
        private static float _pendingGameSeconds;

        /// <summary>When > 0, a resync retry is pending. Counts down each Update frame.</summary>
        private static float _retrySendAt;
        private static int _retryCount;
        private const int MaxRetries = 3;
        private const float RetryDelaySec = 2f;

        // Save-completion wait. SaveGame() returns as soon as the synchronous part
        // of the save is done and leaves the rest on a fire-and-forget Task, so the
        // capture has to wait for that Task before reading anything. See
        // AwaitingSaveCompletion for why.
        /// <summary>Slot this capture is for, or NoSender for a whole-session sync.</summary>
        private static byte _pendingOnlySlot = PlayerRegistry.NoSender;

        /// <summary>False when this capture is a mid-mission join rather than a new battle.</summary>
        private static bool _pendingSessionBoundary = true;

        /// <summary>Time compression to restore once the joiner is in. Sampled before the
        /// pause; 0 means "it was already paused, leave it that way".</summary>
        private static float _resumeTimeCompression;

        /// <summary>Deadline for a mid-mission joiner to finish loading, or 0 when nobody
        /// is joining. Without it one stuck joiner holds every other player at a paused
        /// screen forever, with no way out but a disconnect.</summary>
        private static float _joinDeadline;

        private const float JoinTimeoutSec = 180f;

        private static string? _pendingSavePath;
        private static System.DateTime _saveWriteTimeBefore;
        private static float _saveWaitDeadline;
        private const float SaveWaitTimeoutSec = 10f;

        private static ManualLogSource Log => Plugin.Log;

        // ── Host side ─────────────────────────────────────────────────────────

        /// <summary>Called from Plugin.Update() to check for pending resync retries.</summary>
        public static void TickRetry()
        {
            TickPendingSave();
            TickJoinDeadline();

            if (_retrySendAt <= 0f) return;
            if (Time.unscaledTime < _retrySendAt) return;
            _retrySendAt = 0f;
            Log.LogInfo($"[Session] Retry #{_retryCount}/{MaxRetries} — re-sending session sync");
            CaptureAndSend();
        }

        /// <summary>
        /// Host: a mid-mission joiner has taken too long. Resume without them rather than
        /// hold everybody else at a paused screen indefinitely - the failure mode this
        /// replaces has no exit at all except somebody disconnecting.
        /// </summary>
        private static void TickJoinDeadline()
        {
            if (_joinDeadline <= 0f) return;
            if (SimSyncManager.AllReady)
            {
                _joinDeadline = 0f;
                HostFinishJoin();
                return;
            }
            if (Time.unscaledTime < _joinDeadline) return;

            _joinDeadline = 0f;
            Log.LogError($"[Session] Joining player did not finish loading within {JoinTimeoutSec:F0}s — resuming without them.");
            SimSyncManager.ReportIssue(
                "A joining player never finished loading.",
                "The session has resumed without them; they can try again.",
                warning: true);
            HostFinishJoin();
        }

        /// <summary>Host: everyone who is coming is in. Re-send the world to whoever just
        /// arrived, then let time run again.</summary>
        private static void HostFinishJoin()
        {
            // Their picture starts empty while every host-side delta table believes it is
            // already up to date, so without this the joiner sits looking at stale or
            // missing UI on several channels until each one's next full sweep.
            HostStreamers.ForceFullResend();

            if (_resumeTimeCompression > 0f)
            {
                Log.LogInfo($"[Session] Join complete — resuming at x{_resumeTimeCompression}.");
                TimeSyncManager.HostBroadcastResume(_resumeTimeCompression);
            }
            else
            {
                Log.LogInfo("[Session] Join complete — session was paused, leaving it paused.");
            }
            _resumeTimeCompression = 0f;
        }

        /// <summary>
        /// Waits for the game to finish writing the save before the capture reads it.
        ///
        /// WriteMissionToFile ends with a fire-and-forget
        /// <c>Task.Run(() =&gt; WriteMissionToSaveAsyncParts(...))</c>, which awaits
        /// ScriptRuntime.SerializeToIni (adding the [Scripts] and [Scripting]
        /// sections, with a 1 s internal timeout) and only then calls
        /// <c>ini.saveFile()</c>. SaveGame() returns before any of that runs, so
        /// capturing straight after it silently shipped every save without its
        /// mission scripting state — triggers and objectives simply went missing on
        /// the client.
        ///
        /// The Task is never stored anywhere, so the only observable completion
        /// signal is saveFile() landing on disk. We wait for the file's write time
        /// to advance and then read from the IniHandler cache as before — the cache
        /// is the same instance the async part mutated, so this needs the timestamp
        /// only as a done-flag and never reads a half-written file.
        /// </summary>
        private static void TickPendingSave()
        {
            if (_pendingSavePath == null) return;

            bool written = File.Exists(_pendingSavePath)
                        && File.GetLastWriteTimeUtc(_pendingSavePath) > _saveWriteTimeBefore;

            if (!written)
            {
                if (Time.unscaledTime < _saveWaitDeadline) return;
                // Fall through and send anyway: a save missing its scripting state
                // still beats refusing to sync at all.
                Log.LogWarning($"[Session] Save did not finish writing within {SaveWaitTimeoutSec}s — " +
                               "sending anyway; mission scripting state may be missing.");
            }

            string savePath = _pendingSavePath;
            _pendingSavePath = null;
            SendCapturedSave(savePath);
        }

        /// <summary>
        /// The client's mission load never finished. Clears the loading flag so the
        /// session stops waiting on a scene that is not coming, and puts the reason
        /// in front of the player — a dead LoadMission coroutine otherwise looks
        /// exactly like a slow one, forever.
        /// <paramref name="timeoutSec"/> is negative when the load was declared dead
        /// outright (an exception unwound out of the coroutine) rather than timing out.
        /// </summary>
        public static void OnSceneLoadStalled(string? loadException, float timeoutSec)
        {
            SceneLoading = false;
            Log.LogError((timeoutSec < 0f
                             ? "[Session] Mission load failed — the game's load coroutine threw and stopped."
                             : $"[Session] Mission load did not complete within {timeoutSec}s — giving up.") +
                         (loadException != null ? $" Exception: {loadException}" : ""));

            SimSyncManager.ReportIssue(
                "LOAD FAILED — the mission never finished loading.",
                loadException != null
                    ? $"The game threw: {loadException} Ask the host to press Send again; if it repeats, check both players are on the same Sea Power build."
                    : "Ask the host to press Send again. If it repeats, check that both players are on the same Sea Power build.");
            SimSyncManager.Reset();
        }

        /// <summary>Full session sync to EVERY player - the "Send State &amp; Wait" button.
        /// This is a session boundary: it starts a new battle for everyone.</summary>
        public static void CaptureAndSend() => BeginCapture(sessionBoundary: true, onlySlot: PlayerRegistry.NoSender);

        /// <summary>
        /// Bring ONE player into a battle already in progress.
        ///
        /// Everyone else keeps playing their current session - no reload, no lost
        /// ledger, no reseeded RNG. See <see cref="BeginCapture"/> for exactly which
        /// resets are skipped and why each one would hurt.
        /// </summary>
        public static void CaptureAndSendTo(byte slot)
        {
            if (slot == PlayerRegistry.NoSender) return;
            Log.LogInfo($"[Session] Mid-mission join: capturing for slot {slot}.");
            BeginCapture(sessionBoundary: false, onlySlot: slot);
        }

        private static void BeginCapture(bool sessionBoundary, byte onlySlot)
        {
            // Checked before anything else, and before any state is touched. The
            // old path went ahead from the main menu: it paused, set
            // WaitingForClient, saved a session with no mission in it, and shipped
            // that to the client - leaving the host on "Waiting for client to
            // load..." and the client waiting for a mission that never comes.
            // Neither side had any way back except a disconnect.
            if (!MissionIsLive)
            {
                Log.LogWarning("[Session] CaptureAndSend skipped — no mission loaded");
                SimSyncManager.ReportIssue(
                    "No mission loaded — nothing to send.",
                    "Start or load a mission first, then press Send State & Wait.",
                    // A warning, not an error: the player has done nothing wrong,
                    // they are just early. Errors here would also colour the banner
                    // red and count against the diagnostics error rate.
                    warning: true);
                return;
            }

            if (_pendingSavePath != null)
            {
                Log.LogWarning("[Session] CaptureAndSend skipped — a save is still being written");
                return;
            }

            if (SceneLoading)
            {
                Log.LogWarning("[Session] CaptureAndSend skipped — SceneLoading=true");
                SimSyncManager.ReportIssue(
                    "Sync skipped — a scene is still loading.",
                    "Wait for the mission to finish loading, then press Send again.");
                return;
            }

            // Fresh attempt (not a retry) starts with a clean slate
            if (_retryCount == 0)
                SimSyncManager.ClearIssue();

            Log.LogInfo("[Session] CaptureAndSend starting...");

            _pendingOnlySlot = onlySlot;
            _pendingSessionBoundary = sessionBoundary;

            // A full re-sync supersedes any join in flight: everyone is about to reload
            // anyway, so the join's own resume must not fire on top of it.
            if (sessionBoundary) _joinDeadline = 0f;

            // Remember what to go back to. Read BEFORE the pause, or we resume into a
            // paused game and the players who never left are frozen indefinitely.
            _resumeTimeCompression = GameTime.IsPaused() ? 0f : GameTime.TimeCompression;

            // Freeze the players already in FIRST, so they stop where the save is taken
            // rather than running on while it is written and then being rewound by the
            // stream. Only matters for a mid-mission join - a session boundary reloads
            // everyone anyway.
            if (!sessionBoundary) TimeSyncManager.HostBroadcastPause();

            // Pause and set sync state before saving
            SceneLoading = true; // suppress broadcasts during pause+save
            Log.LogInfo("[Session] Pausing game...");
            GameTime.Pause();

            if (sessionBoundary)
            {
                SimSyncManager.Reset();
                SimSyncManager.CurrentState = SimState.WaitingForClient;
            }
            else
            {
                // Host stays Synchronized. WaitingForClient gates HostEntityStreamer, so
                // dropping into it would switch the entity stream off for the players
                // already in the battle while the joiner loads.
                SimSyncManager.OnPeerJoined(onlySlot);
            }
            SceneLoading = false; // host isn't actually loading a scene

            // Host-local and idempotent - safe either way.
            UnitRegistry.Clear();
            UnitRegistry.PopulateFromScene();

            // SESSION-BOUNDARY ONLY. Each of these is a "new battle" operation, and each
            // would take something away from the players still in the old one:
            //   OrderDeduplicator     - clearing it lets already-sent orders re-send.
            //   HandleEngageTasks /
            //   Submarine.SetDepth    - hold live per-unit ownership locks.
            //   StateApplier orphans  - re-arms orphan purging mid-battle.
            if (sessionBoundary)
            {
                StateApplier.ResetOrphanTracking();
                Patch_ObjectBase_HandleEngageTasks.Reset();
                Patch_Submarine_SetDepth.Reset();
                OrderDeduplicator.Clear();
            }

            // Capture state is scoped to a SESSION, not to the connection. Its only
            // other Clear() is on peer disconnect, and two battles played in one lobby
            // never disconnect - so the census went on advertising the previous
            // mission's SpawnLedger into the new one, the client asked for the ids it
            // could not resolve, and HandleDiffRequest replayed last battle's units,
            // weapons and sonobuoys into a mission they were never part of. Ids are
            // allocated deterministically, so most stale ids collided with live ones and
            // were masked; the visible ghosts were the ids the new battle did not reuse.
            //
            // Here rather than in OnSceneReady, which the suggested fix named: the host
            // never reaches OnSceneReady. It sets SceneLoading true and false again a
            // few lines above without ever loading a scene, so OnSceneReady's guard
            // would log "SceneLoading=false, ignoring" and return - and SpawnLedger is
            // host-only state (HostCaptureActive requires CfgIsHost). CaptureAndSend IS
            // the host's session boundary, which is why every other per-session reset is
            // already in this block.
            // SESSION-BOUNDARY ONLY, and this is the single most dangerous block in the
            // whole mid-mission-join feature.
            //
            // CaptureState.SpawnLedger is what a census diff request is answered from.
            // Clearing it while other people are playing does not merely waste work - it
            // destroys the only record of every weapon currently in the air, so the next
            // self-heal from ANY existing player gets nothing back and their missiles
            // quietly stop being replayable. Same story for the hatch state and the
            // census sequence. None of it may run for a joiner.
            if (sessionBoundary)
            {
                CaptureState.Clear();
                HatchStateCapture.Clear();
                Patch_V2_MissionEnd_Capture.Reset();
                EntityCensusManager.Reset();

                // Immediately after the clear, and only ever after it: put back the ledger
                // entries for rounds that are in the air at this instant, so a mid-battle
                // resync does not lose every missile and torpedo already flying. See
                // WeaponLedgerRebuild for why the clear itself has to stay.
                WeaponLedgerRebuild.Run();
                FlightDeckStreamer.Reset();
                FlightDeckStateApplier.Reset();

                // Flush stale engage tasks on the opposing side's units so a human
                // opponent's save-restored tasks don't fire without their say-so. Only when
                // somebody is actually sitting on that side - against the AI those tasks are
                // the AI's and should stand.
                //
                // Never for a joiner: mid-battle this would cancel live engagements that
                // an opponent who is already playing has ordered. Someone taking over a
                // fleet mid-mission inherits whatever is in flight, which is the right
                // answer anyway.
                if (Teams.ContestedSession)
                    FlushEnemyEngageTasks();
            }

            // SaveGame does not check IsSavingAllowed, but set it true to be safe
            bool wasAllowed = SaveLoadManager.IsSavingAllowed;
            SaveLoadManager.IsSavingAllowed = true;

            Log.LogInfo("[Session] Saving game to MPSession.sav...");

            // Sampled before the save so TickPendingSave can tell the game's async
            // write apart from whatever the previous sync left on disk.
            string plannedPath = SaveLoadManager.GetSaveFilePath("MPSession.sav");
            _saveWriteTimeBefore = File.Exists(plannedPath)
                ? File.GetLastWriteTimeUtc(plannedPath)
                : System.DateTime.MinValue;

            string savePath = SaveLoadManager.SaveGame("MPSession.sav");

            SaveLoadManager.IsSavingAllowed = wasAllowed;

            if (string.IsNullOrEmpty(savePath))
            {
                Log.LogWarning("[Session] SaveGame returned empty path — aborting sync.");
                SimSyncManager.ReportIssue(
                    "Sync failed — the game could not save the session.",
                    "Check that the Saves folder is writable, then press Send again.");
                SimSyncManager.Reset();
                return;
            }

            Log.LogInfo($"[Session] Save path: {savePath}");

            // The save is only half-written at this point - hand off to
            // TickPendingSave, which resumes at SendCapturedSave once it lands.
            _pendingSavePath  = savePath;
            _saveWaitDeadline = Time.unscaledTime + SaveWaitTimeoutSec;
        }

        /// <summary>
        /// Second half of the capture, resumed by TickPendingSave once the game has
        /// finished writing the save.
        /// </summary>
        private static void SendCapturedSave(string savePath)
        {
            // Read save data from the IniHandler cache rather than the file: it is
            // the same instance the game just finished populating, so it needs no
            // parse and cannot catch a partially flushed write.
            var ini = IniHandler.get(savePath);
            if (ini?.Data == null || ini.Data.Count == 0)
            {
                Log.LogWarning("[Session] IniHandler cache empty for save — aborting sync.");
                SimSyncManager.ReportIssue(
                    "Sync failed — the session save came back empty.",
                    "Press Send again; if it repeats, reload the mission.");
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

            // Campaign missions save TWO files: the .sav plus a companion
            // "<save>_campaign.ini" holding mission unlock/complete flags and
            // campaign persistent data. The save references it via
            // File/LinearCampaignSavePath, and SceneCreator refuses to load the
            // mission without it. SaveCampaign() runs synchronously inside
            // WriteMissionToFile (before the .sav's own async write), so it is
            // already on disk here - read it raw to keep its "#!alias" first line,
            // which is what points back at the authored campaign.
            string campaignFileName    = "";
            string campaignFileContent = "";

            if (saveContent.IndexOf("LinearCampaignSavePath", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string campaignPath = SaveLoadManager.GetCampaignSaveFilePathFromMissionSaveFilePath(savePath);
                if (File.Exists(campaignPath))
                {
                    campaignFileName    = Path.GetFileName(campaignPath);
                    campaignFileContent = File.ReadAllText(campaignPath);
                    Log.LogInfo($"[Session] Campaign save: {campaignFileName} ({campaignFileContent.Length} chars)");
                }
                else
                {
                    Log.LogWarning($"[Session] Save references a campaign but {campaignPath} is missing — the client will not be able to load it.");
                }
            }

            float gameSeconds = Singleton<SeaPower.Environment>.Instance.Seconds;

            var msg = new SessionSyncMessage
            {
                SaveFileContent     = saveContent,
                MissionFileName     = missionFileName,
                MissionFileContent  = missionFileContent,
                RngSeed             = rngSeed,
                GameSeconds         = gameSeconds,
                HostTimeVoteEnabled = Plugin.Instance.CfgTimeVote.Value,
                CampaignFileName    = campaignFileName,
                CampaignFileContent = campaignFileContent,
            };

            bool toOne = _pendingOnlySlot != PlayerRegistry.NoSender;
            Log.LogInfo($"[Session] Sending SessionSync to {(toOne ? $"slot {_pendingOnlySlot}" : "all players")}: " +
                        $"save={saveContent.Length}ch, mission={missionFileName} ({missionFileContent.Length}ch), rngSeed={rngSeed}");

            // Unicast for a joiner. Broadcasting a multi-megabyte save would push every
            // existing player through a full reload of a session they are already in.
            if (toOne) NetworkManager.Instance.SendToSlot(_pendingOnlySlot, msg, DeliveryMethod.ReliableOrdered);
            else NetworkManager.Instance.BroadcastToClients(msg, DeliveryMethod.ReliableOrdered);

            if (NetworkManager.Instance.LastSendFailed)
            {
                string reason = NetworkManager.Instance.LastSendError ?? "the transport rejected the message";

                if (_retryCount < MaxRetries)
                {
                    _retryCount++;
                    _retrySendAt = Time.unscaledTime + RetryDelaySec;
                    Log.LogWarning($"[Session] Send failed — scheduling retry #{_retryCount}/{MaxRetries} in {RetryDelaySec}s");
                    SimSyncManager.ReportIssue(
                        $"Sync send failed — retrying ({_retryCount}/{MaxRetries})...",
                        reason,
                        warning: true);
                    return;
                }
                else
                {
                    Log.LogError($"[Session] Send failed after {MaxRetries} retries — session sync could not be delivered. Save may be too large ({saveContent.Length} chars).");
                    _retryCount = 0;
                    SimSyncManager.ReportIssue(
                        "SYNC FAILED — the other player did NOT receive this game.",
                        $"{reason} Save was {saveContent.Length / 1024} KB. Press Send again or restart the mission.");
                    SimSyncManager.Reset();
                    return;
                }
            }

            // Success - reset retry counter
            _retryCount = 0;
            SimSyncManager.ClearIssue();

            // The host is the first player on Blue, so it takes Blue's formations under
            // the same rule as anyone else. Idempotent - only ever claims units nobody
            // holds - so running it on every capture is safe.
            FormationOwnership.HostGrantTeamTo(Team.Blue, 0);

            if (_pendingSessionBoundary)
            {
                // Seed host RNG to match what the clients will use.
                RngSeeder.SeedAll(rngSeed);
            }
            else
            {
                // NOT for a joiner. Reseeding mid-battle would jolt the RNG under the
                // players already in - and it buys nothing: under unified host authority
                // the guest's own RNG only drives cosmetics, so host and joiner diverging
                // there is invisible. The joiner still seeds itself from the message.
                _joinDeadline = Time.unscaledTime + JoinTimeoutSec;
                Log.LogInfo($"[Session] Waiting up to {JoinTimeoutSec:F0}s for slot {_pendingOnlySlot} to load.");
            }

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
            Log.LogInfo($"[Session] Received SessionSync: loadByName={msg.LoadByName}, mission={msg.MissionFileName}, save={msg.SaveFileContent?.Length ?? 0}ch, rngSeed={msg.RngSeed}, hostTimeVote={msg.HostTimeVoteEnabled}");
            IsReceiving = true;

            TimeSyncManager.SetHostVoteMode(msg.HostTimeVoteEnabled);

            try
            {
                // Clear state from previous session
                UnitRegistry.Clear();
                Patch_Vehicle_UpdateAllData_PvP.ClearCache();
                Patch_ObjectBase_HandleEngageTasks.Reset();
                Patch_Submarine_SetDepth.Reset();
                OrderDeduplicator.Clear();                
                FlightDeckStateApplier.Reset();

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
                    SimSyncManager.ReportIssue(
                        "SYNC FAILED — mission not installed on this PC.",
                        $"The host is playing \"{msg.MissionFileName}\". Install it and ask them to press Send again.");
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

            // Campaign missions need the companion "<save>_campaign.ini" next to the
            // save. GetCampaignSaveFilePath resolves a bare LinearCampaignSavePath
            // against the save's own directory, so write it under OUR save's name and
            // repoint the key - the host's value is either its own filename or an
            // absolute host path, neither of which resolves here.
            if (!string.IsNullOrEmpty(msg.CampaignFileContent))
            {
                string campaignPath = SaveLoadManager.GetCampaignSaveFilePathFromMissionSaveFilePath(savePath);
                Directory.CreateDirectory(savesDir);
                File.WriteAllText(campaignPath, msg.CampaignFileContent);
                Log.LogInfo($"[Session] Wrote campaign save: host's {msg.CampaignFileName} -> {campaignPath} ({msg.CampaignFileContent.Length} chars)");

                patchedSave = Regex.Replace(
                    patchedSave,
                    @"(?im)^(\s*LinearCampaignSavePath\s*=\s*).*$",
                    m => m.Groups[1].Value + Path.GetFileName(campaignPath));
            }
            else
            {
                // No companion file arrived - either it was missing next to the host's
                // save at capture time, or the campaign is one this machine does not
                // have at all. The linkage still rides in the save, and SceneCreator
                // only checks that the key is non-empty
                // (TrySetAndCheckLinearCampaignExistenceForMission) before loading the
                // campaign scene and initialising a campaign that isn't there - which
                // throws and takes the whole mission load with it. Clear both keys so
                // the battle loads standalone; nothing in a multiplayer session reads
                // campaign progression anyway.
                string standalone = Regex.Replace(
                    patchedSave,
                    @"(?im)^(\s*(?:LinearCampaignSavePath|LinearCampaignEventName)\s*=\s*).*$",
                    m => m.Groups[1].Value);

                if (standalone != patchedSave)
                    Log.LogWarning("[Session] Save is campaign-linked but no campaign file came with it — " +
                                   "cleared the linkage, the mission loads standalone.");

                patchedSave = standalone;
            }

            // A RED guest swaps PlayerTaskforce ↔ EnemyTaskforce so it commands the
            // opposing side. A BLUE guest is on the host's own side and loads the save
            // exactly as the host wrote it - which is what makes two Blue guests
            // co-operate with no extra machinery.
            if (Teams.LocalSaveSwapped)
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
            // Detect a live scene directly (same signal OnSceneReady waits on)
            // instead of tracking an MP-side flag: a scenario the player loaded
            // on their own never went through OnSceneReady, and taking the menu
            // branch with a scene live destroys TerrainManager mid-mission -
            // AutogenManager then NREs every LateUpdate on the blank
            // auto-created replacement (_biomesName is null until init()).
            if (MissionIsLive)
            {
                // Already in-game: use the game's proper unload-then-load path.
                // DoUnload (99999) tears down terrain/textures, unloads scene 2, and
                // triggers Resources.UnloadUnusedAssets in SceneManager_missionUnloaded.
                // ClearAudioManager (99998) runs AFTER DoUnload to destroy the persistent
                // EnvironmentAudioManager whose _mixer reference goes stale. Must happen
                // AFTER DoUnload so the AudioMixer asset isn't garbage-collected by
                // UnloadUnusedAssets (it's still referenced while the instance lives).
                // Do NOT clear TerrainManager - DoLoad() captures its WaitForDemData/
                // WaitForTerrainChunks coroutines eagerly, and destroying the instance
                // would make those coroutines hang forever.
                Log.LogInfo("[Session] In-game reload: unloading old scene first");
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
        /// leaving _canUpdate=false which gates OnFixedUpdate - ships won't move.
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
            StateApplier.ResetOrphanTracking();
            Patch_Vehicle_UpdateAllData_PvP.ClearCache();
            OrderDeduplicator.Clear();

            // Defer ID alignment until the first state update from the host arrives.
            // The host has live positions - more accurate than save-file positions -
            // and this completely avoids name-prefix matching issues.
            if (!Plugin.Instance.CfgIsHost.Value)
            {
                UnitReplicaDriver.SetPendingAlignment(); // v2 unit stream runs the alignment

                // The client half of the same session scoping the host does in
                // CaptureAndSend. _missCount is the one that bites: two consecutive
                // censuses without an id REMOVE the local replica, so counts carried
                // over from the previous battle can evict a replica this one just
                // spawned. _lastRequestSeq would also suppress a legitimate first
                // request for an id the last battle had already asked about.
                EntityCensusManager.Reset();

                // v2: save files contain in-flight weapons and the load relaunches
                // them LIVE - demote them all to inert replicas (host streams them)
                SpawnReplicator.DemoteLoadedWeapons();

                // The one-shot UID rebase that used to sit here is gone. It ran after the
                // load had settled, but the guest allocates ids all the way THROUGH a
                // load - the weapon pool alone takes hundreds - so the objects that
                // actually collided were already numbered by the time it fired, and
                // SceneCreator reassigns _UID from the save partway through a load
                // regardless. GuestIdFloor holds the same floor on the allocator itself,
                // armed at Welcome and re-checked per call.
            }

            // PvP: flush pre-existing engage tasks on the remote player's units.
            // The save file may contain active engage tasks that bypass the Harmony
            // suppression layers (AddEngageTask, InsertEngageTask) because they're
            // deserialized directly into the unit's weapon queue - the remote
            // player's units must not fire without their say-so. Gated on somebody
            // actually being on that side - against the AI those tasks are legitimate.
            if (Teams.ContestedSession)
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

            // Post-load: an opposing player must re-detect through its own sensors rather
            // than inherit the host's picture. Keyed on the save swap, which is exactly
            // "I am on the other side from the host" - a BLUE guest is the host's
            // teammate and keeps the shared picture.
            if (Teams.LocalSaveSwapped)
            {
                ClearDetectionData();
            }

            // The session loads paused, and the plotting refresh that puts own units on
            // the map is pause-gated - seed it here or the map stays blank until the
            // host unpauses. After ClearDetectionData, which rewrites the same table.
            PlotOwnUnitsNow("scene ready");

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
                ReconnectManager.OnLocalResyncComplete();
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
        /// Push every player-taskforce unit's own truth track onto the plotting table.
        ///
        /// The tactical map plots Globals._playerTaskforce.PlottingTable.Vehicles
        /// (SeapowerUI.MapKnownUnits), and a unit only enters that table when its
        /// self-track is pushed. The routine that does that for a whole side is
        /// Taskforce.updateTaskforceContacts, whose ONLY caller is
        /// TaskforceManager.OnUpdate - which opens with
        /// <c>if (GameTime.IsPaused()) return;</c>. A received session loads paused and
        /// stays paused until the host unpauses, so the side that arrived through the
        /// PvP save swap has no own-unit entries and the guest's map is blank until the
        /// first unpause.
        ///
        /// Safe to call while paused: the self-track is built by OwnSideSensor.MakeTruth
        /// straight off the unit's transform, not from the ECS geo sync that the paused
        /// update would otherwise have run first. Idempotent - pushing a truth track for
        /// a unit already in the table updates it in place, which is exactly what the
        /// unpaused cadence does every DataLinkUpdateRate seconds.
        ///
        /// Calling the game's own per-taskforce routine rather than looping
        /// UpdateOwnPlottingState() by hand: it already skips wakebubbles and chaff and
        /// follows up with the single PlottingTable.Update the per-unit calls do not do.
        /// </summary>
        public static void PlotOwnUnitsNow(string reason)
        {
            var tf = Globals._playerTaskforce;
            if (tf == null || tf.PlottingTable == null)
            {
                Log.LogWarning($"[Session] PlotOwnUnitsNow ({reason}): no player taskforce plotting table");
                return;
            }

            tf.updateTaskforceContacts();
            Log.LogInfo($"[Session] Plotted own units ({reason}): {tf.TaskforceObjects.Count} units -> " +
                        $"{tf.PlottingTable.Vehicles.Count} vehicles on the player plot");
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
