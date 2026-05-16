using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using BepInEx.Logging;
using LiteNetLib;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer.Transport
{
    public class LiteNetTransport : ITransport, INetEventListener
    {
        private NetManager? _net;
        private NetPeer? _serverPeer;
        private bool _isHost;
        private readonly byte[] _receiveBuffer = new byte[512 * 1024]; // 512KB
        private uint _nextFragmentId;
        private readonly Dictionary<uint, FragmentBuffer> _pendingFragments = new();
        private readonly List<uint> _fragmentDropScratch = new();
        private long _lastFragmentCleanupTicks;
        private uint _latestCompletedStateSequence;
        private bool _hasCompletedStateSequence;
        private long _messagesSent;
        private long _payloadBytesSent;
        private long _wireBytesSent;
        private long _messagesReceived;
        private long _payloadBytesReceived;
        private long _wireBytesReceived;
        private long _fragmentedMessagesSent;
        private long _fragmentsSent;
        private long _fragmentsReceived;
        private long _reassembledMessagesReceived;
        private long _fragmentDrops;
        private long _oversizeDrops;
        private int _lastSentBytes;
        private int _lastReceivedBytes;
        private int _lastFragmentedBytes;
        private int _lastFragmentChunkCount;
        private int _lastReassembledBytes;
        private int _lastReassembledChunkCount;

        private static ManualLogSource Log => Plugin.Log;

        private const int MaxPendingFragments = 32;
        private const long FragmentCleanupIntervalTicks = TimeSpan.TicksPerMillisecond * 250;
        private const long FragmentStaleTicks = TimeSpan.TicksPerMillisecond * 500;

        private sealed class FragmentBuffer
        {
            public readonly byte OriginalMessageType;
            public readonly byte DeliveryClass;
            public readonly int TotalLength;
            public readonly byte[][] Chunks;
            public readonly int[] ChunkLengths;
            public readonly long CreatedTicks;
            public int ReceivedCount;
            public int ReceivedLength;

            public FragmentBuffer(byte originalMessageType, byte deliveryClass, int totalChunks, int totalLength)
            {
                OriginalMessageType = originalMessageType;
                DeliveryClass = deliveryClass;
                TotalLength = totalLength;
                Chunks = new byte[totalChunks][];
                ChunkLengths = new int[totalChunks];
                CreatedTicks = DateTime.UtcNow.Ticks;
            }
        }

        public bool IsConnected => _isHost
            ? (_net?.ConnectedPeersCount ?? 0) > 0
            : _serverPeer?.ConnectionState == ConnectionState.Connected;

        public int RttMs { get; private set; }
        public bool LastSendFailed { get; private set; }
        public NetworkDiagnosticsSnapshot Diagnostics => new(
            _messagesSent,
            _payloadBytesSent,
            _wireBytesSent,
            _messagesReceived,
            _payloadBytesReceived,
            _wireBytesReceived,
            _fragmentedMessagesSent,
            _fragmentsSent,
            _fragmentsReceived,
            _reassembledMessagesReceived,
            _fragmentDrops,
            _oversizeDrops,
            _pendingFragments.Count,
            _lastSentBytes,
            _lastReceivedBytes,
            _lastFragmentedBytes,
            _lastFragmentChunkCount,
            _lastReassembledBytes,
            _lastReassembledChunkCount);

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
            ClearFragments();
            Log.LogInfo("[LiteNet] Stopped.");
        }

        public void Poll()
        {
            _net?.PollEvents();
            CleanupStaleFragments();
        }

        public void SendToServer(byte[] data, int length, TransportDelivery delivery)
        {
            if (_serverPeer == null) return;
            LastSendFailed = false;
            var dm = MapDelivery(delivery);
            if (!SendPacket(_serverPeer, data, length, dm))
                LastSendFailed = true;
        }

        public void BroadcastToClients(byte[] data, int length, TransportDelivery delivery)
        {
            if (_net == null) return;
            LastSendFailed = false;
            var dm = MapDelivery(delivery);
            for (int i = 0; i < _net.ConnectedPeerList.Count; i++)
            {
                var peer = _net.ConnectedPeerList[i];
                if (!SendPacket(peer, data, length, dm))
                    LastSendFailed = true;
            }
        }

        private static DeliveryMethod MapDelivery(TransportDelivery delivery) => delivery switch
        {
            TransportDelivery.Unreliable => DeliveryMethod.Unreliable,
            TransportDelivery.Reliable => DeliveryMethod.ReliableSequenced,
            TransportDelivery.ReliableOrdered => DeliveryMethod.ReliableOrdered,
            _ => DeliveryMethod.ReliableOrdered,
        };

        private bool SendPacket(NetPeer peer, byte[] data, int length, DeliveryMethod deliveryMethod)
        {
            int maxSize = peer.GetMaxSinglePacketSize(deliveryMethod);
            if (length <= maxSize)
            {
                peer.Send(data, 0, length, deliveryMethod);
                RecordPayloadSent(length, length);
                return true;
            }

            if (!CanFragment(deliveryMethod))
                return DropOversize(deliveryMethod, length, maxSize, "delivery does not support transport fragmentation");

            int chunkPayloadMax = LiteNetFragmentCodec.GetChunkPayloadMax(maxSize);
            if (chunkPayloadMax <= 0)
                return DropOversize(deliveryMethod, length, maxSize, "fragment header exceeds peer payload limit");

            int totalChunks = LiteNetFragmentCodec.GetChunkCount(length, chunkPayloadMax);
            if (!LiteNetFragmentCodec.IsValidChunkCount(totalChunks))
                return DropOversize(deliveryMethod, length, maxSize, $"chunk count {totalChunks} is invalid");

            uint sequenceId = _nextFragmentId++;
            byte originalType = length > 0 ? data[0] : (byte)0;
            _messagesSent++;
            _payloadBytesSent += length;
            _fragmentedMessagesSent++;
            _lastSentBytes = length;
            _lastFragmentedBytes = length;
            _lastFragmentChunkCount = totalChunks;

            MpLog.InfoThrottle(
                $"LiteNetFragmentSend:{originalType}:{deliveryMethod}",
                "LiteNet",
                $"Fragmenting message type={originalType} bytes={length} chunks={totalChunks} delivery={deliveryMethod} max={maxSize}",
                2f);

            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * chunkPayloadMax;
                int payloadLen = Math.Min(chunkPayloadMax, length - offset);
                int chunkLen = LiteNetFragmentCodec.HeaderSize + payloadLen;
                if (chunkLen > maxSize)
                    return DropOversize(deliveryMethod, chunkLen, maxSize, "fragment chunk still exceeds peer payload limit");

                var chunk = new byte[chunkLen];
                LiteNetFragmentCodec.WriteHeader(chunk,
                    new LiteNetFragmentHeader(sequenceId, originalType, (byte)deliveryMethod, i, totalChunks, length, payloadLen));
                Buffer.BlockCopy(data, offset, chunk, LiteNetFragmentCodec.HeaderSize, payloadLen);
                peer.Send(chunk, 0, chunkLen, deliveryMethod);
                _wireBytesSent += chunkLen;
                _fragmentsSent++;
            }

            return true;
        }

        private static bool CanFragment(DeliveryMethod deliveryMethod) =>
            deliveryMethod == DeliveryMethod.Unreliable ||
            deliveryMethod == DeliveryMethod.ReliableSequenced ||
            deliveryMethod == DeliveryMethod.ReliableOrdered ||
            deliveryMethod == DeliveryMethod.ReliableUnordered;

        private bool DropOversize(DeliveryMethod deliveryMethod, int length, int maxSize, string reason)
        {
            MpLog.WarnThrottle(
                $"LiteNetOversize:{deliveryMethod}",
                "LiteNet",
                $"Dropped {deliveryMethod} packet: {length} bytes exceeds single-packet max {maxSize} bytes ({reason})",
                2f);
            _oversizeDrops++;
            return false;
        }

        private void RecordPayloadSent(int payloadBytes, int wireBytes)
        {
            _messagesSent++;
            _payloadBytesSent += payloadBytes;
            _wireBytesSent += wireBytes;
            _lastSentBytes = payloadBytes;
        }

        private void RecordPayloadReceived(int payloadBytes, int wireBytes)
        {
            _messagesReceived++;
            _payloadBytesReceived += payloadBytes;
            _wireBytesReceived += wireBytes;
            _lastReceivedBytes = payloadBytes;
        }

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
            ClearFragments();
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

            if (LiteNetFragmentCodec.IsFragmentPacket(data, length))
                HandleFragment(data, length);
            else
            {
                RecordPayloadReceived(length, length);
                OnDataReceived?.Invoke(data, length);
            }
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

        private void HandleFragment(byte[] data, int length)
        {
            var header = LiteNetFragmentCodec.ReadHeader(data);
            uint sequenceId = header.SequenceId;
            byte originalType = header.OriginalMessageType;
            int chunkIndex = header.ChunkIndex;
            int totalChunks = header.TotalChunks;
            int totalLength = header.TotalLength;
            int payloadLength = header.PayloadLength;
            _fragmentsReceived++;
            _wireBytesReceived += length;

            if (!ValidateFragmentHeader(header, length))
            {
                _fragmentDrops++;
                return;
            }

            if (IsStateUpdate(originalType) && IsOlderCompletedState(sequenceId))
            {
                MpLog.Trace("LiteNet", $"Discarding old StateUpdate fragment seq={sequenceId}, latest={_latestCompletedStateSequence}");
                return;
            }

            if (!_pendingFragments.TryGetValue(sequenceId, out var buffer))
            {
                buffer = new FragmentBuffer(originalType, header.DeliveryClass, totalChunks, totalLength);
                _pendingFragments[sequenceId] = buffer;
                EnforcePendingFragmentLimit();
            }
            else if (buffer.Chunks.Length != totalChunks ||
                     buffer.TotalLength != totalLength ||
                     buffer.OriginalMessageType != originalType)
            {
                DropFragment(sequenceId, "conflicting fragment header");
                return;
            }

            if (buffer.Chunks[chunkIndex] != null)
            {
                MpLog.Trace("LiteNet", $"Ignoring duplicate fragment seq={sequenceId} chunk={chunkIndex}/{totalChunks}");
                return;
            }

            var payload = new byte[payloadLength];
            Buffer.BlockCopy(data, LiteNetFragmentCodec.HeaderSize, payload, 0, payloadLength);
            buffer.Chunks[chunkIndex] = payload;
            buffer.ChunkLengths[chunkIndex] = payloadLength;
            buffer.ReceivedCount++;
            buffer.ReceivedLength += payloadLength;

            if (buffer.ReceivedCount != totalChunks) return;

            if (buffer.ReceivedLength != buffer.TotalLength)
            {
                DropFragment(sequenceId, $"received length {buffer.ReceivedLength} != expected {buffer.TotalLength}");
                return;
            }

            var reassembled = new byte[buffer.TotalLength];
            int offset = 0;
            for (int i = 0; i < totalChunks; i++)
            {
                Buffer.BlockCopy(buffer.Chunks[i], 0, reassembled, offset, buffer.ChunkLengths[i]);
                offset += buffer.ChunkLengths[i];
            }

            _pendingFragments.Remove(sequenceId);
            _reassembledMessagesReceived++;
            _messagesReceived++;
            _payloadBytesReceived += buffer.TotalLength;
            _lastReceivedBytes = buffer.TotalLength;
            _lastReassembledBytes = buffer.TotalLength;
            _lastReassembledChunkCount = totalChunks;

            MpLog.InfoThrottle(
                $"LiteNetFragmentReassembled:{originalType}",
                "LiteNet",
                $"Reassembled message type={originalType} bytes={buffer.TotalLength} chunks={totalChunks}",
                2f);

            if (IsStateUpdate(originalType))
            {
                _latestCompletedStateSequence = sequenceId;
                _hasCompletedStateSequence = true;
                DropOlderStateFragments(sequenceId);
            }

            OnDataReceived?.Invoke(reassembled, buffer.TotalLength);
        }

        private static bool ValidateFragmentHeader(LiteNetFragmentHeader header, int packetLength)
        {
            if (LiteNetFragmentCodec.ValidateHeader(header, packetLength, out _)) return true;

            MpLog.WarnThrottle("LiteNetFragmentInvalid", "LiteNet",
                $"Invalid fragment seq={header.SequenceId} type={header.OriginalMessageType}: " +
                $"chunk={header.ChunkIndex}/{header.TotalChunks} total={header.TotalLength} payload={header.PayloadLength}",
                2f);
            return false;
        }

        private void CleanupStaleFragments()
        {
            long now = DateTime.UtcNow.Ticks;
            if (now - _lastFragmentCleanupTicks < FragmentCleanupIntervalTicks) return;
            _lastFragmentCleanupTicks = now;

            _fragmentDropScratch.Clear();
            foreach (var kvp in _pendingFragments)
            {
                if (now - kvp.Value.CreatedTicks > FragmentStaleTicks)
                    _fragmentDropScratch.Add(kvp.Key);
            }

            for (int i = 0; i < _fragmentDropScratch.Count; i++)
            {
                uint id = _fragmentDropScratch[i];
                if (!_pendingFragments.TryGetValue(id, out var buffer)) continue;
                MpLog.WarnThrottle("LiteNetFragmentStale", "LiteNet",
                    $"Discarding stale fragment seq={id} type={buffer.OriginalMessageType} received={buffer.ReceivedCount}/{buffer.Chunks.Length} bytes={buffer.ReceivedLength}/{buffer.TotalLength}",
                    2f);
                _pendingFragments.Remove(id);
            }
        }

        private void EnforcePendingFragmentLimit()
        {
            while (_pendingFragments.Count > MaxPendingFragments)
            {
                uint oldestId = 0;
                long oldestTicks = long.MaxValue;
                foreach (var kvp in _pendingFragments)
                {
                    if (kvp.Value.CreatedTicks >= oldestTicks) continue;
                    oldestId = kvp.Key;
                    oldestTicks = kvp.Value.CreatedTicks;
                }

                if (oldestTicks == long.MaxValue) return;
                DropFragment(oldestId, "pending fragment limit exceeded");
            }
        }

        private void DropOlderStateFragments(uint completedSequenceId)
        {
            _fragmentDropScratch.Clear();
            foreach (var kvp in _pendingFragments)
            {
                if (IsStateUpdate(kvp.Value.OriginalMessageType) && kvp.Key < completedSequenceId)
                    _fragmentDropScratch.Add(kvp.Key);
            }

            for (int i = 0; i < _fragmentDropScratch.Count; i++)
                DropFragment(_fragmentDropScratch[i], $"superseded by StateUpdate seq={completedSequenceId}");
        }

        private void DropFragment(uint sequenceId, string reason)
        {
            if (!_pendingFragments.TryGetValue(sequenceId, out var buffer)) return;
            MpLog.WarnThrottle("LiteNetFragmentDrop", "LiteNet",
                $"Discarding fragment seq={sequenceId} type={buffer.OriginalMessageType} received={buffer.ReceivedCount}/{buffer.Chunks.Length}: {reason}",
                2f);
            _fragmentDrops++;
            _pendingFragments.Remove(sequenceId);
        }

        private void ClearFragments()
        {
            _pendingFragments.Clear();
            _fragmentDropScratch.Clear();
            _hasCompletedStateSequence = false;
            _latestCompletedStateSequence = 0;
        }

        private bool IsOlderCompletedState(uint sequenceId) =>
            _hasCompletedStateSequence && sequenceId <= _latestCompletedStateSequence;

        private static bool IsStateUpdate(byte originalType) =>
            originalType == (byte)MessageType.StateUpdate;

    }
}
