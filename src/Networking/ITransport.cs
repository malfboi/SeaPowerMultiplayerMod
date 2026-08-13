using System;
using System.Collections.Generic;

namespace SeapowerMultiplayer.Transport
{
    public enum TransportDelivery { Unreliable, Reliable, ReliableOrdered }

    /// <summary>
    /// Identifies one connection. Allocated MONOTONICALLY by the host and never
    /// reused within a session.
    ///
    /// Deliberately not LiteNetLib's <c>NetPeer.Id</c> or a <c>_clientConnections</c>
    /// index: both are recycled slot numbers, so a packet still in flight when a peer
    /// drops would be attributed to whoever takes that slot next. Ownership, order
    /// relay and per-peer session sync all key off this, so a mis-attributed packet is
    /// a player commanding someone else's fleet - not a cosmetic fault.
    /// </summary>
    public readonly struct PeerId : IEquatable<PeerId>
    {
        public readonly int Raw;
        public PeerId(int raw) { Raw = raw; }

        /// <summary>No peer (default). Never allocated.</summary>
        public static readonly PeerId None = new PeerId(0);

        /// <summary>A guest's view of the host. Guests have exactly one peer, so it
        /// needs no allocation - and a negative value can never collide with a
        /// host-allocated id.</summary>
        public static readonly PeerId Server = new PeerId(-1);

        public bool IsValid => Raw != 0;

        public bool Equals(PeerId other) => Raw == other.Raw;
        public override bool Equals(object? obj) => obj is PeerId p && Equals(p);
        public override int GetHashCode() => Raw;
        public override string ToString() => Raw == -1 ? "server" : $"peer{Raw}";

        public static bool operator ==(PeerId a, PeerId b) => a.Raw == b.Raw;
        public static bool operator !=(PeerId a, PeerId b) => a.Raw != b.Raw;
    }

    public interface ITransport
    {
        bool IsConnected { get; }

        /// <summary>Worst RTT across connected peers, for the overlay's single readout.</summary>
        int RttMs { get; }

        /// <summary>RTT to one peer, or 0 when unknown.</summary>
        int RttMsFor(PeerId peer);

        bool LastSendFailed { get; }

        /// <summary>Human-readable reason for the most recent send failure, or null if none.</summary>
        string? LastSendError { get; }

        /// <summary>Cumulative send-side packet counters for one peer, used by the
        /// overlay's rolling packet-loss indicator. Returns false when the transport
        /// doesn't expose them (Steam) or that peer isn't connected.</summary>
        bool TryGetPacketStats(PeerId peer, out long packetsSent, out long packetsLost);

        /// <summary>Every currently connected peer. Host-side; a guest sees at most
        /// <see cref="PeerId.Server"/>.</summary>
        IReadOnlyList<PeerId> ConnectedPeers { get; }

        /// <summary>SteamID behind a peer, or 0 when the transport has no identity
        /// concept (LiteNetLib). The persona-name source for the player roster.</summary>
        ulong SteamIdOf(PeerId peer);

        void Start(bool asHost);
        void Stop();
        void Poll();

        /// <summary>Disconnect ONE peer, leaving every other connection alive. Used to
        /// refuse an incompatible or surplus joiner without kicking the players already
        /// in the session.</summary>
        void Disconnect(PeerId peer, string reason);

        /// <summary>Disconnect all connected peers but keep the transport alive
        /// (host keeps listening).</summary>
        void DisconnectPeers();

        void SendToServer(byte[] data, int length, TransportDelivery delivery);
        void SendTo(PeerId peer, byte[] data, int length, TransportDelivery delivery);
        void BroadcastToClients(byte[] data, int length, TransportDelivery delivery);

        event Action<PeerId, byte[], int> OnDataReceived;
        event Action<PeerId> OnPeerConnected;
        event Action<PeerId> OnPeerDisconnected;

        /// <summary>Raised when an inbound message was partially received and then
        /// abandoned, so the peer believes it was delivered and nothing will retry.
        /// The string is a human-readable reason. Transports that reassemble
        /// internally never raise it.</summary>
        event Action<PeerId, string> OnReceiveFailed;
    }
}
