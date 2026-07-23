using System;

namespace SeapowerMultiplayer.Transport
{
    public enum TransportDelivery { Unreliable, Reliable, ReliableOrdered }

    public interface ITransport
    {
        bool IsConnected { get; }
        int RttMs { get; }
        bool LastSendFailed { get; }

        /// <summary>Cumulative send-side packet counters for the active peer, used
        /// by the overlay's rolling packet-loss indicator. Returns false when the
        /// transport doesn't expose them (Steam) or no peer is connected.</summary>
        bool TryGetPacketStats(out long packetsSent, out long packetsLost);

        /// <summary>Human-readable reason for the most recent send failure, or null if none.</summary>
        string? LastSendError { get; }

        void Start(bool asHost);
        void Stop();
        void Poll();

        /// <summary>Disconnect all connected peers but keep the transport alive
        /// (host keeps listening). Used to refuse incompatible peers.</summary>
        void DisconnectPeers();

        void SendToServer(byte[] data, int length, TransportDelivery delivery);
        void BroadcastToClients(byte[] data, int length, TransportDelivery delivery);

        event Action<byte[], int> OnDataReceived;
        event Action OnPeerConnected;
        event Action OnPeerDisconnected;
    }
}
