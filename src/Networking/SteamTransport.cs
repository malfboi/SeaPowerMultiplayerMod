using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Steamworks;

namespace SeapowerMultiplayer.Transport
{
    public class SteamTransport : ITransport
    {
        private HSteamListenSocket _listenSocket;
        private HSteamNetConnection _connectionToHost;
        private readonly List<HSteamNetConnection> _clientConnections = new();
        private bool _isHost;
        private bool _running;

        private Callback<SteamNetConnectionStatusChangedCallback_t>? _connectionStatusCallback;

        private static ManualLogSource Log => Plugin.Log;

        private const int MaxMessages = 64;
        private readonly IntPtr[] _messagePointers = new IntPtr[MaxMessages];
        private readonly byte[] _receiveBuffer = new byte[512 * 1024]; // 512KB

        // ── Fragmentation ────────────────────────────────────────────────────
        // SteamNetworkingSockets has a ~512KB per-message limit. Session sync
        // messages can exceed this after gameplay (save files grow to ~1MB+).
        // Fragment large reliable messages into chunks under the limit.

        private const int MaxChunkPayload = 450_000;  // 450KB payload per chunk
        private const int FragmentHeaderSize = 9;      // marker(1) + id(4) + index(2) + total(2)
        private const byte FragmentMarker = 0xFF;      // first byte; MessageType enum uses 0-12

        private uint _nextFragmentId;

        private readonly Dictionary<uint, FragmentBuffer> _pendingFragments = new();
        private long _lastCleanupTicks;

        private class FragmentBuffer
        {
            public byte[][] Chunks;
            public int[] ChunkLengths;
            public int ReceivedCount;
            public int TotalLength;
            public long CreatedTicks;

            public FragmentBuffer(int totalChunks)
            {
                Chunks = new byte[totalChunks][];
                ChunkLengths = new int[totalChunks];
                CreatedTicks = DateTime.UtcNow.Ticks;
            }
        }

        /// <summary>Host SteamID is read from SteamLobbyManager when connecting as client.</summary>

        public bool IsConnected => _isHost
            ? _clientConnections.Count > 0
            : _connectionToHost != HSteamNetConnection.Invalid;

        public int RttMs { get; private set; }

        public event Action<byte[], int>? OnDataReceived;
        public event Action? OnPeerConnected;
        public event Action? OnPeerDisconnected;

        public void Start(bool asHost)
        {
            _isHost = asHost;

            _connectionStatusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);

            if (asHost)
            {
                _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
                Log.LogInfo("[SteamTransport] Listening for P2P connections");
            }
            else
            {
                var hostId = SteamLobbyManager.HostSteamId;
                var identity = new SteamNetworkingIdentity();
                identity.SetSteamID(hostId);
                _connectionToHost = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null);
                Log.LogInfo($"[SteamTransport] Connecting to host {hostId}");
            }

            _running = true;
        }

        public void Stop()
        {
            if (!_running) return;

            if (_isHost)
            {
                foreach (var conn in _clientConnections)
                    SteamNetworkingSockets.CloseConnection(conn, 0, "Host shutting down", false);
                _clientConnections.Clear();

                if (_listenSocket != HSteamListenSocket.Invalid)
                {
                    SteamNetworkingSockets.CloseListenSocket(_listenSocket);
                    _listenSocket = HSteamListenSocket.Invalid;
                }
            }
            else
            {
                if (_connectionToHost != HSteamNetConnection.Invalid)
                {
                    SteamNetworkingSockets.CloseConnection(_connectionToHost, 0, "Client disconnecting", false);
                    _connectionToHost = HSteamNetConnection.Invalid;
                }
            }

            _connectionStatusCallback?.Dispose();
            _connectionStatusCallback = null;
            _pendingFragments.Clear();
            _running = false;
            Log.LogInfo("[SteamTransport] Stopped.");
        }

        public void Poll()
        {
            if (!_running) return;

            if (_isHost)
            {
                foreach (var conn in _clientConnections)
                    ReceiveMessages(conn);
            }
            else if (_connectionToHost != HSteamNetConnection.Invalid)
            {
                ReceiveMessages(_connectionToHost);
            }

            CleanupStaleFragments();
            UpdateRtt();
        }

        public void SendToServer(byte[] data, int length, TransportDelivery delivery)
        {
            if (_connectionToHost == HSteamNetConnection.Invalid) return;
            SendMessage(_connectionToHost, data, length, delivery);
        }

        public void BroadcastToClients(byte[] data, int length, TransportDelivery delivery)
        {
            foreach (var conn in _clientConnections)
                SendMessage(conn, data, length, delivery);
        }

        private void SendMessage(HSteamNetConnection conn, byte[] data, int length, TransportDelivery delivery)
        {
            if (length <= MaxChunkPayload)
            {
                SendRaw(conn, data, length, delivery);
                return;
            }

            // Large message — fragment for reliable delivery
            if (delivery == TransportDelivery.Unreliable)
            {
                Log.LogWarning($"[SteamTransport] Unreliable message too large ({length} bytes), sending anyway");
                SendRaw(conn, data, length, delivery);
                return;
            }

            uint fragmentId = _nextFragmentId++;
            int totalChunks = (length + MaxChunkPayload - 1) / MaxChunkPayload;

            Log.LogInfo($"[SteamTransport] Fragmenting message: {length} bytes → {totalChunks} chunks (id={fragmentId})");

            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * MaxChunkPayload;
                int payloadLen = Math.Min(MaxChunkPayload, length - offset);
                int chunkLen = FragmentHeaderSize + payloadLen;

                byte[] chunk = new byte[chunkLen];
                chunk[0] = FragmentMarker;
                chunk[1] = (byte)(fragmentId & 0xFF);
                chunk[2] = (byte)((fragmentId >> 8) & 0xFF);
                chunk[3] = (byte)((fragmentId >> 16) & 0xFF);
                chunk[4] = (byte)((fragmentId >> 24) & 0xFF);
                chunk[5] = (byte)(i & 0xFF);
                chunk[6] = (byte)((i >> 8) & 0xFF);
                chunk[7] = (byte)(totalChunks & 0xFF);
                chunk[8] = (byte)((totalChunks >> 8) & 0xFF);

                Buffer.BlockCopy(data, offset, chunk, FragmentHeaderSize, payloadLen);
                SendRaw(conn, chunk, chunkLen, delivery);
            }
        }

        private unsafe void SendRaw(HSteamNetConnection conn, byte[] data, int length, TransportDelivery delivery)
        {
            int flags = delivery switch
            {
                TransportDelivery.Unreliable => Constants.k_nSteamNetworkingSend_Unreliable,
                TransportDelivery.Reliable => Constants.k_nSteamNetworkingSend_Reliable
                                            | Constants.k_nSteamNetworkingSend_NoNagle,
                TransportDelivery.ReliableOrdered => Constants.k_nSteamNetworkingSend_Reliable,
                _ => Constants.k_nSteamNetworkingSend_Reliable,
            };

            fixed (byte* ptr = data)
            {
                EResult result = SteamNetworkingSockets.SendMessageToConnection(
                    conn, (IntPtr)ptr, (uint)length, flags, out _);
                if (result != EResult.k_EResultOK)
                    Log.LogError($"[SteamTransport] Send failed: {result}, size={length}");
            }
        }

        private void ReceiveMessages(HSteamNetConnection conn)
        {
            int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(conn, _messagePointers, MaxMessages);

            for (int i = 0; i < count; i++)
            {
                var msg = SteamNetworkingMessage_t.FromIntPtr(_messagePointers[i]);
                int length = msg.m_cbSize;
                byte[] data;
                if (length <= _receiveBuffer.Length)
                {
                    Marshal.Copy(msg.m_pData, _receiveBuffer, 0, length);
                    data = _receiveBuffer;
                }
                else
                {
                    data = new byte[length];
                    Marshal.Copy(msg.m_pData, data, 0, length);
                }

                SteamNetworkingMessage_t.Release(_messagePointers[i]);

                // Check for fragment marker
                if (length >= FragmentHeaderSize && data[0] == FragmentMarker)
                {
                    HandleFragment(data, length);
                }
                else
                {
                    OnDataReceived?.Invoke(data, length);
                }
            }
        }

        private void HandleFragment(byte[] data, int length)
        {
            uint fragmentId = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
            int chunkIndex  = data[5] | (data[6] << 8);
            int totalChunks = data[7] | (data[8] << 8);

            if (totalChunks <= 0 || chunkIndex < 0 || chunkIndex >= totalChunks)
            {
                Log.LogWarning($"[SteamTransport] Invalid fragment header: id={fragmentId} chunk={chunkIndex}/{totalChunks}");
                return;
            }

            if (!_pendingFragments.TryGetValue(fragmentId, out var buffer))
            {
                buffer = new FragmentBuffer(totalChunks);
                _pendingFragments[fragmentId] = buffer;
            }

            int payloadLen = length - FragmentHeaderSize;

            // Guard against duplicate chunks
            if (buffer.Chunks[chunkIndex] != null) return;

            buffer.Chunks[chunkIndex] = new byte[payloadLen];
            Buffer.BlockCopy(data, FragmentHeaderSize, buffer.Chunks[chunkIndex], 0, payloadLen);
            buffer.ChunkLengths[chunkIndex] = payloadLen;
            buffer.TotalLength += payloadLen;
            buffer.ReceivedCount++;

            if (buffer.ReceivedCount == totalChunks)
            {
                // Reassemble
                byte[] reassembled = new byte[buffer.TotalLength];
                int offset = 0;
                for (int i = 0; i < totalChunks; i++)
                {
                    Buffer.BlockCopy(buffer.Chunks[i], 0, reassembled, offset, buffer.ChunkLengths[i]);
                    offset += buffer.ChunkLengths[i];
                }

                _pendingFragments.Remove(fragmentId);
                Log.LogInfo($"[SteamTransport] Reassembled fragment id={fragmentId}: {totalChunks} chunks → {buffer.TotalLength} bytes");
                OnDataReceived?.Invoke(reassembled, buffer.TotalLength);
            }
        }

        private void CleanupStaleFragments()
        {
            long now = DateTime.UtcNow.Ticks;
            // Check every ~5 seconds
            if (now - _lastCleanupTicks < 50_000_000L) return;
            _lastCleanupTicks = now;

            long staleThreshold = 100_000_000L; // 10 seconds in ticks
            List<uint>? staleIds = null;

            foreach (var kvp in _pendingFragments)
            {
                if (now - kvp.Value.CreatedTicks > staleThreshold)
                {
                    staleIds ??= new List<uint>();
                    staleIds.Add(kvp.Key);
                }
            }

            if (staleIds != null)
            {
                foreach (var id in staleIds)
                {
                    var buf = _pendingFragments[id];
                    Log.LogWarning($"[SteamTransport] Discarding stale fragment id={id}: {buf.ReceivedCount}/{buf.Chunks.Length} chunks received");
                    _pendingFragments.Remove(id);
                }
            }
        }

        private void UpdateRtt()
        {
            HSteamNetConnection conn = _isHost
                ? (_clientConnections.Count > 0 ? _clientConnections[0] : HSteamNetConnection.Invalid)
                : _connectionToHost;

            if (conn == HSteamNetConnection.Invalid) return;

            SteamNetConnectionRealTimeStatus_t status = default;
            SteamNetConnectionRealTimeLaneStatus_t laneStatus = default;
            var result = SteamNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref laneStatus);
            if (result == EResult.k_EResultOK)
            {
                RttMs = status.m_nPing;
            }
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t callback)
        {
            var conn = callback.m_hConn;
            var info = callback.m_info;
            var oldState = callback.m_eOldState;

            Log.LogInfo($"[SteamTransport] Connection status: {oldState} -> {info.m_eState} (peer={info.m_identityRemote.GetSteamID()})");

            switch (info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    if (_isHost)
                    {
                        var result = SteamNetworkingSockets.AcceptConnection(conn);
                        if (result != EResult.k_EResultOK)
                            Log.LogError($"[SteamTransport] AcceptConnection failed: {result}");
                    }
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    if (_isHost)
                    {
                        _clientConnections.Add(conn);
                        Log.LogInfo($"[SteamTransport] Client connected ({_clientConnections.Count} peers)");
                    }
                    else
                    {
                        Log.LogInfo("[SteamTransport] Connected to host");
                    }
                    OnPeerConnected?.Invoke();
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    Log.LogInfo($"[SteamTransport] Connection closed: {info.m_szEndDebug}");

                    if (_isHost)
                    {
                        _clientConnections.Remove(conn);
                    }
                    else
                    {
                        _connectionToHost = HSteamNetConnection.Invalid;
                    }

                    SteamNetworkingSockets.CloseConnection(conn, 0, null, false);
                    OnPeerDisconnected?.Invoke();
                    break;
            }
        }
    }
}
