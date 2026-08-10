using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BepInEx.Logging;
using LiteNetLib;
using LiteNetLib.Utils;
using SeapowerMultiplayer.Messages;
using SeapowerMultiplayer.Net2;
using SeapowerMultiplayer.Transport;
using UnityEngine;

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

        // ── v2 handshake state ────────────────────────────────────────────────────
        private HandshakeState _handshake = HandshakeState.Disconnected;
        private float _handshakeDeadline  = -1f;   // realtimeSinceStartup
        private float _refuseDisconnectAt = -1f;   // give the refusal Welcome time to flush
        private const float HandshakeTimeoutSec = 5f;

        public HandshakeState Handshake => _handshake;

        /// <summary>True once the v2 Hello/Welcome handshake completed. All gameplay
        /// traffic (everything except Hello/Welcome) is gated on this.</summary>
        public bool IsEstablished => _running && _handshake == HandshakeState.Established;

        /// <summary>Session parameters received in Welcome (client side only).</summary>
        public WelcomeMessage? SessionParams { get; private set; }

        /// <summary>Set on both sides when a handshake fails on ProtocolVersion.
        /// The F9 overlay shows a centre-screen prompt telling both players to
        /// resubscribe on the Steam Workshop. Cleared on dismiss or a successful
        /// handshake.</summary>
        public static string? VersionMismatchNotice { get; private set; }

        public static void DismissVersionMismatch() => VersionMismatchNotice = null;

        // ── Packet-loss sampling (rolling window for the F9 overlay) ─────────────

        private readonly List<(float time, long sent, long lost)> _lossSamples = new();
        private float _nextLossSampleAt;
        private const float LossWindowSec = 10f;
        private const float LossSampleIntervalSec = 0.5f;

        /// <summary>Send-side packet loss over the last 10 s, in percent.
        /// -1 when the transport exposes no packet counters (Steam) or no peer
        /// is connected.</summary>
        public float PacketLossPct { get; private set; } = -1f;

        private void SamplePacketLoss()
        {
            float now = Time.unscaledTime;
            if (now < _nextLossSampleAt) return;
            _nextLossSampleAt = now + LossSampleIntervalSec;

            if (_transport == null || !_transport.TryGetPacketStats(out long sent, out long lost))
            {
                _lossSamples.Clear();
                PacketLossPct = -1f;
                return;
            }

            // Counters restart with the peer - reset the window instead of going negative.
            int n = _lossSamples.Count;
            if (n > 0 && (sent < _lossSamples[n - 1].sent || lost < _lossSamples[n - 1].lost))
                _lossSamples.Clear();

            _lossSamples.Add((now, sent, lost));
            while (_lossSamples.Count > 0 && _lossSamples[0].time < now - LossWindowSec)
                _lossSamples.RemoveAt(0);

            long dSent = sent - _lossSamples[0].sent;
            long dLost = lost - _lossSamples[0].lost;
            PacketLossPct = dSent > 0 ? 100f * dLost / dSent : 0f;
        }

        // ── Public API ────────────────────────────────────────────────────────────

        public int  LastRttMs      => _transport?.RttMs ?? 0;

        public bool IsConnected    => _transport?.IsConnected ?? false;

        public bool LastSendFailed => _transport?.LastSendFailed ?? false;

        public string? LastSendError => _transport?.LastSendError;

        public bool IsConnectedClient => !_isHost && IsConnected;

        public bool IsHost => _isHost;
        public bool IsHostRunning => _running && _isHost;

        public void StartHost(int port)
        {
            if (_running) Stop(); // clean restart: never overwrite a live transport
            _isHost = true;
            _transport = CreateTransport();
            WireTransportEvents();
            _transport.Start(asHost: true);
            _running = true;
            Log.LogInfo($"[Net] Hosting (transport={Plugin.Instance.CfgTransport.Value})");
        }

        public void StartClient(string ip, int port)
        {
            if (_running) Stop(); // clean restart: never overwrite a live transport
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
            // Tearing the transport down produces a disconnect event we asked for.
            ReconnectManager.NotifyIntentionalDisconnect();
            Patch_Vehicle_UpdateAllData_PvP.ClearCache();
            Patch_ObjectBase_HandleEngageTasks.Reset();
            _transport?.Stop();
            _transport = null;
            _running = false;
            _handshake = HandshakeState.Disconnected;
            _handshakeDeadline = -1f;
            _refuseDisconnectAt = -1f;
            SessionParams = null;
            Log.LogInfo("[Net] Stopped.");
        }

        /// <summary>Called from Plugin.Update() - must run on Unity main thread.</summary>
        public void Tick()
        {
            if (!_running) return;

            _transport?.Poll();
            SamplePacketLoss();

            // Drain queued main-thread actions. One throwing message must not take the
            // drain down with it: nothing upstream catches, so the exception escaped
            // Plugin.Update entirely and skipped the rest of the frame's plugin work
            // (replica driving, carrier ops, telemetry) as well as the queue - for as
            // many frames as it took to grind through a bad burst.
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Log.LogError($"[Net] queued main-thread action threw: {ex}"); }
            }

            // Handshake timeout: peer connected but never completed Hello/Welcome -
            // almost certainly a pre-v2 plugin or an incompatible phase build.
            if (_handshakeDeadline > 0f && Time.realtimeSinceStartup > _handshakeDeadline
                && (_handshake == HandshakeState.AwaitingHello || _handshake == HandshakeState.AwaitingWelcome))
            {
                Log.LogError(_isHost
                    ? "[Handshake] No Hello from peer within timeout — peer likely runs an incompatible plugin version. Disconnecting."
                    : "[Handshake] No Welcome from host within timeout — host likely runs an incompatible plugin version. Disconnecting.");
                _handshakeDeadline = -1f;
                _handshake = HandshakeState.Refused;
                Telemetry.Count("handshake.timeout");
                _transport?.DisconnectPeers();
            }

            // Deferred disconnect after sending a refusal Welcome (lets it flush)
            if (_refuseDisconnectAt > 0f && Time.realtimeSinceStartup > _refuseDisconnectAt)
            {
                _refuseDisconnectAt = -1f;
                _transport?.DisconnectPeers();
            }
        }

        // ── Send helpers ──────────────────────────────────────────────────────────

        public void SendToServer(INetMessage msg, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_transport == null) return;
            if (BlockedPreHandshake(msg.Type)) return;
            if (BlockedByAllyLock(msg)) return;
            _writer.Reset();
            _writer.Put((byte)msg.Type);
            msg.Serialize(_writer);
            _transport.SendToServer(_writer.Data, _writer.Length, MapDelivery(delivery));
            Telemetry.OnSend((byte)msg.Type, _writer.Length);
        }

        public void BroadcastToClients(INetMessage msg, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_transport == null) return;
            if (BlockedPreHandshake(msg.Type)) return;
            if (BlockedByAllyLock(msg)) return;
            _writer.Reset();
            _writer.Put((byte)msg.Type);
            msg.Serialize(_writer);
            _transport.BroadcastToClients(_writer.Data, _writer.Length, MapDelivery(delivery));
            Telemetry.OnSend((byte)msg.Type, _writer.Length);
        }

        /// <summary>
        /// Ally-lock backstop. Order patches are supposed to refuse locally AND not
        /// send, but each one re-implements its own gating and the ones with bespoke
        /// send logic kept forgetting the lock - so an order the local player was
        /// refused still reached the other player, who applied it. That asymmetry is
        /// the worst possible outcome: the two sims diverge silently.
        ///
        /// Catching it here means no order path, present or future, can leak. It is
        /// deliberately narrow: only PlayerOrderMessage, only for the one unit the
        /// remote player holds. Host-authoritative capture events (spawns, impacts,
        /// damage) are not orders and are untouched.
        /// </summary>
        private bool BlockedByAllyLock(INetMessage msg)
        {
            if (msg.Type != MessageType.PlayerOrder) return false;
            if (msg is not PlayerOrderMessage order) return false;
            // Not a command to the unit: ClassifyContact marks a CONTACT hostile or
            // neutral, and its SourceEntityId is that contact. A partner who has an
            // enemy contact selected would otherwise block our classification of it -
            // and since that path applies locally without asking the lock, blocking
            // only the send would desync the very thing this guard exists to prevent.
            if (order.Order == OrderType.ClassifyContact) return false;
            if (!UnitLockManager.IsLockedByRemote(order.SourceEntityId)) return false;
            if (Plugin.Instance.CfgPvP.Value) return false;
            if (OrderHandler.ApplyingFromNetwork) return false;

            Telemetry.Count("net.sendBlockedByAllyLock");
            return true;
        }

        /// <summary>Everything except Hello/Welcome waits for the handshake.</summary>
        private bool BlockedPreHandshake(MessageType type)
        {
            if (type == MessageType.Hello || type == MessageType.Welcome) return false;
            if (_handshake == HandshakeState.Established) return false;
            Telemetry.Count("net.sendBlockedPreHandshake");
            return true;
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
            _transport.OnReceiveFailed += OnReceiveFailed;
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
            _mainThreadQueue.Enqueue(() =>
            {
                // A new peer means a new attempt - don't carry a stale failure
                // banner from the previous session into this one.
                SimSyncManager.ClearIssue();

                if (_isHost)
                {
                    _handshake = HandshakeState.AwaitingHello;
                    _handshakeDeadline = Time.realtimeSinceStartup + HandshakeTimeoutSec;
                    Log.LogInfo("[Handshake] Awaiting client Hello...");
                }
                else
                {
                    var hello = new HelloMessage
                    {
                        ProtocolVersion = ProtocolInfo.ProtocolVersion,
                        PluginVersion   = PluginInfo.PLUGIN_VERSION,
                        IsPvP           = Plugin.Instance.CfgPvP.Value,
                        GameVersion     = ProtocolInfo.GameVersion,
                        GameplayOptions = RemoteGameplayOptions.PackLocal(),
                        ModFingerprint  = ModSetCheck.LocalFingerprint(),
                        ModCount        = (byte)Mathf.Min(ModSetCheck.LocalMods().Count, 255),
                    };
                    ModSetCheck.LogLocal("client");
                    _handshake = HandshakeState.AwaitingWelcome;
                    _handshakeDeadline = Time.realtimeSinceStartup + HandshakeTimeoutSec;
                    SendToServer(hello);
                    Log.LogInfo($"[Handshake] Hello sent (protocol {ProtocolInfo.ProtocolVersion}, pvp={hello.IsPvP}); awaiting Welcome...");
                }
            });
        }

        private void OnPeerDisconnected()
        {
            Log.LogInfo("[Net] Peer disconnected");
            _mainThreadQueue.Enqueue(() =>
            {
                // Captured before the reset below: only a peer that got as far as
                // Established was in a session worth freezing for.
                bool wasEstablished = _handshake == HandshakeState.Established;

                _handshake = HandshakeState.Disconnected;
                _handshakeDeadline = -1f;
                _refuseDisconnectAt = -1f;
                SessionParams = null;
                UnitReplicaDriver.Reset();
                AircraftReplicaDriver.Reset();
                DeckPuppetDriver.Reset();
                CarrierOpsHandler.Reset();
                WeaponHatchHandler.Reset();
                FlightDeckStreamer.Reset();
                FlightDeckStateApplier.Reset();
                RemoteGameplayOptions.Reset();
                ViewportHintSender.Reset();
                HostEntityStreamer.ClearViewportHint();
                SpawnReplicator.Reset();
                WeaponReplicaDriver.Reset();
                UnitIdentityApplier.Reset();
                EntityCensusManager.Reset();
                Patch_V2_MissionEnd_Capture.Reset();
                GuestIdFloor.Disarm();
                CaptureState.Clear();
                HatchStateCapture.Clear();
                ReplicaRegistry.Clear();
                Suppression.EnforceDefenseFlag(); // restores client auto-defence
                Suppression.EnforceInterceptSymmetry(); // restores the difficulty handicap
                TaskforceAssignmentManager.Reset();
                ContactSyncManager.Reset();
                ContactRevealManager.Reset();
                DrawingSyncManager.Reset();
                SensorStateManager.Reset();
                JamStateManager.Reset();
                UnitStatusManager.Reset();
                UnitLockManager.Reset();
                AttackDesignationSync.Reset();
                WeaponStatusSync.Reset();
                StateApplier.ResetOrphanTracking();
                Patch_Vehicle_UpdateAllData_PvP.ClearCache();
                Patch_ObjectBase_HandleEngageTasks.Reset();
                // Remote-owner speed locks: same mission reloaded reuses UniqueIDs,
                // so a stale entry would lock a ship's telegraph next session.
                Patch_Vessel_SetTelegraph.Reset();
                Patch_Submarine_SetTelegraph.Reset();
                Patch_Compartments_UpdateWantedVelocityInKnots.ClearLogCache();
                Patch_V2_Compartments_Sink.ClearLogCache();
                Patch_Vessel_ApplyRudderThrust.ClearLogCache();
                Patch_VesselPropulsionSystem_OnUpdate.ClearLogCache();

                // Last: the resets above have already handed local control back,
                // so this is what stops the client drifting into a solo game.
                ReconnectManager.OnPeerLost(wasEstablished);
            });
        }

        /// <summary>
        /// A message was abandoned part-way through reassembly. The sender saw a
        /// successful send and will not retry, so the only recovery is a fresh
        /// Send from the host — surface that instead of failing silently.
        /// </summary>
        private void OnReceiveFailed(string reason)
        {
            Log.LogError($"[Net] Inbound message lost: {reason}");
            _mainThreadQueue.Enqueue(() =>
            {
                SimSyncManager.ReportIssue(
                    "SYNC FAILED — the game data never finished arriving.",
                    $"{reason} Ask the host to press Send again.");
                SimSyncManager.Reset();
            });
        }

        private void OnDataReceived(byte[] data, int length)
        {
            var reader = new NetDataReader(data, 0, length);
            var type = (MessageType)reader.GetByte();
            Telemetry.OnReceive((byte)type, length);

            // Handshake gate: until Established, only Hello (host) / Welcome (client)
            // are processed; everything else is dropped.
            if (_handshake != HandshakeState.Established)
            {
                HandlePreHandshake(type, reader);
                return;
            }

            if (type != MessageType.PlayerOrder && type != MessageType.DamageState)
                Log.LogDebug($"[Net] Received {type}");

            // One malformed message must not abort the transport poll loop (an
            // exception here propagates out of PollEvents and discards the rest of
            // the frame's event batch, reliable deliveries included). Log and move on.
            try
            {
                Dispatch(type, reader);
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[Net] Failed to handle {type} (len={length}): {ex}");
            }
        }

        private void Dispatch(MessageType type, NetDataReader reader)
        {
            switch (type)
            {
                case MessageType.EntityStateBatch:
                {
                    var msg = EntityStateBatchMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => UnitReplicaDriver.Apply(msg));
                    break;
                }

                case MessageType.EntitySpawn:
                {
                    var msg = EntitySpawnMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => SpawnReplicator.HandleSpawn(msg));
                    break;
                }

                case MessageType.EntityDespawn:
                {
                    var msg = EntityDespawnMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => SpawnReplicator.HandleDespawn(msg));
                    break;
                }

                case MessageType.DeckState:
                {
                    var msg = DeckStateMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => DeckPuppetDriver.OnDeckState(msg));
                    break;
                }

                case MessageType.FlightOpsAnim:
                {
                    var msg = FlightOpsAnimMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => CarrierOpsHandler.HandleAnim(msg));
                    break;
                }

                case MessageType.WeaponHatchEvent:
                {
                    var msg = WeaponHatchEventMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => WeaponHatchHandler.Handle(msg));
                    break;
                }

                case MessageType.FlightDeckState:
                {
                    var msg = FlightDeckStateMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => FlightDeckStateApplier.Apply(msg));
                    break;
                }

                case MessageType.ImpactEvent:
                {
                    var msg = ImpactEventMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => SpawnReplicator.HandleImpact(msg));
                    break;
                }

                case MessageType.DestroyEvent:
                {
                    var msg = DestroyEventMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => SpawnReplicator.HandleDestroyEvent(msg));
                    break;
                }

                case MessageType.GunBurstEvent:
                {
                    var msg = GunBurstEventMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => CosmeticEventHandler.HandleGunBurst(msg));
                    break;
                }

                case MessageType.AmmoStateEvent:
                {
                    var msg = AmmoStateEventMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => CosmeticEventHandler.HandleAmmoState(msg));
                    break;
                }

                case MessageType.EntityCensus:
                {
                    var msg = EntityCensusMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => EntityCensusManager.HandleCensus(msg));
                    break;
                }

                case MessageType.CensusDiffRequest:
                {
                    var msg = CensusDiffRequestMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => EntityCensusManager.HandleDiffRequest(msg));
                    break;
                }

                case MessageType.PlayerOrder:
                {
                    var msg = PlayerOrderMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => OrderHandler.Apply(msg));
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

                case MessageType.ViewportHint:
                {
                    var msg = ViewportHintMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => HostEntityStreamer.OnViewportHint(msg));
                    break;
                }

                case MessageType.ContactSync:
                {
                    var msg = ContactSyncMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => ContactSyncManager.ApplyReceived(msg));
                    break;
                }

                case MessageType.ContactReport:
                {
                    var msg = ContactReportMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => ContactRevealManager.ApplyReceived(msg));
                    break;
                }

                case MessageType.DrawingSync:
                {
                    var msg = DrawingSyncMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => DrawingSyncManager.ApplyReceived(msg));
                    break;
                }

                case MessageType.SensorState:
                {
                    var msg = SensorStateMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => SensorStateManager.ApplyReceived(msg));
                    break;
                }

                case MessageType.UnitStatus:
                {
                    var msg = UnitStatusMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => UnitStatusManager.ApplyReceived(msg));
                    break;
                }

                case MessageType.JamState:
                {
                    var msg = JamStateMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => JamStateManager.ApplyReceived(msg));
                    break;
                }

                case MessageType.SessionReady:
                {
                    var msg = SessionReadyMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() =>
                    {
                        SimSyncManager.OnClientReady();
                        ReconnectManager.OnClientResynced();
                    });
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

                default:
                    Log.LogWarning($"[Net] Unknown message type: {type}");
                    break;
            }
        }

        // ── v2 handshake ──────────────────────────────────────────────────────────

        private void HandlePreHandshake(MessageType type, NetDataReader reader)
        {
            // No synchronous _handshake check here: OnPeerConnected QUEUES the
            // AwaitingHello/AwaitingWelcome transition, so when the peer's Hello
            // arrives in the same Poll batch as the connect event (host frame
            // hitch during boot, localhost RTT) the state still reads
            // Disconnected and the Hello would be dropped - both sides then sit
            // out the 5 s timeout. Enqueue the handler instead; FIFO order puts
            // it after the state transition, and HandleHello/HandleWelcome do
            // the authoritative state check on the main thread.
            if (type == MessageType.Hello && _isHost)
            {
                var msg = HelloMessage.Deserialize(reader);
                _mainThreadQueue.Enqueue(() => HandleHello(msg));
            }
            else if (type == MessageType.Welcome && !_isHost)
            {
                var msg = WelcomeMessage.Deserialize(reader);
                _mainThreadQueue.Enqueue(() => HandleWelcome(msg));
            }
            else
            {
                Telemetry.Count("net.droppedPreHandshake");
                Log.LogDebug($"[Handshake] Dropped {type} (state={_handshake})");
            }
        }

        private void HandleHello(HelloMessage msg)
        {
            if (_handshake != HandshakeState.AwaitingHello) return;

            string? refusal = null;
            if (msg.ProtocolVersion != ProtocolInfo.ProtocolVersion)
            {
                refusal = $"Protocol mismatch: host v{ProtocolInfo.ProtocolVersion}, client v{msg.ProtocolVersion}. Both players need the same mod version.";
                VersionMismatchNotice = refusal;
            }
            // Game build must match before anything else gameplay-related: saves embed
            // per-vessel indices (flight deck elevators, recovery points) that shift
            // between builds, so syncing one to a mismatched client throws inside the
            // game's own FlightDeck loader and hangs it on the loading screen forever.
            // Refusing here is the only point where that is still explainable.
            else if (!string.IsNullOrEmpty(msg.GameVersion) && msg.GameVersion != ProtocolInfo.GameVersion)
            {
                refusal = $"Sea Power version mismatch: host is on {ProtocolInfo.GameVersion}, client is on {msg.GameVersion}. " +
                          "Both players must run the same game build — update through Steam and restart.";
                VersionMismatchNotice = refusal;
            }
            else if (msg.IsPvP != Plugin.Instance.CfgPvP.Value)
                refusal = $"Mode mismatch: host is {(Plugin.Instance.CfgPvP.Value ? "PvP" : "co-op")}, client is {(msg.IsPvP ? "PvP" : "co-op")}.";

            if (refusal != null)
            {
                Log.LogError($"[Handshake] Refusing client (plugin {msg.PluginVersion}, game {msg.GameVersion}): {refusal}");
                Telemetry.Count("handshake.refused");
                BroadcastToClients(new WelcomeMessage { Accepted = false, RefusalReason = refusal });
                _handshake = HandshakeState.Refused;
                _handshakeDeadline = -1f;
                _refuseDisconnectAt = Time.realtimeSinceStartup + 0.75f;
                return;
            }

            _handshake = HandshakeState.Established;
            _handshakeDeadline = -1f;
            VersionMismatchNotice = null;

            // Only after the refusal checks above: a client that is going to be turned
            // away has no options worth adopting, and its byte may not even mean what
            // this build thinks it does.
            RemoteGameplayOptions.Apply(msg.GameplayOptions);

            BroadcastToClients(new WelcomeMessage
            {
                Accepted        = true,
                IsPvP           = Plugin.Instance.CfgPvP.Value,
                ClientUidBase   = ProtocolInfo.ClientUidBase,
                StateRateHz     = 10,
                GameplayOptions = RemoteGameplayOptions.PackLocal(),
                ModFingerprint  = ModSetCheck.LocalFingerprint(),
                ModCount        = (byte)Mathf.Min(ModSetCheck.LocalMods().Count, 255),
            });

            // After the clear above, not before: acceptance resets the notice, and this
            // is a warning that has to survive it. A mod mismatch does not refuse - it
            // is allowed to be a cosmetic pack - but it is the likeliest explanation for
            // the desyncs that follow, so both players are told.
            ModSetCheck.LogLocal("host");
            var modWarning = ModSetCheck.Compare(msg.ModFingerprint, msg.ModCount);
            if (modWarning != null)
            {
                Telemetry.Count("handshake.modMismatch");
                Log.LogWarning($"[Mods] {modWarning}");
                VersionMismatchNotice = modWarning;
            }
            Log.LogInfo($"[Handshake] Client accepted (plugin {msg.PluginVersion}, protocol {msg.ProtocolVersion}, game {ProtocolInfo.GameVersion}). Established.");
            ReconnectManager.OnPeerEstablished();
        }

        private void HandleWelcome(WelcomeMessage msg)
        {
            if (_handshake != HandshakeState.AwaitingWelcome) return;
            _handshakeDeadline = -1f;

            if (!msg.Accepted)
            {
                Log.LogError($"[Handshake] Host refused connection: {msg.RefusalReason}");
                Telemetry.Count("handshake.refused");
                // The host's build generated this string, so the prefix check works
                // against both older and newer hosts.
                if (msg.RefusalReason.StartsWith("Protocol mismatch")
                 || msg.RefusalReason.StartsWith("Sea Power version mismatch"))
                    VersionMismatchNotice = msg.RefusalReason;
                _handshake = HandshakeState.Refused;
                Stop();
                return;
            }

            SessionParams = msg;
            _handshake = HandshakeState.Established;
            VersionMismatchNotice = null;
            RemoteGameplayOptions.Apply(msg.GameplayOptions);

            // See the host half in HandleHello - both ends warn, so whichever player is
            // looking at their own screen when things go strange has the explanation.
            var modWarning = ModSetCheck.Compare(msg.ModFingerprint, msg.ModCount);
            if (modWarning != null)
            {
                Telemetry.Count("handshake.modMismatch");
                Log.LogWarning($"[Mods] {modWarning}");
                VersionMismatchNotice = modWarning;
            }
            // Before the session load starts, which is the point - the guest allocates
            // ids all the way through a load, so a floor armed afterwards is too late.
            GuestIdFloor.Arm(msg.ClientUidBase);
            Log.LogInfo($"[Handshake] Established (pvp={msg.IsPvP}, uidBase={msg.ClientUidBase}, stateRate={msg.StateRateHz}Hz).");
            ReconnectManager.OnPeerEstablished();
        }
    }
}
