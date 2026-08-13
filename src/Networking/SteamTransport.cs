using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Steamworks;

namespace SeapowerMultiplayer.Transport
{
    public class SteamTransport : ITransport
    {
        /// <summary>One client connection, with the identity Steam hands us at accept
        /// time and its own outbound queue. Both used to be discarded: the SteamID was
        /// logged and dropped, and sends went straight out with a blocking retry.</summary>
        private sealed class Conn
        {
            public HSteamNetConnection Handle;
            public PeerId Id;
            public CSteamID Steam;
            public readonly Queue<Pending> Outbox = new();
            public long RetryAtMs;
            public int Attempts;
        }

        private sealed class Pending
        {
            public byte[] Data = Array.Empty<byte>();
            public int Length;
            public TransportDelivery Delivery;
        }

        private HSteamListenSocket _listenSocket;
        private HSteamNetConnection _connectionToHost;

        private readonly Dictionary<HSteamNetConnection, Conn> _byHandle = new();
        private readonly Dictionary<PeerId, Conn> _byId = new();
        private readonly List<PeerId> _connected = new();
        private int _nextPeerId;

        /// <summary>The guest's single connection, modelled as a Conn so the outbox and
        /// fragment paths are identical in both roles.</summary>
        private Conn? _hostConn;

        private bool _isHost;
        private bool _running;

        private Callback<SteamNetConnectionStatusChangedCallback_t>? _connectionStatusCallback;

        private static ManualLogSource Log => Plugin.Log;
        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        private const int MaxMessages = 64;
        private readonly IntPtr[] _messagePointers = new IntPtr[MaxMessages];

        // Scratch for the copy out of Steam's message pointer.
        //
        // INVARIANT: safe to share across peers ONLY because ReceiveMessages drains one
        // connection at a time on the main thread and NetworkManager.Dispatch
        // deserializes eagerly before enqueueing. Nothing may hold this array past the
        // handler - in particular the host's order relay must re-serialize.
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
        private const byte FragmentMarker = 0xFF;      // first byte; MessageType enum stays below 0xFF

        // Send buffer must hold a whole fragmented session sync at once. Steam's
        // default is 512KB; saves compress to several MB late in a mission.
        private const int SendBufferBytes = 16 * 1024 * 1024;

        // Backoff if the buffer fills anyway (very slow link). Escalating delays give
        // the buffer a realistic window to drain: ~5s total. These are now WAITS
        // BETWEEN Poll() ticks, not sleeps - see PumpOutbox.
        private const int FragmentRetryCount = 10;
        private const int FragmentRetryBaseDelayMs = 100;
        private const int FragmentRetryMaxDelayMs = 800;

        private uint _nextFragmentId;

        /// <summary>Result of the most recent SendMessageToConnection, for error reporting.</summary>
        private EResult _lastResult = EResult.k_EResultOK;

        /// <summary>
        /// Half-received transfers, keyed by (sender, fragment id).
        ///
        /// The sender half is not optional. Every peer starts its own _nextFragmentId at
        /// 0, so with two guests sending large messages the ids collide on the FIRST
        /// transfer, not rarely - chunks from both interleave into one buffer, TotalLength
        /// comes out wrong, and the "save" that reassembles is garbage the guest then
        /// tries to load. That presents as a dead loading screen with nothing in the log
        /// to attribute it to.
        /// </summary>
        private readonly Dictionary<(PeerId, uint), FragmentBuffer> _pendingFragments = new();
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
            ? _byId.Count > 0
            : _connectionToHost != HSteamNetConnection.Invalid;

        public int RttMs
        {
            get
            {
                if (!_isHost) return PingOf(_connectionToHost);
                int worst = 0;
                foreach (var c in _byId.Values)
                {
                    int p = PingOf(c.Handle);
                    if (p > worst) worst = p;
                }
                return worst;
            }
        }

        public int RttMsFor(PeerId peer)
        {
            if (!_isHost) return PingOf(_connectionToHost);
            return _byId.TryGetValue(peer, out var c) ? PingOf(c.Handle) : 0;
        }

        public bool LastSendFailed { get; private set; }
        public string? LastSendError { get; private set; }

        public IReadOnlyList<PeerId> ConnectedPeers => _connected;

        public ulong SteamIdOf(PeerId peer)
            => _byId.TryGetValue(peer, out var c) ? c.Steam.m_SteamID : 0UL;

        public bool TryGetPacketStats(PeerId peer, out long packetsSent, out long packetsLost)
        {
            // Steam Networking Sockets exposes a smoothed quality metric, not raw
            // packet counters - the overlay shows n/a on this transport.
            packetsSent = 0;
            packetsLost = 0;
            return false;
        }

        public event Action<PeerId, byte[], int>? OnDataReceived;
        public event Action<PeerId>? OnPeerConnected;
        public event Action<PeerId>? OnPeerDisconnected;
        public event Action<PeerId, string>? OnReceiveFailed;

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
                _hostConn = new Conn { Handle = _connectionToHost, Id = PeerId.Server, Steam = hostId };
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
        ///
        /// The buffer is PER CONNECTION, so four concurrent session syncs are fine.
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
                foreach (var c in _byHandle.Values)
                    SteamNetworkingSockets.CloseConnection(c.Handle, 0, "Host shutting down", false);
                ClearAllPeers();

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
                _hostConn = null;
            }

            _connectionStatusCallback?.Dispose();
            _connectionStatusCallback = null;
            _pendingFragments.Clear();
            _running = false;
            // _nextPeerId is deliberately NOT reset - ids stay unique for the process.
            Log.LogInfo("[SteamTransport] Stopped.");
        }

        /// <summary>Drop one peer, leaving every other connection alive. Refusing a
        /// surplus or incompatible joiner must not kick the players already in.</summary>
        public void Disconnect(PeerId peer, string reason)
        {
            if (!_running) return;

            if (!_isHost)
            {
                if (_connectionToHost == HSteamNetConnection.Invalid) return;
                SteamNetworkingSockets.CloseConnection(_connectionToHost, 0, reason, false);
                _connectionToHost = HSteamNetConnection.Invalid;
                _hostConn = null;
                DropFragmentsOf(PeerId.Server);
                return;
            }

            if (!_byId.TryGetValue(peer, out var c)) return;
            Log.LogInfo($"[SteamTransport] Disconnecting {peer}: {reason}");
            SteamNetworkingSockets.CloseConnection(c.Handle, 0, reason, false);
            ForgetPeer(c);
        }

        public void DisconnectPeers()
        {
            if (!_running) return;

            if (_isHost)
            {
                foreach (var c in _byHandle.Values)
                    SteamNetworkingSockets.CloseConnection(c.Handle, 0, "Refused by host", false);
                ClearAllPeers();
                // Listen socket stays open - host remains joinable
            }
            else if (_connectionToHost != HSteamNetConnection.Invalid)
            {
                SteamNetworkingSockets.CloseConnection(_connectionToHost, 0, "Disconnecting", false);
                _connectionToHost = HSteamNetConnection.Invalid;
                _hostConn = null;
            }
            _pendingFragments.Clear();
            Log.LogInfo("[SteamTransport] Disconnected peers (transport stays up).");
        }

        private void ClearAllPeers()
        {
            _byHandle.Clear();
            _byId.Clear();
            _connected.Clear();
        }

        private void ForgetPeer(Conn c)
        {
            _byHandle.Remove(c.Handle);
            _byId.Remove(c.Id);
            _connected.Remove(c.Id);
            DropFragmentsOf(c.Id);
        }

        /// <summary>Drop only THIS peer's half-received transfers. The old code called
        /// _pendingFragments.Clear() on any connection close, which wiped every other
        /// player's in-flight session sync along with it.</summary>
        private void DropFragmentsOf(PeerId peer)
        {
            if (_pendingFragments.Count == 0) return;
            List<(PeerId, uint)>? doomed = null;
            foreach (var key in _pendingFragments.Keys)
            {
                if (key.Item1 != peer) continue;
                doomed ??= new List<(PeerId, uint)>();
                doomed.Add(key);
            }
            if (doomed == null) return;
            foreach (var k in doomed) _pendingFragments.Remove(k);
        }

        public void Poll()
        {
            if (!_running) return;

            if (_isHost)
            {
                // Snapshot: a status callback during receive can mutate the peer set.
                foreach (var c in new List<Conn>(_byId.Values))
                    ReceiveMessages(c.Id, c.Handle);
            }
            else if (_connectionToHost != HSteamNetConnection.Invalid)
            {
                ReceiveMessages(PeerId.Server, _connectionToHost);
            }

            PumpOutbox();
            CleanupStaleFragments();
        }

        // ── Sending ──────────────────────────────────────────────────────────

        public void SendToServer(byte[] data, int length, TransportDelivery delivery)
        {
            if (_hostConn == null || _connectionToHost == HSteamNetConnection.Invalid) return;
            _hostConn.Handle = _connectionToHost;
            SendMessage(_hostConn, data, length, delivery);
        }

        public void SendTo(PeerId peer, byte[] data, int length, TransportDelivery delivery)
        {
            if (!_isHost) { SendToServer(data, length, delivery); return; }
            if (_byId.TryGetValue(peer, out var c)) SendMessage(c, data, length, delivery);
        }

        public void BroadcastToClients(byte[] data, int length, TransportDelivery delivery)
        {
            foreach (var c in _byId.Values)
                SendMessage(c, data, length, delivery);
        }

        private void SendMessage(Conn c, byte[] data, int length, TransportDelivery delivery)
        {
            LastSendFailed = false;
            LastSendError = null;

            if (length <= MaxChunkPayload)
            {
                Enqueue(c, data, 0, length, delivery, isChunk: false);
                return;
            }

            // Large message - fragment for reliable delivery
            if (delivery == TransportDelivery.Unreliable)
            {
                Log.LogWarning($"[SteamTransport] Unreliable message too large ({length} bytes), sending anyway");
                Enqueue(c, data, 0, length, delivery, isChunk: false);
                return;
            }

            uint fragmentId = _nextFragmentId++;
            int totalChunks = (length + MaxChunkPayload - 1) / MaxChunkPayload;

            Log.LogInfo($"[SteamTransport] Fragmenting message to {c.Id}: {length} bytes → {totalChunks} chunks (id={fragmentId})");

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

                // Already a private array - hand it straight to the queue.
                Enqueue(c, chunk, 0, chunkLen, delivery, isChunk: true);
            }
        }

        /// <summary>
        /// Send now if the peer's queue is empty and Steam accepts it; otherwise queue.
        ///
        /// Queueing when anything is already pending is what preserves ordering: a
        /// reliable stream must not have a small message jump ahead of a chunk waiting
        /// for buffer space.
        /// </summary>
        private void Enqueue(Conn c, byte[] data, int offset, int length,
                             TransportDelivery delivery, bool isChunk)
        {
            if (c.Outbox.Count == 0)
            {
                if (SendRaw(c.Handle, data, offset, length, delivery)) return;
                if (_lastResult != EResult.k_EResultLimitExceeded)
                {
                    // Not backpressure - retrying will not help.
                    FailSend($"Steam rejected the message ({DescribeResult(_lastResult)}).");
                    return;
                }
            }

            // COPY unless the caller already handed us a private array. The plain path
            // is given NetworkManager's shared _writer buffer, which is reused on the
            // very next send - queueing it without a copy would transmit whatever
            // message happened to be serialized last.
            byte[] owned;
            if (isChunk && offset == 0 && length == data.Length)
            {
                owned = data;
            }
            else
            {
                owned = new byte[length];
                Buffer.BlockCopy(data, offset, owned, 0, length);
            }

            c.Outbox.Enqueue(new Pending { Data = owned, Length = length, Delivery = delivery });
        }

        /// <summary>
        /// Drain queued sends, one peer at a time, from Poll().
        ///
        /// This replaces a Thread.Sleep retry ladder that blocked the MAIN THREAD for up
        /// to ~5s per chunk. Its justification was that the game is paused during session
        /// sync - which stops being true the moment a player joins mid-mission while
        /// everyone else is playing, and with three guests the stall multiplied by three.
        /// </summary>
        private void PumpOutbox()
        {
            long now = _clock.ElapsedMilliseconds;

            if (_isHost)
            {
                foreach (var c in _byId.Values) PumpOne(c, now);
            }
            else if (_hostConn != null)
            {
                PumpOne(_hostConn, now);
            }
        }

        private void PumpOne(Conn c, long now)
        {
            {
                while (c.Outbox.Count > 0)
                {
                    if (now < c.RetryAtMs) break;

                    var p = c.Outbox.Peek();
                    if (SendRaw(c.Handle, p.Data, 0, p.Length, p.Delivery))
                    {
                        c.Outbox.Dequeue();
                        c.Attempts = 0;
                        c.RetryAtMs = 0;
                        continue;
                    }

                    if (_lastResult != EResult.k_EResultLimitExceeded)
                    {
                        Log.LogError($"[SteamTransport] Dropping {c.Outbox.Count} queued message(s) to {c.Id}: {DescribeResult(_lastResult)}");
                        c.Outbox.Clear();
                        c.Attempts = 0;
                        FailSend($"Steam rejected a queued message ({DescribeResult(_lastResult)}).");
                        break;
                    }

                    if (++c.Attempts >= FragmentRetryCount)
                    {
                        Log.LogError($"[SteamTransport] Send to {c.Id} failed after {FragmentRetryCount} attempts — dropping {c.Outbox.Count} queued message(s)");
                        c.Outbox.Clear();
                        c.Attempts = 0;
                        FailSend($"Steam send buffer stayed full for this player ({DescribeResult(_lastResult)}).");
                        break;
                    }

                    int delay = Math.Min(FragmentRetryBaseDelayMs * (1 << (c.Attempts - 1)),
                                         FragmentRetryMaxDelayMs);
                    c.RetryAtMs = now + delay;
                    break;
                }
            }
        }

        private unsafe bool SendRaw(HSteamNetConnection conn, byte[] data, int offset, int length, TransportDelivery delivery)
        {
            if (conn == HSteamNetConnection.Invalid)
            {
                _lastResult = EResult.k_EResultNoConnection;
                return false;
            }

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
                    conn, (IntPtr)(ptr + offset), (uint)length, flags, out _);
                _lastResult = result;
                if (result != EResult.k_EResultOK)
                {
                    // Backpressure is expected and handled by the outbox - don't shout.
                    if (result != EResult.k_EResultLimitExceeded)
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

        // ── Receiving ────────────────────────────────────────────────────────

        private void ReceiveMessages(PeerId from, HSteamNetConnection conn)
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
                    HandleFragment(from, data, length);
                }
                else
                {
                    OnDataReceived?.Invoke(from, data, length);
                }
            }
        }

        private void HandleFragment(PeerId from, byte[] data, int length)
        {
            uint fragmentId = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
            int chunkIndex  = data[5] | (data[6] << 8);
            int totalChunks = data[7] | (data[8] << 8);

            if (totalChunks <= 0 || chunkIndex < 0 || chunkIndex >= totalChunks)
            {
                Log.LogWarning($"[SteamTransport] Invalid fragment header from {from}: id={fragmentId} chunk={chunkIndex}/{totalChunks}");
                return;
            }

            var key = (from, fragmentId);
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
                Log.LogInfo($"[SteamTransport] Reassembled fragment from {from} id={fragmentId}: {totalChunks} chunks → {buffer.TotalLength} bytes");
                OnDataReceived?.Invoke(from, reassembled, buffer.TotalLength);
            }
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
            List<(PeerId, uint)>? staleIds = null;

            foreach (var kvp in _pendingFragments)
            {
                if (now - kvp.Value.LastChunkTicks > idleThreshold)
                {
                    staleIds ??= new List<(PeerId, uint)>();
                    staleIds.Add(kvp.Key);
                }
            }

            if (staleIds == null) return;

            foreach (var key in staleIds)
            {
                var buf = _pendingFragments[key];
                _pendingFragments.Remove(key);

                int idleSec  = (int)((now - buf.LastChunkTicks) / TimeSpan.TicksPerSecond);
                int totalSec = (int)((now - buf.CreatedTicks)   / TimeSpan.TicksPerSecond);
                int gotKb    = buf.TotalLength / 1024;

                Log.LogError($"[SteamTransport] Fragment from {key.Item1} id={key.Item2} stalled: {buf.ReceivedCount}/{buf.Chunks.Length} chunks " +
                             $"({gotKb} KB) after {totalSec}s, no data for {idleSec}s — discarding");

                // The sender got an OK from Steam and will never resend, so this
                // message is simply gone. Say so rather than leaving the player
                // staring at a screen that never loads.
                OnReceiveFailed?.Invoke(key.Item1,
                    $"A large transfer stopped {idleSec}s short of completing " +
                    $"({buf.ReceivedCount} of {buf.Chunks.Length} parts, {gotKb} KB received).");
            }
        }

        private static int PingOf(HSteamNetConnection conn)
        {
            if (conn == HSteamNetConnection.Invalid) return 0;
            SteamNetConnectionRealTimeStatus_t status = default;
            SteamNetConnectionRealTimeLaneStatus_t laneStatus = default;
            var result = SteamNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref laneStatus);
            return result == EResult.k_EResultOK ? status.m_nPing : 0;
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t callback)
        {
            var conn = callback.m_hConn;
            var info = callback.m_info;
            var oldState = callback.m_eOldState;
            var remote = info.m_identityRemote.GetSteamID();

            Log.LogInfo($"[SteamTransport] Connection status: {oldState} -> {info.m_eState} (peer={remote})");

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
                        if (_byHandle.ContainsKey(conn)) break;   // duplicate callback
                        var c = new Conn
                        {
                            Handle = conn,
                            Id = new PeerId(++_nextPeerId),
                            // The identity was already here and was only ever logged.
                            // It is the persona-name source for the player roster.
                            Steam = remote,
                        };
                        _byHandle[conn] = c;
                        _byId[c.Id] = c;
                        _connected.Add(c.Id);
                        Log.LogInfo($"[SteamTransport] Client connected as {c.Id} steam={remote} ({_byId.Count} peers)");
                        OnPeerConnected?.Invoke(c.Id);
                    }
                    else
                    {
                        _connectionToHost = conn;
                        if (_hostConn == null) _hostConn = new Conn { Id = PeerId.Server, Steam = remote };
                        _hostConn.Handle = conn;
                        _hostConn.Steam = remote;
                        Log.LogInfo("[SteamTransport] Connected to host");
                        OnPeerConnected?.Invoke(PeerId.Server);
                    }
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    Log.LogInfo($"[SteamTransport] Connection closed: {info.m_szEndDebug}");

                    PeerId closed;
                    if (_isHost)
                    {
                        if (!_byHandle.TryGetValue(conn, out var c))
                        {
                            // Never reached Connected (e.g. refused mid-handshake).
                            SteamNetworkingSockets.CloseConnection(conn, 0, null, false);
                            break;
                        }
                        closed = c.Id;
                        ForgetPeer(c);
                    }
                    else
                    {
                        closed = PeerId.Server;
                        _connectionToHost = HSteamNetConnection.Invalid;
                        _hostConn = null;
                        DropFragmentsOf(PeerId.Server);
                    }

                    SteamNetworkingSockets.CloseConnection(conn, 0, null, false);
                    OnPeerDisconnected?.Invoke(closed);
                    break;
            }
        }
    }
}
