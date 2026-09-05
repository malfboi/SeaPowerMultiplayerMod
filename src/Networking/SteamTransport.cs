using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using Steamworks;

namespace SeapowerMultiplayer.Transport
{
    public class SteamTransport : ITransport
    {
        private HSteamListenSocket _listenSocket;
        private HSteamNetConnection _connectionToHost;

        /// <summary>Host: connected clients, keyed by the id the rest of the mod
        /// addresses them by. Unused on a client, which has exactly one peer
        /// (<see cref="PeerId.Host"/>, held in <see cref="_connectionToHost"/>).</summary>
        private readonly PeerTable<HSteamNetConnection> _peers = new();

        /// <summary>Latest ping per peer id, refreshed each Poll. Entries are
        /// dropped on disconnect so a stale number can't outlive its link.</summary>
        private readonly Dictionary<int, int> _rttOf = new();

        /// <summary>Snapshot of the ids to poll. Receiving can run a Steam callback
        /// that adds or removes a peer, and mutating the table mid-iteration would
        /// throw.</summary>
        private readonly List<int> _pollIds = new();

        private static readonly int[] HostOnly = { PeerId.Host };
        private static readonly int[] NoPeers = new int[0];

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
        //
        // Chunk size must stay well under the connection's send buffer, not just
        // under the per-message limit: SendMessageToConnection returns
        // k_EResultLimitExceeded when the buffer already holds too much unsent
        // data. With the old 450KB chunks only one chunk fit in Steam's 512KB
        // default buffer, so every multi-chunk message failed on chunk 1.
        private const int MaxChunkPayload = 128_000;  // 128KB payload per chunk
        private const int FragmentHeaderSize = 9;      // marker(1) + id(4) + index(2) + total(2)
        private const byte FragmentMarker = 0xFF;      // first byte; MessageType enum uses 0-12

        // Send buffer must hold a whole fragmented session sync at once. Steam's
        // default is 512KB; saves compress to several MB late in a mission.
        private const int SendBufferBytes = 16 * 1024 * 1024;

        // Fallback backoff if the buffer fills anyway (very slow link). Escalating
        // delays give the buffer a realistic window to drain: ~5s total.
        private const int FragmentRetryCount = 10;
        private const int FragmentRetryBaseDelayMs = 100;
        private const int FragmentRetryMaxDelayMs = 800;

        private uint _nextFragmentId;

        /// <summary>Result of the most recent SendMessageToConnection, for error reporting.</summary>
        private EResult _lastResult = EResult.k_EResultOK;

        /// <summary>
        /// In-flight reassembly buffers, keyed by SENDER as well as fragment id.
        ///
        /// The fragment id is only unique within one sender: every peer runs its own
        /// _nextFragmentId counter starting at 0. Keyed on the id alone, two clients
        /// uploading a session sync at the same time would both write into one
        /// buffer, and it would "complete" as an interleaved mixture of two saves.
        /// The peer id makes the key globally unique, which is what the counter was
        /// always assuming.
        /// </summary>
        private readonly Dictionary<(int PeerId, uint FragmentId), FragmentBuffer> _pendingFragments = new();
        private long _lastCleanupTicks;

        private class FragmentBuffer
        {
            public byte[][] Chunks;
            public int[] ChunkLengths;
            public int ReceivedCount;
            public int TotalLength;
            public long CreatedTicks;
            /// <summary>Last time any chunk for this id arrived. The staleness clock
            /// runs from here, not CreatedTicks, so a slow-but-progressing transfer
            /// is never discarded.</summary>
            public long LastChunkTicks;

            public FragmentBuffer(int totalChunks)
            {
                Chunks = new byte[totalChunks][];
                ChunkLengths = new int[totalChunks];
                CreatedTicks = DateTime.UtcNow.Ticks;
                LastChunkTicks = CreatedTicks;
            }
        }

        /// <summary>Host SteamID is read from SteamLobbyManager when connecting as client.</summary>

        public bool IsConnected => _isHost
            ? _peers.Count > 0
            : _connectionToHost != HSteamNetConnection.Invalid;

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

        public bool LastSendFailed { get; private set; }
        public string? LastSendError { get; private set; }

        public bool TryGetPacketStats(int peerId, out long packetsSent, out long packetsLost)
        {
            // Steam Networking Sockets exposes a smoothed quality metric, not raw
            // packet counters - the overlay shows n/a on this transport.
            packetsSent = 0;
            packetsLost = 0;
            return false;
        }

        public event Action<int, byte[], int>? OnDataReceived;
        public event Action<int>? OnPeerConnected;
        public event Action<int>? OnPeerDisconnected;
        public event Action<int, string>? OnReceiveFailed;

        /// <summary>Resolve a peer id to its connection, or Invalid if it is not one
        /// of ours. On a client only PeerId.Host resolves.</summary>
        private HSteamNetConnection ConnFor(int peerId)
        {
            if (!_isHost)
                return peerId == PeerId.Host ? _connectionToHost : HSteamNetConnection.Invalid;
            return _peers.TryGetConn(peerId, out var conn) ? conn : HSteamNetConnection.Invalid;
        }

        /// <summary>Resolve a connection to its peer id, or PeerId.None if it is not
        /// tracked.</summary>
        private int IdFor(HSteamNetConnection conn)
        {
            if (!_isHost) return PeerId.Host;
            return _peers.TryGetId(conn, out int id) ? id : PeerId.None;
        }

        public void Start(bool asHost)
        {
            _isHost = asHost;

            _connectionStatusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);

            ConfigureSendBuffer();
            ConfigureTimeouts();

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

        /// <summary>
        /// Raise the connected-state timeout before any connection is created.
        /// Steam declares a connection dead after 10s of silence by default, which
        /// high-latency players lose to during ordinary stalls. A dead link now
        /// freezes the session instead of silently splitting it, so waiting longer
        /// before giving up costs nothing.
        /// </summary>
        private void ConfigureTimeouts()
        {
            int timeoutMs = Plugin.Instance.CfgDisconnectTimeoutSec.Value * 1000;
            IntPtr valuePtr = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(valuePtr, timeoutMs);
                bool ok = SteamNetworkingUtils.SetConfigValue(
                    ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                    IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    valuePtr);

                if (ok)
                    Log.LogInfo($"[SteamTransport] TimeoutConnected set to {timeoutMs}ms");
                else
                    Log.LogWarning("[SteamTransport] Could not raise TimeoutConnected — high-ping players may drop early");
            }
            finally
            {
                Marshal.FreeHGlobal(valuePtr);
            }
        }

        /// <summary>
        /// Raise the per-connection send buffer before any connection is created.
        /// Steam's 512KB default only fits one fragment chunk, so every multi-chunk
        /// message (i.e. every large session sync) failed on the second chunk with
        /// k_EResultLimitExceeded no matter how many times it retried.
        /// </summary>
        private void ConfigureSendBuffer()
        {
            IntPtr valuePtr = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(valuePtr, SendBufferBytes);
                bool ok = SteamNetworkingUtils.SetConfigValue(
                    ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                    IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    valuePtr);

                if (ok)
                    Log.LogInfo($"[SteamTransport] SendBufferSize set to {SendBufferBytes / (1024 * 1024)}MB");
                else
                    Log.LogWarning("[SteamTransport] Could not raise SendBufferSize — large session syncs may fail");
            }
            finally
            {
                Marshal.FreeHGlobal(valuePtr);
            }
        }

        public void Stop()
        {
            if (!_running) return;

            if (_isHost)
            {
                foreach (var id in _peers.Ids)
                {
                    if (_peers.TryGetConn(id, out var conn))
                        SteamNetworkingSockets.CloseConnection(conn, 0, "Host shutting down", false);
                }
                _peers.Clear();

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
            _rttOf.Clear();
            _running = false;
            Log.LogInfo("[SteamTransport] Stopped.");
        }

        public void DisconnectPeers()
        {
            if (!_running) return;

            if (_isHost)
            {
                foreach (var id in _peers.Ids)
                {
                    if (_peers.TryGetConn(id, out var conn))
                        SteamNetworkingSockets.CloseConnection(conn, 0, "Refused by host", false);
                }
                _peers.Clear();
                // Listen socket stays open - host remains joinable
            }
            else if (_connectionToHost != HSteamNetConnection.Invalid)
            {
                SteamNetworkingSockets.CloseConnection(_connectionToHost, 0, "Disconnecting", false);
                _connectionToHost = HSteamNetConnection.Invalid;
            }
            _pendingFragments.Clear();
            _rttOf.Clear();
            Log.LogInfo("[SteamTransport] Disconnected peers (transport stays up).");
        }

        public void DisconnectPeer(int peerId, string reason)
        {
            if (!_running) return;

            var conn = ConnFor(peerId);
            if (conn == HSteamNetConnection.Invalid) return;

            SteamNetworkingSockets.CloseConnection(conn, 0, reason, false);

            if (_isHost) _peers.Remove(conn);
            else _connectionToHost = HSteamNetConnection.Invalid;

            DropFragmentsFor(peerId);
            _rttOf.Remove(peerId);
            Log.LogInfo($"[SteamTransport] Disconnected peer {peerId}: {reason}");
        }

        public void Poll()
        {
            if (!_running) return;

            if (_isHost)
            {
                // Snapshot: ReceiveMessages can run a status callback that mutates
                // the peer table, and iterating it live would throw.
                _pollIds.Clear();
                _pollIds.AddRange(_peers.Ids);
                for (int i = 0; i < _pollIds.Count; i++)
                {
                    int id = _pollIds[i];
                    if (_peers.TryGetConn(id, out var conn))
                        ReceiveMessages(id, conn);
                }
            }
            else if (_connectionToHost != HSteamNetConnection.Invalid)
            {
                ReceiveMessages(PeerId.Host, _connectionToHost);
            }

            CleanupStaleFragments();
            UpdateRtt();
        }

        public void SendToServer(byte[] data, int length, TransportDelivery delivery)
        {
            if (_connectionToHost == HSteamNetConnection.Invalid) return;
            SendMessage(_connectionToHost, data, length, delivery);
        }

        public void SendToPeer(int peerId, byte[] data, int length, TransportDelivery delivery)
        {
            var conn = ConnFor(peerId);
            if (conn == HSteamNetConnection.Invalid) return;
            SendMessage(conn, data, length, delivery);
        }

        public void BroadcastToClients(byte[] data, int length, TransportDelivery delivery)
        {
            var ids = _peers.Ids;
            for (int i = 0; i < ids.Count; i++)
            {
                if (_peers.TryGetConn(ids[i], out var conn))
                    SendMessage(conn, data, length, delivery);
            }
        }

        private void SendMessage(HSteamNetConnection conn, byte[] data, int length, TransportDelivery delivery)
        {
            LastSendFailed = false;
            LastSendError = null;

            if (length <= MaxChunkPayload)
            {
                if (!SendRaw(conn, data, length, delivery))
                    FailSend($"Steam rejected the message ({DescribeResult(_lastResult)}).");
                return;
            }

            // Large message - fragment for reliable delivery
            if (delivery == TransportDelivery.Unreliable)
            {
                Log.LogWarning($"[SteamTransport] Unreliable message too large ({length} bytes), sending anyway");
                if (!SendRaw(conn, data, length, delivery))
                    FailSend($"Steam rejected an oversized unreliable message ({DescribeResult(_lastResult)}).");
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

                // Retry with backpressure - the send buffer can still fill on a very
                // slow link. Delays escalate so a slow drain gets a real window
                // (~5s total) instead of ten futile attempts in one second. The game
                // is paused during session sync so a brief main-thread block is
                // acceptable.
                bool sent = false;
                int delayMs = FragmentRetryBaseDelayMs;
                for (int attempt = 0; attempt < FragmentRetryCount; attempt++)
                {
                    if (attempt > 0)
                    {
                        Log.LogInfo($"[SteamTransport] Retry {attempt}/{FragmentRetryCount} for chunk {i}/{totalChunks} (id={fragmentId}), waiting {delayMs}ms");
                        Thread.Sleep(delayMs);
                        delayMs = Math.Min(delayMs * 2, FragmentRetryMaxDelayMs);
                    }
                    if (SendRaw(conn, chunk, chunkLen, delivery))
                    {
                        sent = true;
                        break;
                    }
                }
                if (!sent)
                {
                    Log.LogError($"[SteamTransport] Fragment chunk {i}/{totalChunks} (id={fragmentId}) failed after {FragmentRetryCount} retries — aborting send");
                    FailSend($"Steam dropped chunk {i + 1} of {totalChunks} ({DescribeResult(_lastResult)}).");
                    return;
                }
            }
        }

        private unsafe bool SendRaw(HSteamNetConnection conn, byte[] data, int length, TransportDelivery delivery)
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
                _lastResult = result;
                if (result != EResult.k_EResultOK)
                {
                    Log.LogError($"[SteamTransport] Send failed: {result}, size={length}");
                    return false;
                }
                return true;
            }
        }

        private void FailSend(string reason)
        {
            LastSendFailed = true;
            LastSendError = reason;
        }

        /// <summary>Plain-English rendering of the EResult codes send can realistically return.</summary>
        private static string DescribeResult(EResult result) => result switch
        {
            EResult.k_EResultLimitExceeded  => "send buffer full",
            EResult.k_EResultNoConnection   => "connection closed",
            EResult.k_EResultInvalidParam   => "message too large",
            EResult.k_EResultInvalidState   => "connection not ready",
            _                               => result.ToString(),
        };

        private void ReceiveMessages(int peerId, HSteamNetConnection conn)
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
                    HandleFragment(peerId, data, length);
                }
                else
                {
                    OnDataReceived?.Invoke(peerId, data, length);
                }
            }
        }

        private void HandleFragment(int peerId, byte[] data, int length)
        {
            uint fragmentId = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
            int chunkIndex  = data[5] | (data[6] << 8);
            int totalChunks = data[7] | (data[8] << 8);

            if (totalChunks <= 0 || chunkIndex < 0 || chunkIndex >= totalChunks)
            {
                Log.LogWarning($"[SteamTransport] Invalid fragment header from peer {peerId}: id={fragmentId} chunk={chunkIndex}/{totalChunks}");
                return;
            }

            var key = (peerId, fragmentId);
            if (!_pendingFragments.TryGetValue(key, out var buffer))
            {
                buffer = new FragmentBuffer(totalChunks);
                _pendingFragments[key] = buffer;
            }

            // Stamp before the duplicate guard: a resent chunk still proves the
            // transfer is alive, and only silence should age a buffer out.
            buffer.LastChunkTicks = DateTime.UtcNow.Ticks;

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

                _pendingFragments.Remove(key);
                Log.LogInfo($"[SteamTransport] Reassembled fragment id={fragmentId} from peer {peerId}: {totalChunks} chunks → {buffer.TotalLength} bytes");
                OnDataReceived?.Invoke(peerId, reassembled, buffer.TotalLength);
            }
        }

        /// <summary>Discard every in-flight reassembly belonging to one peer. Used
        /// when that peer goes away: anything half-received is dead with the
        /// connection, and leaving it would make the idle sweep report a stalled
        /// transfer on top of the disconnect the player is already being told
        /// about. Other peers' transfers are untouched.</summary>
        private void DropFragmentsFor(int peerId)
        {
            if (_pendingFragments.Count == 0) return;

            List<(int, uint)>? doomed = null;
            foreach (var kvp in _pendingFragments)
            {
                if (kvp.Key.PeerId == peerId)
                {
                    doomed ??= new List<(int, uint)>();
                    doomed.Add(kvp.Key);
                }
            }

            if (doomed == null) return;
            foreach (var key in doomed)
                _pendingFragments.Remove(key);
        }

        /// <summary>
        /// Drop fragment buffers that have gone silent. The clock is idle-based
        /// (time since the last chunk), not age-based: a multi-megabyte session
        /// sync on a slow link legitimately takes minutes, and the old age-based
        /// 10s window discarded those mid-transfer — the client then sat forever
        /// with no mission and nothing on screen to say why.
        ///
        /// The window matches the connection timeout because that is exactly how
        /// long Steam will keep retrying a reliable chunk: while the connection
        /// lives the rest of the message is still coming, and anything shorter
        /// throws away a transfer that would have completed.
        /// </summary>
        private void CleanupStaleFragments()
        {
            if (_pendingFragments.Count == 0) return;

            long now = DateTime.UtcNow.Ticks;
            // Check every ~5 seconds
            if (now - _lastCleanupTicks < 50_000_000L) return;
            _lastCleanupTicks = now;

            long idleThreshold = Plugin.Instance.CfgDisconnectTimeoutSec.Value * TimeSpan.TicksPerSecond;
            List<(int PeerId, uint FragmentId)>? staleKeys = null;

            foreach (var kvp in _pendingFragments)
            {
                if (now - kvp.Value.LastChunkTicks > idleThreshold)
                {
                    staleKeys ??= new List<(int, uint)>();
                    staleKeys.Add(kvp.Key);
                }
            }

            if (staleKeys == null) return;

            foreach (var key in staleKeys)
            {
                var buf = _pendingFragments[key];
                _pendingFragments.Remove(key);

                int idleSec  = (int)((now - buf.LastChunkTicks) / TimeSpan.TicksPerSecond);
                int totalSec = (int)((now - buf.CreatedTicks)   / TimeSpan.TicksPerSecond);
                int gotKb    = buf.TotalLength / 1024;

                Log.LogError($"[SteamTransport] Fragment id={key.FragmentId} from peer {key.PeerId} stalled: " +
                             $"{buf.ReceivedCount}/{buf.Chunks.Length} chunks " +
                             $"({gotKb} KB) after {totalSec}s, no data for {idleSec}s — discarding");

                // The sender got an OK from Steam and will never resend, so this
                // message is simply gone. Say so rather than leaving the player
                // staring at a screen that never loads.
                OnReceiveFailed?.Invoke(key.PeerId,
                    $"A large transfer stopped {idleSec}s short of completing " +
                    $"({buf.ReceivedCount} of {buf.Chunks.Length} parts, {gotKb} KB received).");
            }
        }

        private void UpdateRtt()
        {
            if (_isHost)
            {
                var ids = _peers.Ids;
                for (int i = 0; i < ids.Count; i++)
                {
                    if (_peers.TryGetConn(ids[i], out var conn) && TryGetPing(conn, out int ping))
                        _rttOf[ids[i]] = ping;
                }
                return;
            }

            if (_connectionToHost == HSteamNetConnection.Invalid) return;
            if (TryGetPing(_connectionToHost, out int hostPing))
                _rttOf[PeerId.Host] = hostPing;
        }

        private static bool TryGetPing(HSteamNetConnection conn, out int pingMs)
        {
            pingMs = 0;
            if (conn == HSteamNetConnection.Invalid) return false;

            SteamNetConnectionRealTimeStatus_t status = default;
            SteamNetConnectionRealTimeLaneStatus_t laneStatus = default;
            var result = SteamNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref laneStatus);
            if (result != EResult.k_EResultOK) return false;

            pingMs = status.m_nPing;
            return true;
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
                {
                    int id;
                    if (_isHost)
                    {
                        id = _peers.Add(conn);
                        Log.LogInfo($"[SteamTransport] Client connected as peer {id} ({_peers.Count} peers)");
                    }
                    else
                    {
                        id = PeerId.Host;
                        Log.LogInfo("[SteamTransport] Connected to host");
                    }
                    OnPeerConnected?.Invoke(id);
                    break;
                }

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                {
                    Log.LogInfo($"[SteamTransport] Connection closed: {info.m_szEndDebug}");

                    int id;
                    if (_isHost)
                    {
                        id = _peers.Remove(conn);
                    }
                    else
                    {
                        id = PeerId.Host;
                        _connectionToHost = HSteamNetConnection.Invalid;
                    }

                    // Only this peer's half-received transfers die with it - the
                    // others are still arriving.
                    if (id != PeerId.None)
                    {
                        DropFragmentsFor(id);
                        _rttOf.Remove(id);
                    }

                    SteamNetworkingSockets.CloseConnection(conn, 0, null, false);

                    // A peer that was never registered was never announced as
                    // connected either, so announcing its departure would leave
                    // listeners unbalanced.
                    if (id != PeerId.None)
                        OnPeerDisconnected?.Invoke(id);
                    break;
                }
            }
        }
    }
}
