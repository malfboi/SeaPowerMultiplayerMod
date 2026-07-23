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

        public int RttMs { get; private set; }
        public bool LastSendFailed => false;
        public string? LastSendError => null;   // LiteNetLib fragments internally

        public event Action<byte[], int>? OnDataReceived;
        public event Action? OnPeerConnected;
        public event Action? OnPeerDisconnected;

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
            Log.LogInfo("[LiteNet] Stopped.");
        }

        public void DisconnectPeers()
        {
            _net?.DisconnectAll();
            if (!_isHost) _serverPeer = null;
            Log.LogInfo("[LiteNet] Disconnected all peers (transport stays up).");
        }

        public void Poll()
        {
            _net?.PollEvents();

            // Release sim-delayed packets whose time has come (FIFO keeps order)
            while (_delayQueue.Count > 0 && _delayQueue.Peek().ReleaseAtMs <= _clock.ElapsedMilliseconds)
            {
                var pkt = _delayQueue.Dequeue();
                OnDataReceived?.Invoke(pkt.Data, pkt.Length);
            }
        }

        public void SendToServer(byte[] data, int length, TransportDelivery delivery)
        {
            if (_serverPeer == null) return;
            var dm = MapDelivery(delivery);
            if ((dm == DeliveryMethod.Unreliable || dm == DeliveryMethod.ReliableSequenced)
                && length > MaxUnreliablePayload)
                dm = DeliveryMethod.ReliableUnordered;
            _serverPeer.Send(data, 0, length, dm);
        }

        public void BroadcastToClients(byte[] data, int length, TransportDelivery delivery)
        {
            if (_net == null) return;
            var dm = MapDelivery(delivery);
            if ((dm == DeliveryMethod.Unreliable || dm == DeliveryMethod.ReliableSequenced)
                && length > MaxUnreliablePayload)
                dm = DeliveryMethod.ReliableUnordered;
            _net.SendToAll(data, 0, length, dm);
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
            Log.LogInfo($"[LiteNet] Peer connected: {peer}");
            if (!_isHost)
                _serverPeer = peer;
            OnPeerConnected?.Invoke();
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            Log.LogInfo($"[LiteNet] Peer disconnected: {peer}  reason={disconnectInfo.Reason}");
            if (!_isHost) _serverPeer = null;
            OnPeerDisconnected?.Invoke();
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            Log.LogError($"[LiteNet] Network error from {endPoint}: {socketError}");
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            int length = reader.AvailableBytes;

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
            OnDataReceived?.Invoke(data, length);
        }

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            RttMs = latency;
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
