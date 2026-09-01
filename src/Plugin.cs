using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Transport;
using UnityEngine;

namespace SeapowerMultiplayer
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        internal static Plugin Instance = null!;

        // --- Config entries (edit BepInEx/config/SeapowerMultiplayer.cfg in-game folder) ---
        internal ConfigEntry<bool> CfgIsHost = null!;
        internal ConfigEntry<string> CfgHostIP = null!;
        internal ConfigEntry<int> CfgPort = null!;
        internal ConfigEntry<bool> CfgAutoConnect = null!;
        internal ConfigEntry<bool> CfgLockUnits = null!;
        internal ConfigEntry<string> CfgDefaultTeam = null!;
        internal ConfigEntry<string> CfgUsername = null!;
        internal ConfigEntry<string> CfgTransport = null!;
        internal ConfigEntry<bool> CfgTimeVote = null!;

        // Debug config
        internal ConfigEntry<bool> CfgVerboseDebug = null!;
        internal ConfigEntry<float> CfgNetSimLossPct = null!;
        internal ConfigEntry<int> CfgNetSimLatencyMs = null!;
        internal ConfigEntry<int> CfgNetSimJitterMs = null!;
        internal ConfigEntry<bool> CfgMotionTrace = null!;

        // PvP sync tuning
        internal ConfigEntry<float> CfgDamageSyncInterval = null!;

        // Connection resilience
        internal ConfigEntry<int> CfgDisconnectTimeoutSec = null!;

        // State stream rates (host)
        internal ConfigEntry<int> CfgMissileStateHz = null!;
        internal ConfigEntry<int> CfgUnitStateHz = null!;
        internal ConfigEntry<bool> CfgReplicaInterpolation = null!;
        internal ConfigEntry<int> CfgUnitStateHzNear = null!;

        // Shared tactical picture
        internal ConfigEntry<bool> CfgContactSync = null!;
        internal ConfigEntry<bool> CfgDrawingSync = null!;

        // Opt-in diagnostics
        internal ConfigEntry<bool>   CfgShareDiagnostics = null!;
        internal ConfigEntry<bool>   CfgDiagnosticsAsked = null!;
        internal ConfigEntry<string> CfgInstallId        = null!;
        internal ConfigEntry<string> CfgDiagnosticsUrl   = null!;

        /// <summary>Verbose diagnostics are on when the player asked for them, and
        /// also while diagnostics sharing is on - investigating replication bugs is
        /// the entire point of opting in. Kept separate from CfgVerboseDebug so the
        /// opt-in never silently rewrites a value the player can see.</summary>
        internal bool VerboseEffective => CfgVerboseDebug.Value || Analytics.Enabled;

        private Harmony _harmony = null!;
        private int _sceneReadyFrames;
        private const int SceneSettleFrames = 30; // ~0.5s buffer after IsLoadingDone

        /// <summary>Set when Awake's patch/init sequence throws. Multiplayer is
        /// dead for the session; the F9 overlay shows this instead of the
        /// connection controls so the failure is loud rather than a silent
        /// do-nothing lobby button.</summary>
        public static string? FatalInitError { get; private set; }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // Bind config
            CfgIsHost      = Config.Bind("Network", "IsHost",       true,        "True = run as server, False = connect as client");
            CfgHostIP      = Config.Bind("Network", "HostIP",       "127.0.0.1", "Host IP address (used when IsHost=false)");
            CfgPort        = Config.Bind("Network", "Port",         7777,        "UDP port");
            CfgAutoConnect = Config.Bind("Network", "AutoConnect",  false,       "Connect/host automatically on game launch");
            CfgLockUnits   = Config.Bind("Network", "LockUnitsToPlayers", false,  "Host: each player owns the formations assigned to them and sees teammates' units as allies. Off = free-for-all, any teammate can order any unit.");
            // The lock rides on every ownership packet, so a mid-session change has to
            // push one - otherwise clients keep enforcing the old rule and one machine
            // silently disagrees with the others about who may command what.
            CfgLockUnits.SettingChanged += (_, __) => FormationOwnership.HostSendFull();
            CfgDefaultTeam = Config.Bind("Network", "DefaultTeam",  "Blue",      "Team to request when joining without an invite (Blue or Red). The host decides the final seating.");
            CfgUsername    = Config.Bind("Network", "Username",     "",          "Name other players see in the roster and the 'send to player' menu. Mainly for LiteNetLib, which has no identity of its own - leave empty there and you show up as \"Player 2\". On Steam this overrides your persona name if set.");
            CfgTransport   = Config.Bind("Network", "Transport",    "LiteNetLib", "Network transport: LiteNetLib (direct IP) or Steam (P2P with invites)");
            CfgTimeVote    = Config.Bind("Network", "TimeVote",     false,       "Time vote mode: both players must agree on time compression changes");
            // The client defers to the host's setting, and SessionSync only carries
            // it at join time. The overlay makes it editable mid-session, so push
            // every change - otherwise the client stays on the legacy request path.
            CfgTimeVote.SettingChanged += (_, __) => TimeSyncManager.HostBroadcastVoteMode();
            CfgDisconnectTimeoutSec = Config.Bind("Network", "DisconnectTimeoutSec", 20,
                "Seconds of silence before the link is declared dead. The stock transport defaults " +
                "(LiteNetLib 5s, Steam 10s) drop high-latency players during ordinary stalls. " +
                "A dead link now freezes the session rather than silently splitting it, so a slower " +
                "verdict costs nothing.");

            // Debug
            CfgVerboseDebug = Config.Bind("Debug", "VerboseLogging", false,
                "Enable verbose per-tick debug logging (Serialize counts, AutoFire diagnostics, Net received)");
            CfgNetSimLossPct = Config.Bind("Debug", "NetSimPacketLossPct", 0f,
                "TESTING ONLY: drop this percentage of incoming Unreliable packets (LiteNetLib transport)");
            CfgNetSimLatencyMs = Config.Bind("Debug", "NetSimLatencyMs", 0,
                "TESTING ONLY: delay all incoming packets by this many milliseconds (LiteNetLib transport)");
            CfgNetSimJitterMs = Config.Bind("Debug", "NetSimJitterMs", 0,
                "TESTING ONLY: vary NetSimLatencyMs by ±this many milliseconds per packet. " +
                "A constant delay cannot reproduce arrival-time variance, which is what " +
                "high-ping links actually inflict on the replica stream. Needs NetSimLatencyMs > 0.");
            CfgMotionTrace = Config.Bind("Debug", "MotionTrace", false,
                "DIAGNOSTIC: write a per-frame motion trace CSV to <persistentDataPath>/MPTrace/ for " +
                "one unit, pinned by id when tracing starts. Select the same unit on host and client, " +
                "start the trace on both, then DESELECT on both - the files line up on the mission " +
                "clock so host truth can be diffed against client rendering, and deselecting releases " +
                "the co-op ally lock so the unit can still be ordered. Ctrl+F11 toggles it in-game " +
                "(each enable starts a new file and re-pins).");

            // PvP sync tuning
            CfgDamageSyncInterval   = Config.Bind("Sync", "DamageSyncInterval",     2f,   "Seconds between damage state corrections (default 2)");

            // State stream rates (host → client)
            CfgMissileStateHz = Config.Bind("Sync", "MissileStateHz", 20,
                "Host missile state stream rate in Hz (1-60, default 20)");
            CfgUnitStateHzNear = Config.Bind("Sync", "UnitStateHzNear", 25,
                "Host stream rate in Hz for units inside the client's camera view (1-60, default 25). " +
                "Only a handful of units are ever on screen, so the extra bandwidth is small and it " +
                "buys smoothness exactly where the player can see it. Falls back to UnitStateHz for " +
                "everything else, and for all units until the client reports a viewport.");
            CfgReplicaInterpolation = Config.Bind("Sync", "ReplicaInterpolation", true,
                "Client renders remote units slightly in the host's past and interpolates between " +
                "received states, instead of extrapolating past the newest one. This is what keeps " +
                "link jitter out of unit motion. Turn off to A/B against pure extrapolation.");
            CfgUnitStateHz    = Config.Bind("Sync", "UnitStateHz",    10,
                "Host unit/torpedo state stream rate in Hz (1-60, default 10)");

            // Shared tactical picture (CO-OP ONLY - both are ignored in PvP, where the
            // two players are opponents whose pictures are meant to differ)
            CfgContactSync = Config.Bind("Sync", "ContactSync", true,
                "CO-OP ONLY. Share the host's contact picture - track numbers, classified side and " +
                "identified class - with the client. Sensors are simulated on both machines, so " +
                "without this the same contact carries different track numbers on each screen and " +
                "can be identified on one and unknown on the other. Additive: the client never " +
                "loses a contact it identified first. Ignored in PvP.");
            CfgDrawingSync = Config.Bind("Sync", "DrawingSync", true,
                "CO-OP ONLY. Share map drawings (markers, rulers, circles, polygons, text) between " +
                "both players. Either side may draw; the whole layer is replaced on the other side " +
                "when it changes. Ignored in PvP.");

            // Opt-in diagnostics. Off until the player says otherwise; nothing is
            // captured or sent while it is off. See PRIVACY.md.
            CfgShareDiagnostics = Config.Bind("Diagnostics", "ShareDiagnostics", false,
                "Share anonymous diagnostics (logs, ping/packet loss, frame rate, replica drift) " +
                "to help fix desyncs and connection bugs. Off by default. Steam IDs, names and " +
                "file paths are scrubbed before anything leaves this PC.");
            CfgDiagnosticsAsked = Config.Bind("Diagnostics", "Asked", false,
                "Internal: the one-time consent prompt has been shown. Delete to see it again.");
            CfgInstallId = Config.Bind("Diagnostics", "InstallId", "",
                "Internal: random anonymous id, generated the first time diagnostics are enabled. " +
                "Quote it to have your data deleted. Delete this line to get a new one.");
            CfgDiagnosticsUrl = Config.Bind("Diagnostics", "Endpoint", "",
                "Override the diagnostics upload endpoint. Leave blank for the default; " +
                "used to point at a local wrangler dev instance during development.");

            // Two-instance test harness: SPMP_* environment variables override the
            // shared config file so one install can run host + client instances.
            ApplyEnvOverrides();

            // Before PatchAll, so a patch failure below is captured and uploaded.
            Analytics.Init();

            // Attach helper MonoBehaviours to this same GameObject
            gameObject.AddComponent<StateBroadcaster>();
            gameObject.AddComponent<HostEntityStreamer>();
            gameObject.AddComponent<ContactSyncStreamer>();
            gameObject.AddComponent<SeapowerMultiplayer.UI.NoesisOverlay>();

            // Apply Harmony patches. One bad patch used to abort Awake silently:
            // Steam callbacks never registered and the lobby button did nothing.
            // Fail loud instead - record the error for the F9 overlay and leave
            // multiplayer disabled (unapplied patches are inert while offline).
            try
            {
                _harmony = new Harmony(PluginInfo.PLUGIN_GUID);
                _harmony.PatchAll();

                // Initialize Steam lobby callbacks (safe even if transport is LiteNetLib)
                SteamLobbyManager.Init();

                // Ask the Workshop whether this build is the current one. Purely
                // informational - it only ever raises a banner in the overlay, and
                // it no-ops on this branch's non-workshop install layout.
                SeapowerMultiplayer.UI.WorkshopVersionCheck.Start();
            }
            catch (Exception ex)
            {
                FatalInitError = ex.Message;
                Log.LogError($"FATAL: mod init failed - multiplayer disabled for this session.\n{ex}");
                return;
            }

            Log.LogInfo($"SeapowerMultiplayer v{PluginInfo.PLUGIN_VERSION} loaded.");
            Log.LogInfo($"Transport: {CfgTransport.Value}  Mode: {(CfgIsHost.Value ? "HOST" : "CLIENT")}  Port: {CfgPort.Value}");
            Log.LogInfo("Press Ctrl+F9 to toggle the multiplayer overlay (works in the main menu too).");

            // Check for +connect_lobby launch arg (Steam invite while game was closed)
            if (CfgTransport.Value == "Steam")
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong lobbyId))
                    {
                        Log.LogInfo($"[Steam] Launch arg +connect_lobby {lobbyId}");
                        SteamLobbyManager.JoinLobbyFromLaunchArg(lobbyId);
                        break;
                    }
                }
            }

            // Auto-connect (LiteNetLib) is deferred to Update() - see TryAutoConnect().
            // It must NOT run here: at Awake() the network pump (Update -> Tick ->
            // Poll) isn't ticking yet, so a connection opened now completes on
            // LiteNetLib's background thread while no Poll runs. The client never
            // processes "peer connected" (and never sends its Hello) until its main
            // loop finally pumps - by which point the host's 5 s Hello deadline has
            // expired and it has dropped the connection.
        }

        /// <summary>
        /// SPMP_* environment variables override config values for this run.
        /// When any override is active, config persistence is disabled so the
        /// overrides never leak into the cfg file.
        /// CAVEAT (verified 2026-06-10): launching Sea Power.exe directly respawns
        /// the process via Steam, dropping an injected environment - overrides
        /// only reach the game when the environment survives (e.g. set globally).
        /// For routine two-instance testing, set each install's own cfg instead
        /// (Steam install = host, desktop copy = client, both AutoConnect).
        /// </summary>
        private void ApplyEnvOverrides()
        {
            static string? V(string name)
            {
                var v = System.Environment.GetEnvironmentVariable(name);
                return string.IsNullOrEmpty(v) ? null : v;
            }

            string? role      = V("SPMP_ROLE");
            string? hostIp    = V("SPMP_HOSTIP");
            string? port      = V("SPMP_PORT");
            // SPMP_TEAM replaces SPMP_PVP: the two-instance test harness now says which
            // SIDE the second instance plays, not what mode the session is in.
            string? team      = V("SPMP_TEAM");
            // The whole point of a name override is telling two instances on ONE
            // machine apart, so it has to be settable per-process, not just in the
            // config file both of them share.
            string? username  = V("SPMP_USERNAME");
            string? autoConn  = V("SPMP_AUTOCONNECT");
            string? transport = V("SPMP_TRANSPORT");
            string? simLoss   = V("SPMP_NETSIM_LOSS");
            string? simLat    = V("SPMP_NETSIM_LATMS");
            string? simJitter = V("SPMP_NETSIM_JITTERMS");

            if (role == null && hostIp == null && port == null && team == null
                && username == null && autoConn == null && transport == null && simLoss == null
                && simLat == null && simJitter == null)
                return;

            Config.SaveOnConfigSet = false; // keep dev overrides out of the shared cfg

            if (role != null)      CfgIsHost.Value      = role.Equals("host", StringComparison.OrdinalIgnoreCase);
            if (hostIp != null)    CfgHostIP.Value      = hostIp;
            if (port != null && int.TryParse(port, out int p))            CfgPort.Value = p;
            if (team != null)      CfgDefaultTeam.Value = team.Equals("red", StringComparison.OrdinalIgnoreCase) ? "Red" : "Blue";
            if (username != null)  CfgUsername.Value    = username;
            if (autoConn != null)  CfgAutoConnect.Value = autoConn == "1" || autoConn.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (transport != null) CfgTransport.Value   = transport;
            if (simLoss != null && float.TryParse(simLoss, out float l)) CfgNetSimLossPct.Value = l;
            if (simLat != null && int.TryParse(simLat, out int ms))      CfgNetSimLatencyMs.Value = ms;
            if (simJitter != null && int.TryParse(simJitter, out int j)) CfgNetSimJitterMs.Value = j;

            Log.LogWarning($"[Config] SPMP_* env overrides active (role={(CfgIsHost.Value ? "host" : "client")}, " +
                $"ip={CfgHostIP.Value}, port={CfgPort.Value}, team={CfgDefaultTeam.Value}, user={CfgUsername.Value}, " +
                $"autoConnect={CfgAutoConnect.Value}, " +
                $"transport={CfgTransport.Value}, simLoss={CfgNetSimLossPct.Value}%, " +
                $"simLat={CfgNetSimLatencyMs.Value}ms, simJitter=±{CfgNetSimJitterMs.Value}ms). " +
                "Config persistence disabled for this run.");
        }

        /// <summary>
        /// Fires the configured LiteNetLib auto-connect once, from the Update loop
        /// rather than Awake(), so the network pump is already running when the
        /// connection opens. This guarantees the client's Hello is sent within a
        /// frame of "peer connected" instead of racing the game's startup load
        /// against the host's 5 s handshake deadline. A short settle past the first
        /// Update keeps the frame cadence steady (we're past the boot-load hitch)
        /// before opening the deadline-bearing handshake.
        /// </summary>
        private void TryAutoConnect()
        {
            if (_autoConnectStarted) return;

            if (!CfgAutoConnect.Value || CfgTransport.Value == "Steam")
            {
                _autoConnectStarted = true; // nothing to do; never re-check
                return;
            }

            if (_firstUpdateRealtime < 0f) _firstUpdateRealtime = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - _firstUpdateRealtime < 1f) return;

            _autoConnectStarted = true;
            if (CfgIsHost.Value)
                NetworkManager.Instance.StartHost(CfgPort.Value);
            else
                NetworkManager.Instance.StartClient(CfgHostIP.Value, CfgPort.Value);
        }

        /// <summary>
        /// Records the first exception raised while the synced mission is loading.
        /// Only recorded, not acted on: harmless exceptions do occur during a normal
        /// load, so this is reported solely when the load then fails to finish — at
        /// which point it is usually the actual cause and worth putting in front of
        /// the player.
        /// </summary>
        private void OnLoadLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception) return;
            if (_firstLoadException != null) return;
            _firstLoadException = condition;

            // An exception that unwound out of the LoadMission coroutine is fatal,
            // not incidental: Unity stops iterating a coroutine that throws, so the
            // rest of the load never runs and IsLoadingDone can never become true.
            // No point waiting out the deadline for a verdict already decided.
            if (stackTrace != null && stackTrace.Contains("LoadMission"))
            {
                Log.LogError($"[SceneReady] Mission load coroutine died: {condition}");
                ReportStalledLoad(coroutineDied: true);
                return;
            }

            Log.LogWarning($"[SceneReady] Exception during mission load (reported only if the load stalls): {condition}");
        }

        private void ReportStalledLoad(bool coroutineDied = false)
        {
            Application.logMessageReceived -= OnLoadLogMessage;
            _wasSceneLoading = false;
            _sceneReadyPollCount = 0;
            _sceneReadyFrames = 0;
            _loggedWaitingForSceneCreator = false;
            SessionManager.OnSceneLoadStalled(_firstLoadException, coroutineDied ? -1f : SceneLoadTimeoutSec);
            _firstLoadException = null;
        }

        private bool _loggedWaitingForSceneCreator;
        private int _sceneReadyPollCount;

        // Stalled-load detection. The game loads the synced save inside the
        // SceneCreator.LoadMission coroutine; an exception in there (a save
        // referencing data the local build doesn't have, say) kills the coroutine
        // silently, IsLoadingDone never turns true, and the poll above spins until
        // the player gives up. Generous deadline - a large save on a slow disk is
        // legitimately slow, and a false alarm here would be worse than waiting.
        private const float SceneLoadTimeoutSec = 120f;
        private bool _wasSceneLoading;
        private float _sceneLoadDeadline;
        private string? _firstLoadException;

        // Deferred auto-connect state (see TryAutoConnect)
        private bool  _autoConnectStarted;
        private float _firstUpdateRealtime = -1f;

        private void Update()
        {
            // Pump the network manager every frame (processes queued actions on main thread)
            NetworkManager.Instance.Tick();

            // Open the auto-connect connection now that the pump is running (not in Awake)
            TryAutoConnect();

            // Advance per-frame telemetry ring (send-bytes flatness)
            Telemetry.FrameTick();

            // Opt-in diagnostics: metric sampling + error-flush timing. No-op when off.
            Analytics.Tick();

            // v2: drive kinematic weapon replicas (client) + keep defence switch asserted.
            // Normally already done this frame by the RenderPosition.OnUpdate prefix, so
            // the camera sees our writes; this is the fallback when that never runs.
            ReplicaTick.RunOnce();
            CarrierOpsHandler.Tick();
            WeaponHatchHandler.Tick();
            Suppression.EnforceDefenseFlag();
            Suppression.EnforceInterceptSymmetry();
            OrderRefusalNotice.SampleInput();

            // Ctrl+F11 motion trace. Last of the per-frame hooks so the FRAME row
            // records the transform the replica drivers actually left behind.
            MotionTrace.Tick();

            // Check for pending session sync retries (failed sends)
            SessionManager.TickRetry();

            // Hold the sim frozen while a dropped peer is being recovered
            ReconnectManager.Tick();

            // Tell the host what we're looking at, so it streams those units faster
            ViewportHintSender.Tick();

            // Ctrl+F10: manual hard sync
            if (Input.GetKeyDown(KeyCode.F10) &&
                (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
                NetworkManager.Instance.IsConnected)
            {
                if (CfgIsHost.Value)
                {
                    Log.LogInfo("[HardSync] Manual hard sync triggered (host)");
                    SessionManager.CaptureAndSend();
                }
                else
                {
                    Log.LogInfo("[HardSync] Manual hard sync requested (client)");
                    NetworkManager.Instance.SendToServer(new GameEventMessage
                    {
                        EventType = GameEventType.HardSyncRequest,
                    }, DeliveryMethod.ReliableOrdered);
                }
            }

            // Detect client scene load completion.
            // Wait a few frames after IsLoadingDone for game objects
            // (sensors, taskforces) to finish initialising.
            if (SessionManager.SceneLoading != _wasSceneLoading)
            {
                _wasSceneLoading = SessionManager.SceneLoading;
                if (_wasSceneLoading)
                {
                    _sceneLoadDeadline = Time.unscaledTime + SceneLoadTimeoutSec;
                    _firstLoadException = null;
                    Application.logMessageReceived += OnLoadLogMessage;
                }
                else
                {
                    Application.logMessageReceived -= OnLoadLogMessage;
                }
            }

            if (SessionManager.SceneLoading)
            {
                bool scExists = Singleton<SceneCreator>.InstanceExists(false);
                bool loadDone = scExists && Singleton<SceneCreator>.Instance.IsLoadingDone;

                if (!loadDone && Time.unscaledTime > _sceneLoadDeadline)
                {
                    ReportStalledLoad();
                    return;
                }

                if (!scExists && !_loggedWaitingForSceneCreator)
                {
                    Log.LogInfo("[SceneReady] SceneLoading=true, waiting for SceneCreator to exist...");
                    _loggedWaitingForSceneCreator = true;
                }

                if (scExists && !loadDone && _sceneReadyFrames == 0)
                {
                    _sceneReadyPollCount++;
                    if (_sceneReadyPollCount == 1 || _sceneReadyPollCount % 60 == 0)
                        Log.LogInfo($"[SceneReady] SceneCreator exists, IsLoadingDone=false, waiting... (poll #{_sceneReadyPollCount})");
                }

                if (loadDone)
                {
                    _sceneReadyFrames++;
                    if (_sceneReadyFrames == 1)
                        Log.LogInfo($"[SceneReady] IsLoadingDone=true, settling for {SceneSettleFrames} frames...");
                    if (_sceneReadyFrames >= SceneSettleFrames)
                    {
                        Log.LogInfo("[SceneReady] Settle complete, calling OnSceneReady()");
                        if (_sceneReadyPollCount > 1)
                            Log.LogInfo($"[SceneReady] Loading complete after {_sceneReadyPollCount} polls");
                        _sceneReadyPollCount = 0;
                        _sceneReadyFrames = 0;
                        _loggedWaitingForSceneCreator = false;
                        SessionManager.OnSceneReady();
                    }
                }
                else
                {
                    // Reset if IsLoadingDone flickers false during unload/reload
                    // (prevents stale frame count from carrying over)
                    _sceneReadyFrames = 0;
                }
            }
            else
            {
                _sceneReadyFrames = 0;
                _loggedWaitingForSceneCreator = false;
            }
        }

        private void OnDestroy()
        {
            // First: the session-end batch is the most valuable one, and it needs
            // the link state that Stop() is about to tear down.
            Analytics.Shutdown();
            MotionTrace.Close();
            NetworkManager.Instance.Stop();
            _harmony.UnpatchSelf();
        }
    }
}
