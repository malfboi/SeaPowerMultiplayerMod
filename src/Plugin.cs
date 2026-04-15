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
    /// <summary>Lightweight shim so existing CfgPvP.Value reads keep compiling.</summary>
    internal sealed class AlwaysTrue { public bool Value => true; }

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
        internal readonly AlwaysTrue CfgPvP = new();
        internal ConfigEntry<string> CfgTransport = null!;
        internal ConfigEntry<bool> CfgTimeVote = null!;

        // Debug config
        internal ConfigEntry<bool> CfgVerboseDebug = null!;

        // PvP sync tuning
        internal ConfigEntry<float> CfgDamageSyncInterval = null!;

        private Harmony _harmony = null!;
        private int _sceneReadyFrames;
        private const int SceneSettleFrames = 30; // ~0.5s buffer after IsLoadingDone

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // Bind config
            CfgIsHost      = Config.Bind("Network", "IsHost",       true,        "True = run as server, False = connect as client");
            CfgHostIP      = Config.Bind("Network", "HostIP",       "127.0.0.1", "Host IP address (used when IsHost=false)");
            CfgPort        = Config.Bind("Network", "Port",         7777,        "UDP port");
            CfgAutoConnect = Config.Bind("Network", "AutoConnect",  false,       "Connect/host automatically on game launch");
            // PvP is always on — no config entry needed
            CfgTransport   = Config.Bind("Network", "Transport",    "LiteNetLib", "Network transport: LiteNetLib (direct IP) or Steam (P2P with invites)");
            CfgTimeVote    = Config.Bind("Network", "TimeVote",     false,       "Time vote mode: both players must agree on time compression changes");

            // Debug
            CfgVerboseDebug = Config.Bind("Debug", "VerboseLogging", false,
                "Enable verbose per-tick debug logging (Serialize counts, AutoFire diagnostics, Net received)");

            // PvP sync tuning
            CfgDamageSyncInterval   = Config.Bind("Sync", "DamageSyncInterval",     2f,   "Seconds between damage state corrections (default 2)");

            // Attach helper MonoBehaviours to this same GameObject
            gameObject.AddComponent<StateBroadcaster>();
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

            // Auto-connect only for LiteNetLib mode
            if (CfgAutoConnect.Value && CfgTransport.Value != "Steam")
            {
                if (CfgIsHost.Value)
                    NetworkManager.Instance.StartHost(CfgPort.Value);
                else
                    NetworkManager.Instance.StartClient(CfgHostIP.Value, CfgPort.Value);
            }
        }

        private bool _loggedWaitingForSceneCreator;
        private int _sceneReadyPollCount;

        private void Update()
        {
            // Pump the network manager every frame (processes queued actions on main thread)
            NetworkManager.Instance.Tick();

            // Drain any PvP delayed combat actions whose timer has expired
            OrderDelayQueue.Tick();
            CombatEventHandler.Tick();

            // Check for pending session sync retries (failed sends)
            SessionManager.TickRetry();

            // Process deferred flight ops spawns (elevators were busy)
            FlightOpsHandler.Tick();

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
            OrderDelayQueue.Clear();
            NetworkManager.Instance.Stop();
            _harmony.UnpatchSelf();
        }
    }
}
