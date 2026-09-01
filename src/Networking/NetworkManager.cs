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
        //
        // PER PEER on the host. It used to be one scalar for the whole manager, which
        // worked only because there was exactly one guest: a second peer connecting reset
        // it to AwaitingHello and clobbered the first peer's Established, which then
        // blocked every outbound message through BlockedPreHandshake. Worse, a refusal
        // called DisconnectPeers() and kicked the healthy players along with the
        // incompatible joiner.
        private sealed class PeerSession
        {
            public PeerId Peer;
            public HandshakeState State;
            public float Deadline = -1f;            // realtimeSinceStartup
            public float RefuseDisconnectAt = -1f;  // let the refusal Welcome flush first
            public byte Slot;
        }

        private readonly Dictionary<PeerId, PeerSession> _peers = new();

        /// <summary>A guest has exactly one peer (the host), so its state stays a
        /// scalar.</summary>
        private HandshakeState _clientHandshake = HandshakeState.Disconnected;
        private float _clientDeadline = -1f;

        /// <summary>How many peers have completed the handshake this session. Only the
        /// first one's gameplay options are adopted.</summary>
        private int _establishedCount;

        private const float HandshakeTimeoutSec = 5f;

        /// <summary>
        /// UI-facing summary for the overlay's single status line.
        ///
        /// Established wins outright rather than being picked by enum order: Refused
        /// sorts above it, so "someone was turned away" would otherwise mask a session
        /// that is up and running perfectly well for everyone else.
        /// </summary>
        public HandshakeState Handshake
        {
            get
            {
                if (!_isHost) return _clientHandshake;
                var best = HandshakeState.Disconnected;
                foreach (var s in _peers.Values)
                {
                    if (s.State == HandshakeState.Established) return HandshakeState.Established;
                    if (s.State > best) best = s.State;
                }
                return best;
            }
        }

        /// <summary>
        /// True once at least one peer completed the v2 handshake. All gameplay traffic
        /// (everything except Hello/Welcome) is gated on this.
        ///
        /// "At least one" preserves the meaning every existing call site relies on: they
        /// gate host streaming on "is anyone listening", which is exactly this.
        /// </summary>
        public bool IsEstablished
        {
            get
            {
                if (!_running) return false;
                if (!_isHost) return _clientHandshake == HandshakeState.Established;
                foreach (var s in _peers.Values)
                    if (s.State == HandshakeState.Established) return true;
                return false;
            }
        }

        /// <summary>
        /// The name other players see in the roster and the send-to-player menu.
        ///
        /// Configured name first, then the Steam persona, then "" - which the registry
        /// renders as "Player N". The explicit setting wins over Steam deliberately: its
        /// main job is telling two instances on ONE machine apart while testing, and both
        /// of those are signed in as the same Steam account, so deferring to the persona
        /// would give them the same name.
        /// </summary>
        internal static string LocalPersonaName()
        {
            string configured = Plugin.Instance.CfgUsername.Value?.Trim() ?? "";
            if (configured.Length > 0) return Sanitize(configured);

            try
            {
                if (Plugin.Instance.CfgTransport.Value == "Steam")
                    return Sanitize(Steamworks.SteamFriends.GetPersonaName() ?? "");
            }
            catch (Exception) { /* Steam not initialised - fall through to the slot name */ }
            return "";
        }

        /// <summary>Names go on the wire and into game menu labels, so cap the length and
        /// drop control characters - a pasted newline would otherwise break the roster
        /// row and the context-menu entry it ends up in.</summary>
        private static string Sanitize(string name)
        {
            const int MaxNameChars = 32;
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsControl(c)) continue;
                sb.Append(c);
                if (sb.Length >= MaxNameChars) break;
            }
            return sb.ToString().Trim();
        }

        /// <summary>Has THIS peer finished handshaking? The per-peer question the
        /// dispatch gate and the roster need.</summary>
        public bool IsEstablishedFor(PeerId peer)
        {
            if (!_isHost) return _clientHandshake == HandshakeState.Established;
            return _peers.TryGetValue(peer, out var s) && s.State == HandshakeState.Established;
        }

        /// <summary>Every peer past the handshake. The audience for a broadcast - a peer
        /// still handshaking must not receive the entity stream.</summary>
        public IEnumerable<PeerId> EstablishedPeers
        {
            get
            {
                foreach (var s in _peers.Values)
                    if (s.State == HandshakeState.Established) yield return s.Peer;
            }
        }

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

            if (!TryGetAggregatePacketStats(out long sent, out long lost))
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

        /// <summary>Sum the per-peer counters into the single figure the overlay shows.
        /// A peer leaving makes the totals fall, which the caller's "counters went
        /// backwards" guard already treats as a window reset.</summary>
        private bool TryGetAggregatePacketStats(out long sent, out long lost)
        {
            sent = 0;
            lost = 0;
            if (_transport == null) return false;

            bool any = false;
            foreach (var peer in _transport.ConnectedPeers)
            {
                if (!_transport.TryGetPacketStats(peer, out long s, out long l)) continue;
                sent += s;
                lost += l;
                any = true;
            }
            return any;
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
            _establishedCount = 0;
            // Seat ourselves before anyone can connect: the host is a player too now, and
            // slot 0 / Blue is what every ownership and routing decision is measured
            // against.
            PlayerRegistry.HostInit(LocalPersonaName());
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
            _peers.Clear();
            _clientHandshake = HandshakeState.Disconnected;
            PlayerRegistry.Reset();
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

            TickHandshakeDeadlines();
        }

        /// <summary>
        /// Handshake timeouts and deferred refusals, PER PEER.
        ///
        /// Every disconnect here names one peer. The old code called DisconnectPeers(),
        /// so one joiner running an incompatible build - or simply never answering - took
        /// down every player already in the session with it.
        /// </summary>
        private void TickHandshakeDeadlines()
        {
            float now = Time.realtimeSinceStartup;

            if (!_isHost)
            {
                if (_clientHandshake == HandshakeState.AwaitingWelcome
                    && _clientDeadline > 0f && now > _clientDeadline)
                {
                    Log.LogError("[Handshake] No Welcome from host within timeout — host likely runs an incompatible plugin version. Disconnecting.");
                    _clientDeadline = -1f;
                    _clientHandshake = HandshakeState.Refused;
                    Telemetry.Count("handshake.timeout");
                    _transport?.DisconnectPeers();
                }
                return;
            }

            List<PeerId>? drop = null;
            foreach (var s in _peers.Values)
            {
                if (s.State == HandshakeState.AwaitingHello && s.Deadline > 0f && now > s.Deadline)
                {
                    Log.LogError($"[Handshake] No Hello from {s.Peer} within timeout — that player likely runs an incompatible plugin version. Disconnecting them.");
                    s.Deadline = -1f;
                    s.State = HandshakeState.Refused;
                    Telemetry.Count("handshake.timeout");
                    (drop ??= new List<PeerId>()).Add(s.Peer);
                }
                else if (s.RefuseDisconnectAt > 0f && now > s.RefuseDisconnectAt)
                {
                    s.RefuseDisconnectAt = -1f;
                    (drop ??= new List<PeerId>()).Add(s.Peer);
                }
            }

            if (drop == null) return;
            foreach (var peer in drop)
            {
                _transport?.Disconnect(peer, "handshake refused");
                _peers.Remove(peer);
            }
        }

        // ── Send helpers ──────────────────────────────────────────────────────────

        public void SendToServer(INetMessage msg, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_transport == null) return;
            if (BlockedPreHandshake(msg.Type)) return;
            if (BlockedByOwnership(msg)) return;
            if (!Serialize(msg)) return;
            _transport.SendToServer(_writer.Data, _writer.Length, MapDelivery(delivery));
            Telemetry.OnSend((byte)msg.Type, _writer.Length);
        }

        /// <summary>
        /// Send to every ESTABLISHED peer.
        ///
        /// Deliberately no longer the transport's own broadcast: that sprayed to every
        /// connected socket, including a peer still mid-handshake, which made the
        /// per-peer handshake gate one-directional. Looping established peers is what
        /// makes it real.
        /// </summary>
        public void BroadcastToClients(INetMessage msg, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_transport == null) return;
            if (BlockedPreHandshake(msg.Type)) return;
            if (BlockedByOwnership(msg)) return;
            if (!Serialize(msg)) return;

            var dm = MapDelivery(delivery);
            foreach (var s in _peers.Values)
            {
                if (s.State != HandshakeState.Established) continue;
                _transport.SendTo(s.Peer, _writer.Data, _writer.Length, dm);
            }
            Telemetry.OnSend((byte)msg.Type, _writer.Length);
        }

        /// <summary>
        /// Host: send only to players on one team.
        ///
        /// The audience question the old co-op/PvP flag was standing in for. A shared
        /// contact picture, a map drawing or a relayed order is its team's business and
        /// nobody else's - sending it to the other side leaks intent that their sensors
        /// have not earned.
        ///
        /// GUESTS NEVER CALL THIS. They always SendToServer and let the host pick the
        /// audience; a guest has no connection to its teammates to send on.
        /// </summary>
        public void SendToTeam(Team team, INetMessage msg, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
            => SendToTeamExcept(team, PlayerRegistry.NoSender, msg, delivery);

        /// <summary>Host: <see cref="SendToTeam"/> minus one slot - the relay case, where
        /// the sender must not be told its own message.</summary>
        public void SendToTeamExcept(Team team, byte exceptSlot, INetMessage msg,
                                     DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_transport == null || !_isHost) return;
            if (BlockedPreHandshake(msg.Type)) return;
            if (!Serialize(msg)) return;

            var dm = MapDelivery(delivery);
            foreach (var s in _peers.Values)
            {
                if (s.State != HandshakeState.Established) continue;
                if (s.Slot == exceptSlot) continue;
                if (!PlayerRegistry.TryGet(s.Slot, out var p) || p.Team != team) continue;
                _transport.SendTo(s.Peer, _writer.Data, _writer.Length, dm);
            }
            Telemetry.OnSend((byte)msg.Type, _writer.Length);
        }

        /// <summary>Which player a peer is, or <see cref="PlayerRegistry.NoSender"/> when
        /// the peer is unknown (guest, or a message that arrived mid-teardown).
        ///
        /// Derived from the CONNECTION, never from a field in the message. A slot the
        /// sender wrote itself would be trivially forgeable, and every ownership decision
        /// downstream hangs off this answer.</summary>
        public byte SlotOf(PeerId peer)
        {
            if (!_isHost) return PlayerRegistry.NoSender;
            return _peers.TryGetValue(peer, out var s) ? s.Slot : PlayerRegistry.NoSender;
        }

        /// <summary>
        /// Host: pass a guest-originated message on to the REST of that guest's team.
        ///
        /// Guests have no connection to each other, so without this a second player on a
        /// team never learns what their teammate did - they only see the resulting motion
        /// in the entity stream, and nothing at all for orders with no kinematic effect
        /// (weapon status, EMCON, waypoint lists, formation ops, classification).
        ///
        /// Team-scoped, not broadcast: relaying an order to the opposing side would leak
        /// intent their sensors have not earned - they would learn a course change before
        /// they could possibly detect it.
        /// </summary>
        public void RelayToTeam(byte senderSlot, INetMessage msg,
                                DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (!_isHost) return;                                   // guests never relay
            if (senderSlot == PlayerRegistry.NoSender) return;
            if (!PlayerRegistry.TryGet(senderSlot, out var sender)) return;
            if (PlayerRegistry.TeammateCount(senderSlot) == 0) return;   // nobody to tell
            SendToTeamExcept(sender.Team, senderSlot, msg, delivery);
            Telemetry.Count("net.relayedToTeam");
        }

        /// <summary>Host: pass a guest-originated message on to every OTHER peer,
        /// regardless of team. For genuinely global things - time votes.</summary>
        public void RelayToAll(byte senderSlot, INetMessage msg,
                               DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (!_isHost) return;
            if (_transport == null) return;
            if (BlockedPreHandshake(msg.Type)) return;
            if (!Serialize(msg)) return;

            var dm = MapDelivery(delivery);
            foreach (var s in _peers.Values)
            {
                if (s.State != HandshakeState.Established) continue;
                if (s.Slot == senderSlot) continue;
                _transport.SendTo(s.Peer, _writer.Data, _writer.Length, dm);
            }
            Telemetry.OnSend((byte)msg.Type, _writer.Length);
            Telemetry.Count("net.relayedToAll");
        }

        /// <summary>Host: send to one player by slot.</summary>
        public void SendToSlot(byte slot, INetMessage msg, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            foreach (var s in _peers.Values)
            {
                if (s.Slot != slot) continue;
                SendTo(s.Peer, msg, delivery);
                return;
            }
        }

        /// <summary>
        /// Fill the shared writer once so a fan-out serializes a single time, stamping
        /// the originating player on the way past.
        ///
        /// Central on purpose: GameEvents are relayed, so the originator has to travel
        /// WITH the message, and there are twenty send sites that would each have had to
        /// remember. Only an unstamped message is claimed - one the host is relaying
        /// already carries the true sender and must pass through untouched.
        /// </summary>
        private bool Serialize(INetMessage msg)
        {
            if (msg is GameEventMessage ge && ge.Slot == PlayerRegistry.NoSender)
                ge.Slot = PlayerRegistry.LocalSlot;

            _writer.Reset();
            _writer.Put((byte)msg.Type);
            msg.Serialize(_writer);
            return true;
        }

        /// <summary>
        /// Send to exactly one peer. The only way to deliver per-recipient data - the
        /// handshake verdict, a joiner's own slot and UID band, and (from the
        /// mid-mission join work) a session save meant for one player.
        ///
        /// Deliberately NOT gated by BlockedPreHandshake: its whole purpose includes
        /// answering a peer that has not finished handshaking yet.
        /// </summary>
        public void SendTo(PeerId peer, INetMessage msg, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_transport == null) return;
            if (!peer.IsValid) return;
            if (!Serialize(msg)) return;
            _transport.SendTo(peer, _writer.Data, _writer.Length, MapDelivery(delivery));
            Telemetry.OnSend((byte)msg.Type, _writer.Length);
        }

        /// <summary>
        /// Ownership backstop. Order patches are supposed to refuse locally AND not
        /// send, but each one re-implements its own gating and the ones with bespoke
        /// send logic kept forgetting - so an order the local player was refused still
        /// reached the other players, who applied it. That asymmetry is the worst
        /// possible outcome: the sims diverge silently.
        ///
        /// Catching it here means no order path, present or future, can leak. It is
        /// deliberately narrow: only PlayerOrderMessage, only for units somebody else
        /// owns. Host-authoritative capture events (spawns, impacts, damage) are not
        /// orders and are untouched.
        /// </summary>
        private bool BlockedByOwnership(INetMessage msg)
        {
            if (msg.Type != MessageType.PlayerOrder) return false;
            if (msg is not PlayerOrderMessage order) return false;
            // Not a command to the unit: ClassifyContact marks a CONTACT hostile or
            // neutral, and its SourceEntityId is that contact. A partner who has an
            // enemy contact selected would otherwise block our classification of it -
            // and since that path applies locally without asking the lock, blocking
            // only the send would desync the very thing this guard exists to prevent.
            if (order.Order == OrderType.ClassifyContact) return false;
            if (OrderHandler.ApplyingFromNetwork) return false;
            if (!FormationOwnership.BlocksOrdersFor(StateSerializer.FindById(order.SourceEntityId))) return false;

            Telemetry.Count("net.sendBlockedByOwnership");
            return true;
        }

        /// <summary>Everything except Hello/Welcome waits for the handshake.</summary>
        private bool BlockedPreHandshake(MessageType type)
        {
            if (type == MessageType.Hello || type == MessageType.Welcome) return false;
            if (IsEstablished) return false;
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

        /// <summary>
        /// Tell MY TEAM, whichever role I am.
        ///
        /// A guest cannot address its teammates directly, so it sends upstream and the
        /// host's relay picks the audience; a host addresses its own team directly. For
        /// anything that is a side's private business - which unit I have selected, my
        /// map plot - where SendToOther would have handed it to the opposition.
        /// </summary>
        public void SendToMyTeam(INetMessage msg, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
        {
            if (_isHost) SendToTeam(PlayerRegistry.LocalTeam, msg, delivery);
            else SendToServer(msg, delivery);
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

        private void OnPeerConnected(PeerId peer)
        {
            Log.LogInfo($"[Net] Peer connected: {peer}");
            _mainThreadQueue.Enqueue(() =>
            {
                // A new peer means a new attempt - don't carry a stale failure
                // banner from the previous session into this one.
                SimSyncManager.ClearIssue();

                if (_isHost)
                {
                    // Per peer: a second joiner arriving must not touch the first's state.
                    _peers[peer] = new PeerSession
                    {
                        Peer = peer,
                        State = HandshakeState.AwaitingHello,
                        Deadline = Time.realtimeSinceStartup + HandshakeTimeoutSec,
                    };
                    Log.LogInfo($"[Handshake] Awaiting Hello from {peer}...");
                }
                else
                {
                    var requested = SteamLobbyManager.PendingTeamForJoin ?? Team.Blue;
                    PlayerRegistry.GuestInit(LocalPersonaName(), requested);

                    var hello = new HelloMessage
                    {
                        ProtocolVersion = ProtocolInfo.ProtocolVersion,
                        PluginVersion   = PluginInfo.PLUGIN_VERSION,
                        RequestedTeam   = (byte)requested,
                        GameVersion     = ProtocolInfo.GameVersion,
                        GameplayOptions = RemoteGameplayOptions.PackLocal(),
                        ModFingerprint  = ModSetCheck.LocalFingerprint(),
                        ModCount        = (byte)Mathf.Min(ModSetCheck.LocalMods().Count, 255),
                        DisplayName     = LocalPersonaName(),
                    };
                    ModSetCheck.LogLocal("client");
                    _clientHandshake = HandshakeState.AwaitingWelcome;
                    _clientDeadline = Time.realtimeSinceStartup + HandshakeTimeoutSec;
                    SendToServer(hello);
                    Log.LogInfo($"[Handshake] Hello sent (protocol {ProtocolInfo.ProtocolVersion}, team={requested}); awaiting Welcome...");
                }
            });
        }

        private void OnPeerDisconnected(PeerId peer)
        {
            Log.LogInfo($"[Net] Peer disconnected: {peer}");
            _mainThreadQueue.Enqueue(() =>
            {
                // Captured before the reset below: only a peer that got as far as
                // Established was in a session worth freezing for.
                bool wasEstablished = IsEstablishedFor(peer);
                byte slot = SlotOf(peer);

                _peers.Remove(peer);
                // Back to zero means the next joiner is once again the first, and its
                // gameplay options should be adopted - otherwise a guest who dropped and
                // reconnected would silently run the session on stale settings.
                if (wasEstablished && _establishedCount > 0) _establishedCount--;
                if (!_isHost)
                {
                    _clientHandshake = HandshakeState.Disconnected;
                    _clientDeadline = -1f;
                }

                PerPeerTeardown(slot);
                PlayerRegistry.HostRemove(peer);

                // HOST WITH PLAYERS LEFT: stop here.
                //
                // Everything below tears down session-wide state that the REMAINING
                // players are still using. Clearing CaptureState alone breaks their
                // census self-heal - the spawn ledger is what a diff request is answered
                // from - so one player rage-quitting used to quietly degrade everyone
                // else's session. A guest never takes this branch: losing the host is
                // losing everything.
                if (_isHost && _peers.Count > 0)
                {
                    PlayerRegistry.HostBroadcastRoster();
                    ReconnectManager.OnPeerLost(wasEstablished, peersRemain: true);
                    return;
                }

                if (_isHost) PlayerRegistry.HostBroadcastRoster();
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
                HostEntityStreamer.ClearAllViewportHints();
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
                FormationOwnership.Reset();
                ContactSyncManager.Reset();
                ContactRevealManager.Reset();
                DrawingSyncManager.Reset();
                SensorStateManager.Reset();
                JamStateManager.Reset();
                UnitStatusManager.Reset();
                OrderRefusalNotice.Reset();
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
                ReconnectManager.OnPeerLost(wasEstablished, peersRemain: false);
            });
        }

        /// <summary>
        /// Release state belonging to ONE departing player, leaving everyone else's
        /// session untouched.
        ///
        /// Small on purpose. Most of the big teardown below is the GUEST's - on the host
        /// the replica drivers, the spawn replicator and the id floor are already no-ops
        /// - so the per-peer set is just the handful of things genuinely keyed to a
        /// particular player.
        /// </summary>
        private void PerPeerTeardown(byte slot)
        {
            if (slot == PlayerRegistry.NoSender) return;

            SimSyncManager.OnPeerLeft(slot);
            HostEntityStreamer.ClearViewportHint(slot);

            // Their formations go to the longest-present remaining teammate, so nothing
            // on that side is left commandable by nobody. Before HostRemove, which is
            // what decides who the heir is.
            FormationOwnership.HostReleaseSlot(slot);

        }

        /// <summary>
        /// A message was abandoned part-way through reassembly. The sender saw a
        /// successful send and will not retry, so the only recovery is a fresh
        /// Send from the host — surface that instead of failing silently.
        /// </summary>
        private void OnReceiveFailed(PeerId peer, string reason)
        {
            Log.LogError($"[Net] Inbound message from {peer} lost: {reason}");
            _mainThreadQueue.Enqueue(() =>
            {
                SimSyncManager.ReportIssue(
                    "SYNC FAILED — the game data never finished arriving.",
                    $"{reason} Ask the host to press Send again.");
                SimSyncManager.Reset();
            });
        }

        private void OnDataReceived(PeerId from, byte[] data, int length)
        {
            var reader = new NetDataReader(data, 0, length);
            var type = (MessageType)reader.GetByte();
            Telemetry.OnReceive((byte)type, length);

            // Handshake gate, PER PEER: until this peer is Established, only its Hello
            // (host) / Welcome (client) is processed; everything else from it is dropped.
            // Peer-scoped so a second joiner mid-handshake cannot have its traffic
            // accepted on the strength of an established first peer, nor block it.
            if (!IsEstablishedFor(from))
            {
                HandlePreHandshake(from, type, reader);
                return;
            }

            if (type != MessageType.PlayerOrder && type != MessageType.DamageState)
                Log.LogDebug($"[Net] Received {type}");

            // One malformed message must not abort the transport poll loop (an
            // exception here propagates out of PollEvents and discards the rest of
            // the frame's event batch, reliable deliveries included). Log and move on.
            try
            {
                Dispatch(from, type, reader);
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[Net] Failed to handle {type} from {from} (len={length}): {ex}");
            }
        }

        /// <summary>
        /// Run a queued apply with the sender's slot ambient, so handlers can ask WHO
        /// sent this without every message growing a redundant (and forgeable) slot
        /// field. Cleared in a finally: a throwing handler must not leave the next
        /// LOCAL action running as if a remote player had made it.
        /// </summary>
        private static void ApplyAs(byte slot, Action apply)
        {
            PlayerRegistry.BeginApply(slot);
            try { apply(); }
            finally { PlayerRegistry.EndApply(); }
        }

        private void Dispatch(PeerId from, MessageType type, NetDataReader reader)
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
                    byte slot = SlotOf(from);
                    _mainThreadQueue.Enqueue(() => EntityCensusManager.HandleDiffRequest(slot, msg));
                    break;
                }

                case MessageType.PlayerOrder:
                {
                    var msg = PlayerOrderMessage.Deserialize(reader);
                    byte slot = SlotOf(from);
                    // NOT relayed here. OrderHandler.Apply relays from inside itself,
                    // after its guards - so "the teammates saw it" and "the host applied
                    // it" cannot come apart. Relaying at dispatch would forward orders
                    // the host then refuses, which is the worst outcome available: the
                    // two sims diverge and nothing says so.
                    _mainThreadQueue.Enqueue(() => ApplyAs(slot, () => OrderHandler.Apply(msg)));
                    break;
                }

                case MessageType.GameEvent:
                {
                    var msg = GameEventMessage.Deserialize(reader);
                    byte slot = SlotOf(from);

                    // The host decides who sent this, from the CONNECTION. Whatever the
                    // guest wrote in the field is overwritten before it is applied or
                    // relayed, so a guest cannot attribute its own actions to somebody
                    // else. Guests keep what they were given - it came from the host.
                    if (_isHost && slot != PlayerRegistry.NoSender) msg.Slot = slot;

                    // Relayed at receipt, not inside the handler: it must reach the
                    // sender's teammates even when the host itself ignores the event
                    // (an opponent's ally-lock claim is no business of the host's, but
                    // it is very much their teammate's).
                    switch (GameEventRelay.AudienceFor(msg.EventType))
                    {
                        case RelayAudience.Team: RelayToTeam(slot, msg); break;
                        case RelayAudience.All:  RelayToAll(slot, msg);  break;
                    }
                    _mainThreadQueue.Enqueue(() => ApplyAs(slot, () => GameEventHandler.Apply(msg)));
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
                    byte slot = SlotOf(from);
                    _mainThreadQueue.Enqueue(() => HostEntityStreamer.OnViewportHint(slot, msg));
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
                    byte slot = SlotOf(from);
                    // Relayed at receipt for the same reason as GameEvent: a Red guest's
                    // plot has to reach the other Red guest even though the Blue host
                    // ignores it entirely.
                    RelayToTeam(slot, msg);
                    _mainThreadQueue.Enqueue(() => ApplyAs(slot, () => DrawingSyncManager.ApplyReceived(msg)));
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

                case MessageType.PlayerRoster:
                {
                    var msg = PlayerRosterMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => PlayerRegistry.ApplyRoster(msg));
                    break;
                }

                case MessageType.UnitOwnership:
                {
                    var msg = UnitOwnershipMessage.Deserialize(reader);
                    _mainThreadQueue.Enqueue(() => FormationOwnership.ApplySnapshot(msg));
                    break;
                }

                case MessageType.SessionReady:
                {
                    var msg = SessionReadyMessage.Deserialize(reader);
                    byte slot = SlotOf(from);
                    _mainThreadQueue.Enqueue(() =>
                    {
                        SimSyncManager.OnClientReady(slot);
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

        private void HandlePreHandshake(PeerId from, MessageType type, NetDataReader reader)
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
                _mainThreadQueue.Enqueue(() => HandleHello(from, msg));
            }
            else if (type == MessageType.Welcome && !_isHost)
            {
                var msg = WelcomeMessage.Deserialize(reader);
                _mainThreadQueue.Enqueue(() => HandleWelcome(msg));
            }
            else
            {
                Telemetry.Count("net.droppedPreHandshake");
                Log.LogDebug($"[Handshake] Dropped {type} from {from} (state={Handshake})");
            }
        }

        private void HandleHello(PeerId from, HelloMessage msg)
        {
            if (!_peers.TryGetValue(from, out var session)) return;
            if (session.State != HandshakeState.AwaitingHello) return;

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
            // The old mode-mismatch refusal lived here. There is no session mode any more:
            // a joiner asks for a team and the host seats them, so there is nothing left
            // to disagree about.

            var requestedTeam = msg.RequestedTeam == (byte)Team.Red ? Team.Red : Team.Blue;
            PlayerInfo? seated = null;
            // Sanitized on RECEIPT, not just where it was typed: this arrived off the
            // wire, and the name goes straight into every other player's roster row and
            // context-menu labels. A peer sending a 4 KB name or an embedded newline is
            // everyone else's problem otherwise.
            if (refusal == null
                && !PlayerRegistry.HostTryAdd(from, _transport?.SteamIdOf(from) ?? 0UL,
                                              Sanitize(msg.DisplayName ?? ""), requestedTeam,
                                              out seated, out var full))
            {
                refusal = full;
            }

            if (refusal != null)
            {
                Log.LogError($"[Handshake] Refusing {from} (plugin {msg.PluginVersion}, game {msg.GameVersion}): {refusal}");
                Telemetry.Count("handshake.refused");
                // UNICAST. Broadcasting the refusal told every established player they
                // had been refused too.
                SendTo(from, new WelcomeMessage { Accepted = false, RefusalReason = refusal });
                session.State = HandshakeState.Refused;
                session.Deadline = -1f;
                session.RefuseDisconnectAt = Time.realtimeSinceStartup + 0.75f;
                return;
            }

            session.State = HandshakeState.Established;
            session.Deadline = -1f;
            session.Slot = seated!.Slot;
            VersionMismatchNotice = null;

            // Only after the refusal checks above: a client that is going to be turned
            // away has no options worth adopting, and its byte may not even mean what
            // this build thinks it does.
            //
            // FIRST established peer only. These are global gameplay settings, so with
            // several guests the last one to join would silently overwrite everyone
            // else's session.
            if (_establishedCount == 0)
                RemoteGameplayOptions.Apply(msg.GameplayOptions);
            else if (msg.GameplayOptions != RemoteGameplayOptions.PackLocal())
                Log.LogWarning($"[Handshake] {seated.DisplayName} has different gameplay options to the session — the session's own settings stand.");
            _establishedCount++;

            // UNICAST: slot, team and UID band are per-recipient and cannot ride a
            // broadcast. This is the only message that can tell a player who they are.
            SendTo(from, new WelcomeMessage
            {
                Accepted        = true,
                AssignedTeam    = (byte)seated.Team,
                AssignedSlot    = seated.Slot,
                ClientUidBase   = seated.UidBase,
                StateRateHz     = 10,
                GameplayOptions = RemoteGameplayOptions.PackLocal(),
                ModFingerprint  = ModSetCheck.LocalFingerprint(),
                ModCount        = (byte)Mathf.Min(ModSetCheck.LocalMods().Count, 255),
            });

            // Everyone learns who just joined - including the joiner, whose own identity
            // came from the Welcome above.
            PlayerRegistry.HostBroadcastRoster();

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
            Log.LogInfo($"[Handshake] {seated.DisplayName} accepted as slot {seated.Slot} on {seated.Team} " +
                        $"(plugin {msg.PluginVersion}, protocol {msg.ProtocolVersion}, game {ProtocolInfo.GameVersion}). Established.");
            ReconnectManager.OnPeerEstablished();

            // MID-MISSION JOIN. A battle is already running and other people are in it,
            // so this player gets the world unicast to them while everyone else keeps
            // their session - rather than the host's "Send State & Wait" button, which
            // starts a new battle for everybody.
            //
            // Not for the first joiner: with nobody else in yet there is no session to
            // preserve, and the host still drives that one from the button.
            if (SessionManager.MissionIsLive
                && SimSyncManager.CurrentState == SimState.Synchronized
                && _establishedCount > 1)
            {
                SessionManager.CaptureAndSendTo(seated.Slot);
            }
        }

        private void HandleWelcome(WelcomeMessage msg)
        {
            if (_clientHandshake != HandshakeState.AwaitingWelcome) return;
            _clientDeadline = -1f;

            if (!msg.Accepted)
            {
                Log.LogError($"[Handshake] Host refused connection: {msg.RefusalReason}");
                Telemetry.Count("handshake.refused");
                // The host's build generated this string, so the prefix check works
                // against both older and newer hosts.
                if (msg.RefusalReason.StartsWith("Protocol mismatch")
                 || msg.RefusalReason.StartsWith("Sea Power version mismatch"))
                    VersionMismatchNotice = msg.RefusalReason;
                _clientHandshake = HandshakeState.Refused;
                Stop();
                return;
            }

            SessionParams = msg;
            _clientHandshake = HandshakeState.Established;
            VersionMismatchNotice = null;

            // Before anything reads our team - in particular before any SessionSync can
            // arrive, which BlockedPreHandshake guarantees - because the save swap is
            // decided from it.
            PlayerRegistry.ApplyWelcome(msg);
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
            Log.LogInfo($"[Handshake] Established (slot={msg.AssignedSlot}, team={(Team)msg.AssignedTeam}, " +
                        $"uidBase={msg.ClientUidBase}, stateRate={msg.StateRateHz}Hz).");
            ReconnectManager.OnPeerEstablished();
        }
    }
}
