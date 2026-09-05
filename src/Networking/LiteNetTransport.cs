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
        private readonly byte[] _receiveBuffer = new byte[512 * 1024]; // 512KB

        /// <summary>Host: connected clients, keyed by the id the rest of the mod
        /// addresses them by. Unused on a client, which has exactly one peer
        /// (<see cref="PeerId.Host"/>, held in <see cref="_serverPeer"/>).</summary>
        private readonly PeerTable<NetPeer> _peers = new();

        /// <summary>Latest reported latency per peer id, fed by
        /// OnNetworkLatencyUpdate. Entries are dropped on disconnect so a stale
        /// number can't outlive the link it described.</summary>
        private readonly Dictionary<int, int> _rttOf = new();

        private static readonly int[] HostOnly = { PeerId.Host };
        private static readonly int[] NoPeers = new int[0];

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
            public int PeerId;
            public byte[] Data;
            public int Length;
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

        public IReadOnlyList<int> ConnectedPeers => _isHost
            ? _peers.Ids
            : (IsConnected ? HostOnly : NoPeers);

        /// <summary>Worst RTT across connected peers on a host, the host link on a
        /// client. See ITransport.RttMs for why worst rather than first.</summary>
        public int RttMs
        {
            get
            {
                if (!_isHost) return RttMsFor(PeerId.Host);

                int worst = 0;
                var ids = _peers.Ids;
                for (int i = 0; i < ids.Count; i++)
                {
                    int rtt = RttMsFor(ids[i]);
                    if (rtt > worst) worst = rtt;
                }
                return worst;
            }
        }

        public int RttMsFor(int peerId) => _rttOf.TryGetValue(peerId, out int rtt) ? rtt : 0;

        public bool LastSendFailed => false;
        public string? LastSendError => null;   // LiteNetLib fragments internally

        public bool TryGetPacketStats(int peerId, out long packetsSent, out long packetsLost)
        {
            packetsSent = 0;
            packetsLost = 0;
            var peer = PeerFor(peerId);
            if (peer == null) return false;
            packetsSent = peer.Statistics.PacketsSent;
            packetsLost = peer.Statistics.PacketLoss;
            return true;
        }

        public event Action<int, byte[], int>? OnDataReceived;
        public event Action<int>? OnPeerConnected;
        public event Action<int>? OnPeerDisconnected;

        // LiteNetLib reassembles fragments internally and holds them until the
        // whole message lands, so there is never a half-received message to report.
#pragma warning disable CS0067
        public event Action<int, string>? OnReceiveFailed;
#pragma warning restore CS0067

        /// <summary>Resolve a peer id to its connection, or null if it is not one
        /// of ours. On a client only PeerId.Host resolves.</summary>
        private NetPeer? PeerFor(int peerId)
        {
            if (!_isHost)
                return peerId == PeerId.Host ? _serverPeer : null;
            return _peers.TryGetConn(peerId, out var peer) ? peer : null;
        }

        /// <summary>Resolve a connection to its peer id, or PeerId.None if it is
        /// not tracked. Untracked on the host means a packet arrived between the
        /// socket accepting the peer and OnPeerConnected registering it.</summary>
        private int IdFor(NetPeer peer)
        {
            if (!_isHost) return PeerId.Host;
            return _peers.TryGetId(peer, out int id) ? id : PeerId.None;
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
            _peers.Clear();
            _rttOf.Clear();
            _delayQueue.Clear();
            _lastReleaseAtMs = 0;
            Log.LogInfo("[LiteNet] Stopped.");
        }

        public void DisconnectPeers()
        {
            _net?.DisconnectAll();
            if (!_isHost) _serverPeer = null;
            Log.LogInfo("[LiteNet] Disconnected all peers (transport stays up).");
        }

        public void DisconnectPeer(int peerId, string reason)
        {
            var peer = PeerFor(peerId);
            if (peer == null) return;
            _net?.DisconnectPeer(peer);
            if (!_isHost) _serverPeer = null;
            Log.LogInfo($"[LiteNet] Disconnected peer {peerId}: {reason}");
        }

        public void Poll()
        {
            _net?.PollEvents();

            // Release sim-delayed packets whose time has come (FIFO keeps order)
            while (_delayQueue.Count > 0 && _delayQueue.Peek().ReleaseAtMs <= _clock.ElapsedMilliseconds)
            {
                var pkt = _delayQueue.Dequeue();
                OnDataReceived?.Invoke(pkt.PeerId, pkt.Data, pkt.Length);
            }
        }

        public void SendToServer(byte[] data, int length, TransportDelivery delivery)
        {
            if (_serverPeer == null) return;
            _serverPeer.Send(data, 0, length, MapDelivery(delivery, length));
        }

        public void SendToPeer(int peerId, byte[] data, int length, TransportDelivery delivery)
        {
            var peer = PeerFor(peerId);
            if (peer == null) return;
            peer.Send(data, 0, length, MapDelivery(delivery, length));
        }

        public void BroadcastToClients(byte[] data, int length, TransportDelivery delivery)
        {
            if (_net == null) return;
            _net.SendToAll(data, 0, length, MapDelivery(delivery, length));
        }

        /// <summary>Map to a LiteNetLib delivery method, upgrading a payload that
        /// would exceed the single-packet floor to one that fragments instead of
        /// throwing. See MaxUnreliablePayload.</summary>
        private static DeliveryMethod MapDelivery(TransportDelivery delivery, int length)
        {
            var dm = delivery switch
            {
                TransportDelivery.Unreliable => DeliveryMethod.Unreliable,
                TransportDelivery.Reliable => DeliveryMethod.ReliableSequenced,
                TransportDelivery.ReliableOrdered => DeliveryMethod.ReliableOrdered,
                _ => DeliveryMethod.ReliableOrdered,
            };

            if ((dm == DeliveryMethod.Unreliable || dm == DeliveryMethod.ReliableSequenced)
                && length > MaxUnreliablePayload)
                dm = DeliveryMethod.ReliableUnordered;

            return dm;
        }

        // ── INetEventListener ───────────────────────────────────────────────

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            int id;
            if (_isHost)
            {
                id = _peers.Add(peer);
            }
            else
            {
                _serverPeer = peer;
                id = PeerId.Host;
            }

            Log.LogInfo($"[LiteNet] Peer connected: {peer} (peer {id})");
            OnPeerConnected?.Invoke(id);
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            int id;
            if (_isHost)
            {
                id = _peers.Remove(peer);
            }
            else
            {
                _serverPeer = null;
                id = PeerId.Host;
            }
            _rttOf.Remove(id);

            Log.LogInfo($"[LiteNet] Peer disconnected: {peer} (peer {id})  reason={disconnectInfo.Reason}");

            // A peer that was never registered was never announced as connected
            // either, so announcing its departure would leave listeners unbalanced -
            // and on a host it would tear down a session belonging to a DIFFERENT
            // client. LiteNetLib does reach here with no matching OnPeerConnected
            // when a connection attempt fails outright, which on a client is the
            // host refusing us; that case still reports, because a client's only
            // peer is PeerId.Host whether or not it ever completed.
            if (id != PeerId.None)
                OnPeerDisconnected?.Invoke(id);
            else
                Log.LogWarning($"[LiteNet] Disconnect from an unregistered peer {peer} — ignored.");
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            Log.LogError($"[LiteNet] Network error from {endPoint}: {socketError}");
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            int length = reader.AvailableBytes;
            int peerId = IdFor(peer);
            if (peerId == PeerId.None) return;   // arrived before OnPeerConnected registered it

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
                    PeerId = peerId,
                    Data = copy,
                    Length = length,
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
            OnDataReceived?.Invoke(peerId, data, length);
        }

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            int id = IdFor(peer);
            if (id == PeerId.None) return;
            _rttOf[id] = latency;
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
