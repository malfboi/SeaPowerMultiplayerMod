using System;
using System.Net;
using System.Net.Sockets;
using BepInEx.Logging;
using LiteNetLib;

namespace SeapowerMultiplayer.Transport
{
    public class LiteNetTransport : ITransport, INetEventListener
    {
        private NetManager? _net;
        private NetPeer? _serverPeer;
        private bool _isHost;

        private static ManualLogSource Log => Plugin.Log;

        private const int MaxUnreliablePayload = 1400;

        public bool IsConnected => _isHost
            ? (_net?.ConnectedPeersCount ?? 0) > 0
            : _serverPeer?.ConnectionState == ConnectionState.Connected;

        public int RttMs { get; private set; }

        public event Action<byte[], int>? OnDataReceived;
        public event Action? OnPeerConnected;
        public event Action? OnPeerDisconnected;

        public void Start(bool asHost)
        {
            _isHost = asHost;
            _net = new NetManager(this) { AutoRecycle = true, ReuseAddress = true };

            if (asHost)
            {
                _net.Start(Plugin.Instance.CfgPort.Value);
                Log.LogInfo($"[LiteNet] Hosting on port {Plugin.Instance.CfgPort.Value}");
            }
            else
            {
                _net.Start();
                _serverPeer = _net.Connect(
                    Plugin.Instance.CfgHostIP.Value,
                    Plugin.Instance.CfgPort.Value,
                    PluginInfo.PLUGIN_GUID);
                Log.LogInfo($"[LiteNet] Connecting to {Plugin.Instance.CfgHostIP.Value}:{Plugin.Instance.CfgPort.Value}");
            }
        }

        public void Stop()
        {
            _net?.Stop();
            _serverPeer = null;
            Log.LogInfo("[LiteNet] Stopped.");
        }

        public void Poll()
        {
            _net?.PollEvents();
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
            Log.LogInfo($"[LiteNet] Peer connected: {peer.EndPoint}");
            if (!_isHost)
                _serverPeer = peer;
            OnPeerConnected?.Invoke();
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            Log.LogInfo($"[LiteNet] Peer disconnected: {peer.EndPoint}  reason={disconnectInfo.Reason}");
            if (!_isHost) _serverPeer = null;
            OnPeerDisconnected?.Invoke();
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            Log.LogError($"[LiteNet] Network error from {endPoint}: {socketError}");
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            // Copy the data out before the reader is recycled (AutoRecycle = true)
            int length = reader.AvailableBytes;
            byte[] data = new byte[length];
            Buffer.BlockCopy(reader.RawData, reader.Position, data, 0, length);
            OnDataReceived?.Invoke(data, length);
        }

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            RttMs = latency;
        }

        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            if (request.Data.TryGetString(out string key) && key == PluginInfo.PLUGIN_GUID)
                request.Accept();
            else
                request.Reject();
        }
    }
}
