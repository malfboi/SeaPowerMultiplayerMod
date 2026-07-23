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
    /// <summary>
    /// Mod runtime. Loaded by Anchor Chain (see <see cref="AnchorChainEntry"/>),
    /// not by BepInEx directly, so this is a plain MonoBehaviour rather than a
    /// BaseUnityPlugin - <see cref="Boot"/> does the hosting BepInEx used to do.
    /// BepInEx itself is still present (Anchor Chain's preloader is a BepInEx
    /// plugin), so its logging and config file remain available.
    /// </summary>
    public class Plugin : MonoBehaviour
    {
        internal static ManualLogSource Log = null!;
        internal static Plugin Instance = null!;

        /// <summary>Set by <see cref="Boot"/> before the component is added, because
        /// AddComponent runs Awake() synchronously and Awake() binds against it.</summary>
        internal static ConfigFile Config = null!;

        private static bool _booted;

        /// <summary>
        /// Entry point called by Anchor Chain. Creates the persistent host
        /// GameObject and the logger/config that BepInEx would otherwise provide.
        /// </summary>
        internal static void Boot()
        {
            if (_booted)
            {
                Log?.LogWarning("Boot() called twice - ignoring the second load.");
                return;
            }
            _booted = true;

            Log = BepInEx.Logging.Logger.CreateLogSource(PluginInfo.PLUGIN_NAME);

            // Anchor Chain loads every DLL in every search directory without
            // consulting the game's mod checkboxes, so unticking us in the mod
            // menu would otherwise do nothing. Honour it ourselves.
            if (!IsEnabledInModMenu())
            {
                Log.LogInfo("Disabled in the game's mod menu - not loading. " +
                            "Tick \"Seapower Multiplayer\" there to enable it.");
                return;
            }
            // GUID-named, matching what BaseUnityPlugin used to create, so existing
            // installs keep their settings instead of silently reverting to defaults.
            Config = new ConfigFile(
                System.IO.Path.Combine(Paths.ConfigPath, PluginInfo.PLUGIN_GUID + ".cfg"), true);

            var go = new GameObject(PluginInfo.PLUGIN_NAME);
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Plugin>();
        }

        /// <summary>
        /// Looks up the SearchDirectory this assembly was loaded from and returns
        /// its tick state from the game's mod menu.
        ///
        /// Fails open: if the directory can't be identified, load anyway. A mod
        /// that silently refuses to start is far worse to diagnose than one that
        /// ignores a checkbox.
        /// </summary>
        private static bool IsEnabledInModMenu()
        {
            try
            {
                string here = System.IO.Path.GetFullPath(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "");
                if (here.Length == 0) return true;

                foreach (var sd in Singleton<FileManager>.Instance.Directories)
                {
                    var di = sd?.DirectoryInfo;
                    if (di == null) continue;

                    // Anchor Chain scans recursively, so we may sit in a subfolder
                    // of the search directory rather than its root.
                    string root = System.IO.Path.GetFullPath(di.FullName)
                                  .TrimEnd(System.IO.Path.DirectorySeparatorChar);
                    if (here.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                        here.StartsWith(root + System.IO.Path.DirectorySeparatorChar,
                                        StringComparison.OrdinalIgnoreCase))
                        return sd.IsChecked;
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Could not read the mod menu state ({ex.Message}); loading anyway.");
            }

            return true;
        }

        // --- Config entries (edited in-game from the F9 overlay's SETTINGS section,
        //     persisted to BepInEx/config/com.seapowermultiplayer.plugin.cfg) ---
        internal ConfigEntry<bool> CfgIsHost = null!;
        internal ConfigEntry<string> CfgHostIP = null!;
        internal ConfigEntry<int> CfgPort = null!;
        internal ConfigEntry<bool> CfgAutoConnect = null!;
        internal ConfigEntry<bool> CfgPvP = null!;
        internal ConfigEntry<string> CfgTransport = null!;
        internal ConfigEntry<bool> CfgTimeVote = null!;

        // Debug config
        internal ConfigEntry<bool> CfgVerboseDebug = null!;
        internal ConfigEntry<float> CfgNetSimLossPct = null!;
        internal ConfigEntry<int> CfgNetSimLatencyMs = null!;
        internal ConfigEntry<int> CfgNetSimJitterMs = null!;

        // PvP sync tuning
        internal ConfigEntry<float> CfgDamageSyncInterval = null!;

        // Connection resilience
        internal ConfigEntry<int> CfgDisconnectTimeoutSec = null!;

        // State stream rates (host)
        internal ConfigEntry<int> CfgMissileStateHz = null!;
        internal ConfigEntry<int> CfgUnitStateHz = null!;
        internal ConfigEntry<bool> CfgReplicaInterpolation = null!;
        internal ConfigEntry<int> CfgUnitStateHzNear = null!;

        private Harmony _harmony = null!;
        private int _sceneReadyFrames;
        private const int SceneSettleFrames = 30; // ~0.5s buffer after IsLoadingDone

        private void Awake()
        {
            Instance = this;

            // Bind config
            CfgIsHost      = Config.Bind("Network", "IsHost",       true,        "True = run as server, False = connect as client");
            CfgHostIP      = Config.Bind("Network", "HostIP",       "127.0.0.1", "Host IP address (used when IsHost=false)");
            CfgPort        = Config.Bind("Network", "Port",         7777,        "UDP port");
            CfgAutoConnect = Config.Bind("Network", "AutoConnect",  false,       "Connect/host automatically on game launch");
            CfgPvP         = Config.Bind("Network", "PvP",          true,        "True = PvP mode (opposing taskforces); False = co-op (shared ally control)");
            CfgTransport   = Config.Bind("Network", "Transport",    "LiteNetLib", "Network transport: LiteNetLib (direct IP) or Steam (P2P with invites)");
            CfgTimeVote    = Config.Bind("Network", "TimeVote",     false,       "Time vote mode: both players must agree on time compression changes");
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

            // Two-instance test harness: SPMP_* environment variables override the
            // shared config file so one install can run host + client instances.
            ApplyEnvOverrides();

            // Workshop builds are Steam-only: there is no launcher to configure a
            // direct-IP session and no UI to pick a transport. Forced after the env
            // overrides so a stale cfg value can't select a transport that the mod
            // no longer exposes.
            CfgTransport.Value = "Steam";

            // Attach helper MonoBehaviours to this same GameObject
            gameObject.AddComponent<StateBroadcaster>();
            gameObject.AddComponent<HostEntityStreamer>();
            gameObject.AddComponent<MultiplayerUI>();

            // Apply Harmony patches
            _harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            _harmony.PatchAll();

            // Initialize Steam lobby callbacks (safe even if transport is LiteNetLib)
            SteamLobbyManager.Init();

            Log.LogInfo($"SeapowerMultiplayer v{PluginInfo.PLUGIN_VERSION} loaded.");
            Log.LogInfo($"Transport: {CfgTransport.Value}  Mode: {(CfgIsHost.Value ? "HOST" : "CLIENT")}  Port: {CfgPort.Value}");
            Log.LogInfo("Press F9 in-game to toggle the multiplayer UI overlay.");

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
            string? pvp       = V("SPMP_PVP");
            string? autoConn  = V("SPMP_AUTOCONNECT");
            string? transport = V("SPMP_TRANSPORT");
            string? simLoss   = V("SPMP_NETSIM_LOSS");
            string? simLat    = V("SPMP_NETSIM_LATMS");
            string? simJitter = V("SPMP_NETSIM_JITTERMS");

            if (role == null && hostIp == null && port == null && pvp == null
                && autoConn == null && transport == null && simLoss == null && simLat == null
                && simJitter == null)
                return;

            Config.SaveOnConfigSet = false; // keep dev overrides out of the shared cfg

            if (role != null)      CfgIsHost.Value      = role.Equals("host", StringComparison.OrdinalIgnoreCase);
            if (hostIp != null)    CfgHostIP.Value      = hostIp;
            if (port != null && int.TryParse(port, out int p))            CfgPort.Value = p;
            if (pvp != null)       CfgPvP.Value         = pvp == "1" || pvp.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (autoConn != null)  CfgAutoConnect.Value = autoConn == "1" || autoConn.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (transport != null) CfgTransport.Value   = transport;
            if (simLoss != null && float.TryParse(simLoss, out float l)) CfgNetSimLossPct.Value = l;
            if (simLat != null && int.TryParse(simLat, out int ms))      CfgNetSimLatencyMs.Value = ms;
            if (simJitter != null && int.TryParse(simJitter, out int j)) CfgNetSimJitterMs.Value = j;

            Log.LogWarning($"[Config] SPMP_* env overrides active (role={(CfgIsHost.Value ? "host" : "client")}, " +
                $"ip={CfgHostIP.Value}, port={CfgPort.Value}, pvp={CfgPvP.Value}, autoConnect={CfgAutoConnect.Value}, " +
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

        private bool _loggedWaitingForSceneCreator;
        private int _sceneReadyPollCount;

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

            // v2: drive kinematic weapon replicas (client) + keep defence switch asserted
            WeaponReplicaDriver.Tick();
            UnitReplicaDriver.Tick();
            DeckPuppetDriver.Tick();
            CarrierOpsHandler.Tick();
            WeaponHatchHandler.Tick();
            Suppression.EnforceDefenseFlag();

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
            if (SessionManager.SceneLoading)
            {
                bool scExists = Singleton<SceneCreator>.InstanceExists(false);
                bool loadDone = scExists && Singleton<SceneCreator>.Instance.IsLoadingDone;

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
            NetworkManager.Instance.Stop();
            _harmony.UnpatchSelf();
        }
    }
}
