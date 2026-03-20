using System;
using System.Collections.Concurrent;
using BepInEx.Logging;
using LiteNetLib;
using LiteNetLib.Utils;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Transport;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Singleton that manages network transport (LiteNetLib or Steam).
    /// All network callbacks arrive on a background thread; they enqueue Actions
    /// into _mainThreadQueue which Plugin.Update() drains on the Unity main thread.
    /// </summary>
    public class NetworkManager
    {
        // ── Singleton ────────────────────────────────────────────────────────────
        public static readonly NetworkManager Instance = new NetworkManager();
        private NetworkManager() { }

        // ── State ─────────────────────────────────────────────────────────────────
        private ITransport? _transport;
        private bool        _isHost;
        private bool        _running;

        private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
        private readonly NetDataWriter           _writer          = new();

        private static ManualLogSource Log => Plugin.Log;

        // ── Public API ────────────────────────────────────────────────────────────

        public int  LastRttMs   => _transport?.RttMs ?? 0;

        public bool IsConnected => _transport?.IsConnected ?? false;

        public bool IsConnectedClient => !_isHost && IsConnected;

        public bool IsHost => _isHost;

        public void StartHost(int port)
        {
            _isHost = true;
            _transport = CreateTransport();
            WireTransportEvents();
            _transport.Start(asHost: true);
            _running = true;
            Log.LogInfo($"[Net] Hosting (transport={Plugin.Instance.CfgTransport.Value})");
        }

        public void StartClient(string ip, int port)
        {
            _isHost = false;
            _transport = CreateTransport();
            WireTransportEvents();
            _transport.Start(asHost: false);
            _running = true;
            Log.LogInfo($"[Net] Connecting as client (transport={Plugin.Instance.CfgTransport.Value})");
        }

        /// <summary>Start as host or client for transports that don't need IP/port (Steam).</summary>
        public void StartTransport(bool asHost)
        {
            if (asHost)
                StartHost(0);
            else
                StartClient("", 0);
        }

        public void Stop()
        {
            if (!_running) return;
            OrderDelayQueue.Clear();
            CombatEventHandler.ClearDelayed();
            PvPDeathNotifications.Clear();
            PvPFireAuth.Clear();
            Patch_ObjectBase_HandleEngageTasks.Reset();
            Patch_Blastzone_OnHitUnit.ClearMissileImpacts();
            _transport?.Stop();
            _transport = null;
            _running = false;
            Log.LogInfo("[Net] Stopped.");
        }

        /// <summary>Called from Plugin.Update() — must run on Unity main thread.</summary>
        public void Tick()
        {
            if (!_running) return;

            _transport?.Poll();

            // Drain queued main-thread actions
            while (_mainThreadQueue.TryDequeue(out var action))
                action();
        }

        // ── Send helpers ──────────────────────────────────────────────────────────

        public void SendToServer(INetMessage msg, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_transport == null) return;
            _writer.Reset();
            _writer.Put((byte)msg.Type);
            msg.Serialize(_writer);
            _transport.SendToServer(_writer.Data, _writer.Length, MapDelivery(delivery));
        }

        public void BroadcastToClients(INetMessage msg, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_transport == null) return;
            _writer.Reset();
            _writer.Put((byte)msg.Type);
            msg.Serialize(_writer);
            _transport.BroadcastToClients(_writer.Data, _writer.Length, MapDelivery(delivery));
        }

        public void SendToOther(INetMessage msg, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_isHost)
                BroadcastToClients(msg, delivery);
            else
                SendToServer(msg, delivery);
        }

        // ── Transport factory ───────────────────────────────────────────────────

        private ITransport CreateTransport()
        {
            if (Plugin.Instance.CfgTransport.Value == "Steam")
                return new SteamTransport();
            return new LiteNetTransport();
        }

        private void WireTransportEvents()
        {
            if (_transport == null) return;
            _transport.OnDataReceived += OnDataReceived;
            _transport.OnPeerConnected += OnPeerConnected;
            _transport.OnPeerDisconnected += OnPeerDisconnected;
        }

        // ── Delivery mapping ────────────────────────────────────────────────────

        private static TransportDelivery MapDelivery(DeliveryMethod dm) => dm switch
        {
            DeliveryMethod.Unreliable => TransportDelivery.Unreliable,
            DeliveryMethod.ReliableSequenced => TransportDelivery.Reliable,
            DeliveryMethod.ReliableOrdered => TransportDelivery.ReliableOrdered,
            DeliveryMethod.ReliableUnordered => TransportDelivery.Reliable,
            _ => TransportDelivery.ReliableOrdered,
        };

        // ── Transport event handlers ────────────────────────────────────────────

        private void OnPeerConnected()
        {
            Log.LogInfo("[Net] Peer connected");
        }

        private void OnPeerDisconnected()
        {
            Log.LogInfo("[Net] Peer disconnected");
            _mainThreadQueue.Enqueue(() =>
            {
                OrderDelayQueue.Clear();
                DriftDetector.Reset();
                StateApplier.ResetOrphanTracking();
                PvPDeathNotifications.Clear();
                PvPFireAuth.Clear();
                Patch_ObjectBase_HandleEngageTasks.Reset();
                Patch_Blastzone_OnHitUnit.ClearMissileImpacts();
                FlightOpsHandler.Clear();
                Patch_Compartments_CalculateWantedVelocityInKnots.ClearLogCache();
                Patch_Vessel_ApplyRudderThrust.ClearLogCache();
                Patch_VesselPropulsionSystem_OnUpdate.ClearLogCache();
            });
        }

        private void OnDataReceived(byte[] data, int length)
        {
            var reader = new NetDataReader(data, 0, length);
            var type = (MessageType)reader.GetByte();

            if (type != MessageType.StateUpdate && type != MessageType.MissileStateSync)
                Log.LogDebug($"[Net] Received {type}");

            switch (type)
            {
                case MessageType.StateUpdate:
                {
                    var msg = StateUpdateMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => StateApplier.Apply(msg));
                    break;
                }

                case MessageType.PlayerOrder:
                {
                    var msg = PlayerOrderMessage.Deserialize(reader);
                    long enqueueMs = AIAutoFireState.DiagMs;
                    if (msg.Order == Messages.OrderType.AutoFireWeapon)
                    {
                        Log.LogDebug($"[AutoFire DIAG] t={enqueueMs}ms ENQUEUE (bg thread) " +
                            $"unit={msg.SourceEntityId} ammo={msg.AmmoId} target={msg.TargetEntityId} " +
                            $"shots={msg.ShotsToFire}");
                    }
                    _mainThreadQueue.Enqueue(() =>
                    {
                        if (msg.Order == Messages.OrderType.AutoFireWeapon)
                        {
                            long applyMs = AIAutoFireState.DiagMs;
                            Log.LogDebug($"[AutoFire DIAG] t={applyMs}ms DEQUEUE (main thread, " +
                                $"waited {applyMs - enqueueMs}ms) unit={msg.SourceEntityId} ammo={msg.AmmoId}");
                        }
                        OrderHandler.Apply(msg);
                    });
                    break;
                }

                case MessageType.GameEvent:
                {
                    var msg = GameEventMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => GameEventHandler.Apply(msg));
                    break;
                }

                case MessageType.SessionSync:
                {
                    var msg = SessionSyncMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => SessionManager.ApplyReceivedSession(msg));
                    break;
                }

                case MessageType.SessionReady:
                {
                    var msg = SessionReadyMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => SimSyncManager.OnClientReady());
                    break;
                }

                case MessageType.CombatEvent:
                {
                    var msg = CombatEventMessage.Deserialize(reader);
                    Log.LogDebug($"[Net] Deserialized CombatEvent: {msg.EventType} target={msg.TargetEntityId} source={msg.SourceEntityId}");
                    _mainThreadQueue.Enqueue(() => CombatEventHandler.Apply(msg));
                    break;
                }

                case MessageType.DamageState:
                {
                    var msg = DamageStateMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => DamageStateSerializer.Apply(msg));
                    break;
                }

                case MessageType.DamageDecal:
                {
                    var msg = DamageDecalMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => CombatEventHandler.RunAsNetworkEvent(
                        () => DamageStateSerializer.ApplyDecal(msg)));
                    break;
                }

                case MessageType.ProjectileSpawn:
                {
                    var msg = ProjectileSpawnMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => ProjectileIdMapper.OnHostSpawnReceived(msg.HostProjectileId, msg.SourceUnitId, msg.AmmoName));
                    break;
                }

                case MessageType.FlightOps:
                {
                    var msg = FlightOpsMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => FlightOpsHandler.Apply(msg));
                    break;
                }

                case MessageType.MissileStateSync:
                {
                    var msg = MissileStateSyncMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => MissileStateSyncHandler.Apply(msg));
                    break;
                }

                case MessageType.ChaffLaunch:
                {
                    var msg = ChaffLaunchMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => Patch_ObjectBase_LaunchChaff.ApplyFromNetwork(msg));
                    break;
                }

                default:
                    Log.LogWarning($"[Net] Unknown message type: {type}");
                    break;
            }
        }
    }
}
