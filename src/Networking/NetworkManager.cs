using System;
using System.Collections.Concurrent;
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
        private long        _stateUpdateSentCount;
        private long        _largeStateUpdateSentCount;
        private int         _lastStateUpdateBytes;
        private int         _lastStateUpdateUnits;
        private int         _lastStateUpdateProjectiles;
        private const int LiteNetStateUpdateSinglePacketBytes = 1023;
        private const int StateUpdateLargeBytes = 900;

        private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
        private readonly NetDataWriter           _writer          = new();

        // ── Public API ────────────────────────────────────────────────────────────

        public int  LastRttMs      => _transport?.RttMs ?? 0;

        public bool IsConnected    => _transport?.IsConnected ?? false;

        public bool LastSendFailed => _transport?.LastSendFailed ?? false;

        public bool IsConnectedClient => !_isHost && IsConnected;

        public bool IsHost => _isHost;
        public bool IsHostRunning => _running && _isHost;
        public string TransportName => _transport?.GetType().Name ?? Plugin.Instance.CfgTransport.Value;
        public NetworkDiagnosticsSnapshot Diagnostics => _transport?.Diagnostics ?? default;
        public long StateUpdateSentCount => _stateUpdateSentCount;
        public long LargeStateUpdateSentCount => _largeStateUpdateSentCount;
        public int LastStateUpdateBytes => _lastStateUpdateBytes;
        public int LastStateUpdateUnits => _lastStateUpdateUnits;
        public int LastStateUpdateProjectiles => _lastStateUpdateProjectiles;

        public void StartHost(int port)
        {
            if (_running)
                Stop();

            Plugin.Instance.CfgIsHost.Value = true;
            if (port > 0)
                Plugin.Instance.CfgPort.Value = port;

            _isHost = true;
            _transport = CreateTransport();
            WireTransportEvents();
            _transport.Start(asHost: true);
            _running = true;
            MpLog.Info("Net", $"Hosting transport={Plugin.Instance.CfgTransport.Value} port={Plugin.Instance.CfgPort.Value}");
        }

        public void StartClient(string ip, int port)
        {
            if (_running)
                Stop();

            Plugin.Instance.CfgIsHost.Value = false;
            if (!string.IsNullOrWhiteSpace(ip))
                Plugin.Instance.CfgHostIP.Value = ip;
            if (port > 0)
                Plugin.Instance.CfgPort.Value = port;

            _isHost = false;
            _transport = CreateTransport();
            WireTransportEvents();
            _transport.Start(asHost: false);
            _running = true;
            MpLog.Info("Net", $"Connecting transport={Plugin.Instance.CfgTransport.Value} host={Plugin.Instance.CfgHostIP.Value}:{Plugin.Instance.CfgPort.Value}");
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
            Patch_Blastzone_OnHitWeapon.ClearInterceptions();
            CombatEventHandler.ClearDeathWatch();
            Patch_ObjectBase_NotifyDestroyed_PvP.Clear();
            Patch_WeaponBase_CommonLaunchSettings.ClearSpawnTimes();
            _transport?.Stop();
            _transport = null;
            _running = false;
            MpLog.Info("Net", "Stopped.");
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
            LogSerializedSize(msg, _writer.Length, delivery);
            _transport.SendToServer(_writer.Data, _writer.Length, MapDelivery(delivery));
        }

        public void BroadcastToClients(INetMessage msg, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_transport == null) return;
            _writer.Reset();
            _writer.Put((byte)msg.Type);
            msg.Serialize(_writer);
            LogSerializedSize(msg, _writer.Length, delivery);
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

        private void LogSerializedSize(INetMessage msg, int length, DeliveryMethod delivery)
        {
            if (msg.Type != MessageType.StateUpdate) return;

            var state = msg as StateUpdateMessage;
            _stateUpdateSentCount++;
            _lastStateUpdateBytes = length;
            _lastStateUpdateUnits = state?.Units.Count ?? 0;
            _lastStateUpdateProjectiles = state?.Projectiles.Count ?? 0;

            string detail = state == null
                ? $"bytes={length}"
                : $"bytes={length} units={state.Units.Count} projectiles={state.Projectiles.Count}";

            if (length > StateUpdateLargeBytes)
            {
                _largeStateUpdateSentCount++;
                if (Plugin.Instance.CfgTransport.Value == "LiteNetLib" &&
                    length > LiteNetStateUpdateSinglePacketBytes)
                {
                    MpLog.InfoThrottle("StateUpdateWillFragment", "Net",
                        $"StateUpdate will fragment {detail} delivery={delivery} threshold={LiteNetStateUpdateSinglePacketBytes}", 2f);
                }
                else
                {
                    MpLog.Trace("Net", $"Large StateUpdate {detail} delivery={delivery}");
                }
            }
            else
            {
                MpLog.Trace("Net", $"StateUpdate {detail} delivery={delivery}");
            }
        }

        // ── Transport event handlers ────────────────────────────────────────────

        private void OnPeerConnected()
        {
            MpLog.Info("Net", "Peer connected");
        }

        private void OnPeerDisconnected()
        {
            MpLog.Warn("Net", "Peer disconnected");
            _mainThreadQueue.Enqueue(() =>
            {
                OrderDelayQueue.Clear();
                TaskforceAssignmentManager.Reset();
                UnitLockManager.Reset();
                StateApplier.ResetOrphanTracking();
                PvPDeathNotifications.Clear();
                PvPFireAuth.Clear();
                Patch_ObjectBase_HandleEngageTasks.Reset();
                Patch_Blastzone_OnHitUnit.ClearMissileImpacts();
                Patch_Blastzone_OnHitWeapon.ClearInterceptions();
                Patch_WeaponBase_CommonLaunchSettings.ClearSpawnTimes();
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

            if (type != MessageType.StateUpdate && type != MessageType.MissileStateSync
                && type != MessageType.PlayerOrder && type != MessageType.DamageState)
                MpLog.Trace("Net", $"Received {type}");

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
                        if (Plugin.Instance.CfgVerboseDebug.Value)
                            MpLog.Debug("AutoFire", $"t={enqueueMs}ms ENQUEUE (bg thread) " +
                                $"unit={msg.SourceEntityId} ammo={msg.AmmoId} target={msg.TargetEntityId} " +
                                $"shots={msg.ShotsToFire}");
                    }
                    _mainThreadQueue.Enqueue(() =>
                    {
                        if (msg.Order == Messages.OrderType.AutoFireWeapon)
                        {
                            long applyMs = AIAutoFireState.DiagMs;
                            if (Plugin.Instance.CfgVerboseDebug.Value)
                                MpLog.Debug("AutoFire", $"t={applyMs}ms DEQUEUE (main thread, " +
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
                    MpLog.Debug("Net", $"Deserialized CombatEvent: {msg.EventType} target={msg.TargetEntityId} source={msg.SourceEntityId}");
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
                    // In co-op, only the host spawns projectiles and only the client
                    // should receive these messages. If the host receives one (e.g.
                    // from a client running a stale/mismatched PvP config) dropping
                    // it here prevents the ForceSpawn→Postfix→broadcast feedback
                    // loop that produces infinite interceptors.
                    if (Plugin.Instance.CfgIsHost.Value && !Plugin.Instance.CfgPvP.Value)
                    {
                        MpLog.Warn("Net", $"Dropping unexpected ProjectileSpawn on co-op host (source unit {msg.SourceUnitId}, ammo={msg.AmmoName})");
                        break;
                    }
                    _mainThreadQueue.Enqueue(() => ProjectileIdMapper.OnHostSpawnReceived(
                        msg.HostProjectileId, msg.SourceUnitId, msg.AmmoName,
                        msg.TargetEntityId, msg.TargetX, msg.TargetY, msg.TargetZ,
                        msg.LaunchDirX, msg.LaunchDirY, msg.LaunchDirZ));
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

                case MessageType.AircraftRecoveryRequest:
                {
                    var msg = AircraftRecoveryRequestMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => FlightOpsHandler.HandleRecoveryRequest(msg));
                    break;
                }

                case MessageType.AircraftRecoveryResponse:
                {
                    var msg = AircraftRecoveryResponseMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => FlightOpsHandler.HandleRecoveryResponse(msg));
                    break;
                }

                case MessageType.ProjectileReconciliation:
                {
                    var msg = ProjectileReconciliationMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => ProjectileIdMapper.OnReconciliationReceived(msg));
                    break;
                }

                default:
                    MpLog.Warn("Net", $"Unknown message type: {type}");
                    break;
            }
        }
    }
}
