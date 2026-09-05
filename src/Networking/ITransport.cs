using System;
using System.Collections.Generic;

namespace SeapowerMultiplayer.Transport
{
    public enum TransportDelivery { Unreliable, Reliable, ReliableOrdered }

    public interface ITransport
    {
        bool IsConnected { get; }

        /// <summary>Representative round-trip time. On a client that is the link to
        /// the host; on a host it is the WORST of the connected peers, so a single
        /// readout stays meaningful once there is more than one of them. Use
        /// <see cref="RttMsFor"/> when a specific link is meant.</summary>
        int RttMs { get; }

        bool LastSendFailed { get; }

        /// <summary>Ids of the peers currently connected. On a client this is the
        /// single entry <see cref="PeerId.Host"/> while connected, and empty
        /// otherwise.</summary>
        IReadOnlyList<int> ConnectedPeers { get; }

        /// <summary>Round-trip time to one peer, or 0 if it is unknown or gone.</summary>
        int RttMsFor(int peerId);

        /// <summary>Cumulative send-side packet counters for one peer, used by the
        /// overlay's rolling packet-loss indicator. Returns false when the transport
        /// doesn't expose them (Steam) or that peer isn't connected.</summary>
        bool TryGetPacketStats(int peerId, out long packetsSent, out long packetsLost);

        /// <summary>Human-readable reason for the most recent send failure, or null if none.</summary>
        string? LastSendError { get; }

        void Start(bool asHost);
        void Stop();
        void Poll();

        /// <summary>Disconnect all connected peers but keep the transport alive
        /// (host keeps listening). Used to tear a session down.</summary>
        void DisconnectPeers();

        /// <summary>Disconnect one peer and leave the rest of the session alone.
        /// Used to refuse a single incompatible client. A no-op for an id that is
        /// not connected.</summary>
        void DisconnectPeer(int peerId, string reason);

        void SendToServer(byte[] data, int length, TransportDelivery delivery);
        void BroadcastToClients(byte[] data, int length, TransportDelivery delivery);

        /// <summary>Send to one peer. On a host that addresses one client; on a
        /// client the only valid target is <see cref="PeerId.Host"/>. A no-op for
        /// an id that is not connected.</summary>
        void SendToPeer(int peerId, byte[] data, int length, TransportDelivery delivery);

        /// <summary>Payload received, tagged with the peer it came from.</summary>
        event Action<int, byte[], int> OnDataReceived;

        event Action<int> OnPeerConnected;
        event Action<int> OnPeerDisconnected;

        /// <summary>Raised when an inbound message was partially received and then
        /// abandoned, so the peer believes it was delivered and nothing will retry.
        /// The string is a human-readable reason. Transports that reassemble
        /// internally never raise it.</summary>
        event Action<int, string> OnReceiveFailed;
    }
}
