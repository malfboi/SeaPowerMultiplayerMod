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
            int flags = delivery switch
            {
                TransportDelivery.Unreliable => Constants.k_nSteamNetworkingSend_Unreliable,
                TransportDelivery.Reliable => Constants.k_nSteamNetworkingSend_Reliable
                                            | Constants.k_nSteamNetworkingSend_NoNagle,
                TransportDelivery.ReliableOrdered => Constants.k_nSteamNetworkingSend_Reliable,
                _ => Constants.k_nSteamNetworkingSend_Reliable,
            };

            // Pin the data and send
            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = handle.AddrOfPinnedObject();
                SteamNetworkingSockets.SendMessageToConnection(
                    conn, ptr, (uint)length, flags, out _);
            }
            finally
            {
                handle.Free();
            }
        }

        private void ReceiveMessages(HSteamNetConnection conn)
        {
            int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(conn, _messagePointers, MaxMessages);

            for (int i = 0; i < count; i++)
            {
                var msg = SteamNetworkingMessage_t.FromIntPtr(_messagePointers[i]);
                int length = msg.m_cbSize;
                byte[] data = new byte[length];
                Marshal.Copy(msg.m_pData, data, 0, length);

                SteamNetworkingMessage_t.Release(_messagePointers[i]);

                OnDataReceived?.Invoke(data, length);
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
