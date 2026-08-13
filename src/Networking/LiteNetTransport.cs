using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BepInEx.Logging;
using LiteNetLib;
using SeapowerMultiplayer.Net2;

namespace SeapowerMultiplayer.Transport
{
    public class LiteNetTransport : ITransport, INetEventListener
    {
        private NetManager? _net;
        private NetPeer? _serverPeer;
        private bool _isHost;

        // Scratch for the copy out of LiteNetLib's recycled reader.
        //
        // INVARIANT: safe to share across peers ONLY because OnDataReceived is drained
        // synchronously inside PollEvents() and NetworkManager.Dispatch deserializes
        // eagerly before enqueueing anything to the main thread. Nothing may hold this
        // array past the handler - in particular the host's order relay must
        // re-serialize the message, never forward this buffer.
        private readonly byte[] _receiveBuffer = new byte[512 * 1024]; // 512KB

        // Host-allocated peer identity. Monotonic, never reused - see PeerId.
        private readonly Dictionary<NetPeer, PeerId> _idOfPeer = new();
        private readonly Dictionary<PeerId, NetPeer> _peerOfId = new();
        private readonly List<PeerId> _connected = new();
        private int _nextPeerId;

        private readonly Dictionary<PeerId, int> _rttOf = new();

        private static ManualLogSource Log => Plugin.Log;

        // LiteNetLib 1.3.5 throws TooBigPacketException for Unreliable /
        // ReliableSequenced payloads above GetMaxSinglePacketSize(), which at the
        // initial MTU of 1024 is 1023 (Unreliable) / 1020 (ReliableSequenced) and
        // only grows if MTU discovery succeeds. Anything above this floor is
        // upgraded to ReliableUnordered below, which fragments instead of throwing.
        private const int MaxUnreliablePayload = 1000;

        // ── Network condition simulation (testing only) ──────────────────────
        // Applied on the RECEIVE side, above LiteNetLib's reliability layer:
        // loss is injected only for Unreliable payloads (dropping reliable data
        // here would violate the delivery contract - real loss is retransmitted
        // below this layer); latency delays everything uniformly (FIFO, so
        // ReliableOrdered streams keep their order).
        //
        // Jitter varies the delay per packet. A constant delay cannot reproduce
        // the class of bug that arrival-time variance causes, which is exactly
        // what the replica driver has to survive on a real high-ping link.
        private struct DelayedPacket
        {
            public long ReleaseAtMs;
            public byte[] Data;
            public int Length;
            public PeerId From;
        }

        private float _simLossPct;
        private int _simLatencyMs;
        private int _simJitterMs;
        private long _lastReleaseAtMs;
        private readonly Queue<DelayedPacket> _delayQueue = new();
        private readonly Random _simRng = new();
        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        public bool IsConnected => _isHost
            ? (_net?.ConnectedPeersCount ?? 0) > 0
            : _serverPeer?.ConnectionState == ConnectionState.Connected;

        /// <summary>Worst RTT across peers - the overlay shows one number, and the
        /// worst link is the one that explains a stutter.</summary>
        public int RttMs
        {
            get
            {
                int worst = 0;
                foreach (var kv in _rttOf) if (kv.Value > worst) worst = kv.Value;
                return worst;
            }
        }

        public int RttMsFor(PeerId peer) => _rttOf.TryGetValue(peer, out int ms) ? ms : 0;

        public bool LastSendFailed => false;
        public string? LastSendError => null;   // LiteNetLib fragments internally

        public IReadOnlyList<PeerId> ConnectedPeers => _connected;

        public ulong SteamIdOf(PeerId peer) => 0UL;   // no identity concept on direct IP

        public bool TryGetPacketStats(PeerId peer, out long packetsSent, out long packetsLost)
        {
            packetsSent = 0;
            packetsLost = 0;
            var p = Resolve(peer);
            if (p == null) return false;
            packetsSent = p.Statistics.PacketsSent;
            packetsLost = p.Statistics.PacketLoss;
            return true;
        }

        public event Action<PeerId, byte[], int>? OnDataReceived;
        public event Action<PeerId>? OnPeerConnected;
        public event Action<PeerId>? OnPeerDisconnected;

        // LiteNetLib reassembles fragments internally and holds them until the
        // whole message lands, so there is never a half-received message to report.
#pragma warning disable CS0067
        public event Action<PeerId, string>? OnReceiveFailed;
#pragma warning restore CS0067

        /// <summary>PeerId -> NetPeer. A guest's only peer is the server.</summary>
        private NetPeer? Resolve(PeerId peer)
        {
            if (!_isHost) return peer == PeerId.Server || !peer.IsValid ? _serverPeer : null;
            return _peerOfId.TryGetValue(peer, out var p) ? p : null;
        }

        /// <summary>NetPeer -> PeerId, allocating on first sight (host only).</summary>
        private PeerId IdOf(NetPeer peer)
        {
            if (!_isHost) return PeerId.Server;
            if (_idOfPeer.TryGetValue(peer, out var id)) return id;
            id = new PeerId(++_nextPeerId);
            _idOfPeer[peer] = id;
            _peerOfId[id] = peer;
            return id;
        }

        /// <summary>Drop every trace of a peer. Takes the id explicitly because a guest
        /// never registers its server in <see cref="_idOfPeer"/> (IdOf short-circuits to
        /// PeerId.Server), so looking it up by NetPeer would silently no-op and leave
        /// the connected list holding a dead entry.</summary>
        private void Forget(NetPeer? peer, PeerId id)
        {
            if (peer != null) _idOfPeer.Remove(peer);
            _peerOfId.Remove(id);
            _connected.Remove(id);
            _rttOf.Remove(id);
        }

        public void Start(bool asHost)
        {
            _isHost = asHost;
            // DisconnectTimeout defaults to 5s, which a 300ms-RTT link loses to
            // routinely: one stalled frame plus a burst of reliable traffic is
            // enough to starve the keepalive. PingInterval is tightened at the same
            // time so the timeout is measured from frequent, cheap probes rather
            // than from whatever gameplay traffic happens to be flowing.
            _net = new NetManager(this)
            {
                AutoRecycle = true,
                ReuseAddress = true,
                DisconnectTimeout = Plugin.Instance.CfgDisconnectTimeoutSec.Value * 1000,
                PingInterval = 1000,
                EnableStatistics = true, // feeds the overlay's packet-loss indicator
            };

            _simLossPct   = Plugin.Instance.CfgNetSimLossPct.Value;
            _simLatencyMs = Plugin.Instance.CfgNetSimLatencyMs.Value;
            _simJitterMs  = Plugin.Instance.CfgNetSimJitterMs.Value;
            if (_simLossPct > 0f || _simLatencyMs > 0)
                Log.LogWarning($"[LiteNet] NETWORK SIMULATION ACTIVE: loss={_simLossPct}% " +
                               $"latency={_simLatencyMs}ms jitter=±{_simJitterMs}ms (testing only)");

            if (asHost)
            {
                _net.Start(Plugin.Instance.CfgPort.Value);
                Log.LogInfo($"[LiteNet] Hosting on port {Plugin.Instance.CfgPort.Value} (key {ProtocolInfo.ConnectKey})");
            }
            else
            {
                _net.Start();
                _serverPeer = _net.Connect(
                    Plugin.Instance.CfgHostIP.Value,
                    Plugin.Instance.CfgPort.Value,
                    ProtocolInfo.ConnectKey);
                Log.LogInfo($"[LiteNet] Connecting to {Plugin.Instance.CfgHostIP.Value}:{Plugin.Instance.CfgPort.Value} (key {ProtocolInfo.ConnectKey})");
            }
        }

        public void Stop()
        {
            _net?.Stop();
            _serverPeer = null;
            _delayQueue.Clear();
            _lastReleaseAtMs = 0;
            _idOfPeer.Clear();
            _peerOfId.Clear();
            _connected.Clear();
            _rttOf.Clear();
            // _nextPeerId is deliberately NOT reset: ids stay unique for the process,
            // so a stale reference from a previous session can never resolve.
            Log.LogInfo("[LiteNet] Stopped.");
        }

        /// <summary>Drop one peer and leave every other connection alive - refusing a
        /// surplus or incompatible joiner must not kick the players already in.</summary>
        public void Disconnect(PeerId peer, string reason)
        {
            var p = Resolve(peer);
            if (p == null) return;
            Log.LogInfo($"[LiteNet] Disconnecting {peer}: {reason}");
            _net?.DisconnectPeer(p);
            if (!_isHost) _serverPeer = null;
            Forget(p, peer);
        }

        public void DisconnectPeers()
        {
            _net?.DisconnectAll();
            if (!_isHost) _serverPeer = null;
            _idOfPeer.Clear();
            _peerOfId.Clear();
            _connected.Clear();
            _rttOf.Clear();
            Log.LogInfo("[LiteNet] Disconnected all peers (transport stays up).");
        }

        public void Poll()
        {
            _net?.PollEvents();

            // Release sim-delayed packets whose time has come (FIFO keeps order)
            while (_delayQueue.Count > 0 && _delayQueue.Peek().ReleaseAtMs <= _clock.ElapsedMilliseconds)
            {
                var pkt = _delayQueue.Dequeue();
                OnDataReceived?.Invoke(pkt.From, pkt.Data, pkt.Length);
            }
        }

        public void SendToServer(byte[] data, int length, TransportDelivery delivery)
        {
            _serverPeer?.Send(data, 0, length, Resolve(delivery, length));
        }

        public void SendTo(PeerId peer, byte[] data, int length, TransportDelivery delivery)
        {
            Resolve(peer)?.Send(data, 0, length, Resolve(delivery, length));
        }

        public void BroadcastToClients(byte[] data, int length, TransportDelivery delivery)
        {
            _net?.SendToAll(data, 0, length, Resolve(delivery, length));
        }

        /// <summary>The single place the payload-size upgrade lives. It used to be
        /// copy-pasted into each send path, which is exactly the kind of rule that
        /// drifts once a third path is added.</summary>
        private static DeliveryMethod Resolve(TransportDelivery delivery, int length)
        {
            var dm = MapDelivery(delivery);
            if ((dm == DeliveryMethod.Unreliable || dm == DeliveryMethod.ReliableSequenced)
                && length > MaxUnreliablePayload)
                dm = DeliveryMethod.ReliableUnordered;
            return dm;
        }

        private static DeliveryMethod MapDelivery(TransportDelivery delivery) => delivery switch
        {
            TransportDelivery.Unreliable => DeliveryMethod.Unreliable,
            TransportDelivery.Reliable => DeliveryMethod.ReliableSequenced,
            TransportDelivery.ReliableOrdered => DeliveryMethod.ReliableOrdered,
            _ => DeliveryMethod.ReliableOrdered,
        };

        // ── INetEventListener ───────────────────────────────────────────────

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            if (!_isHost) _serverPeer = peer;
            var id = IdOf(peer);
            if (!_connected.Contains(id)) _connected.Add(id);
            Log.LogInfo($"[LiteNet] Peer connected: {peer} as {id}");
            OnPeerConnected?.Invoke(id);
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            var id = _isHost ? (_idOfPeer.TryGetValue(peer, out var k) ? k : PeerId.None)
                             : PeerId.Server;
            Log.LogInfo($"[LiteNet] Peer disconnected: {peer} ({id})  reason={disconnectInfo.Reason}");
            if (!_isHost) _serverPeer = null;
            Forget(peer, id);
            OnPeerDisconnected?.Invoke(id);
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            Log.LogError($"[LiteNet] Network error from {endPoint}: {socketError}");
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            int length = reader.AvailableBytes;
            PeerId from = IdOf(peer);

            // Network condition simulation (testing only)
            if (_simLossPct > 0f && deliveryMethod == DeliveryMethod.Unreliable
                && _simRng.NextDouble() * 100.0 < _simLossPct)
            {
                Telemetry.Count("netsim.droppedIn");
                return;
            }
            if (_simLatencyMs > 0)
            {
                var copy = new byte[length];
                Buffer.BlockCopy(reader.RawData, reader.Position, copy, 0, length);

                long delay = _simLatencyMs;
                if (_simJitterMs > 0)
                    delay += _simRng.Next(-_simJitterMs, _simJitterMs + 1);
                if (delay < 0) delay = 0;

                // The queue drains FIFO, so a packet must never be scheduled ahead
                // of one already queued - that would reorder the stream instead of
                // jittering it, which is a different (and unrealistic) fault.
                long releaseAt = _clock.ElapsedMilliseconds + delay;
                if (releaseAt < _lastReleaseAtMs) releaseAt = _lastReleaseAtMs;
                _lastReleaseAtMs = releaseAt;

                _delayQueue.Enqueue(new DelayedPacket
                {
                    ReleaseAtMs = releaseAt,
                    Data = copy,
                    Length = length,
                    From = from,
                });
                return;
            }

            // Copy the data out before the reader is recycled (AutoRecycle = true)
            byte[] data;
            if (length <= _receiveBuffer.Length)
            {
                Buffer.BlockCopy(reader.RawData, reader.Position, _receiveBuffer, 0, length);
                data = _receiveBuffer;
            }
            else
            {
                data = new byte[length];
                Buffer.BlockCopy(reader.RawData, reader.Position, data, 0, length);
            }
            OnDataReceived?.Invoke(from, data, length);
        }

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            _rttOf[IdOf(peer)] = latency;
        }

        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            // Versioned key: peers built against a different protocol version are
            // refused before any message flows (they see a failed connection).
            if (request.Data.TryGetString(out string key) && key == ProtocolInfo.ConnectKey)
            {
                request.Accept();
            }
            else
            {
                Log.LogWarning($"[LiteNet] Rejected connection with key '{key}' (expected '{ProtocolInfo.ConnectKey}') — mismatched plugin/protocol version.");
                request.Reject();
            }
        }
    }
}
