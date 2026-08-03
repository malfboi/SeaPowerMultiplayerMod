namespace SeapowerMultiplayer.Net2
{
    /// <summary>
    /// v2 protocol identity. The version participates in the LiteNetLib connect key,
    /// so mismatched plugin builds are refused at the transport level before any
    /// message flows; the Hello/Welcome handshake re-checks it for transports without
    /// key-based accept (Steam) and validates session mode (PvP vs co-op).
    /// Bump ProtocolVersion on every wire-format change (one bump per overhaul phase).
    /// </summary>
    public static class ProtocolInfo
    {
        public const ushort ProtocolVersion = 224; // formation command replication

        /// <summary>
        /// The Sea Power build both players are running. Save files embed indices
        /// into per-vessel data (flight deck elevators, recovery points) that differ
        /// between game builds, so a save synced across mismatched builds throws
        /// deep inside the game's own loader and leaves the client on a dead loading
        /// screen. Compared during the handshake so the pairing is refused instead.
        /// </summary>
        public static string GameVersion => UnityEngine.Application.version ?? "";

        /// <summary>LiteNetLib connection key - versioned so old/new builds cannot pair.</summary>
        public static string ConnectKey => $"{PluginInfo.PLUGIN_GUID}/p{ProtocolVersion}";

        /// <summary>Start of the client-local UID band (sent in Welcome). Host-assigned
        /// ids stay far below this, so client-side spawns can never collide.</summary>
        public const int ClientUidBase = 100_000_000;

        /// <summary>Self-imposed cap for unreliable state packets. LiteNetLib 1.3.5
        /// THROWS TooBigPacketException for Unreliable payloads above
        /// GetMaxSinglePacketSize(), which at the initial MTU of 1024 is 1023 bytes
        /// and only grows if MTU discovery succeeds - so packets must be sized to
        /// the floor. (Verified live: 1100 B batches crashed the host streamer on
        /// every tick, killing all state streaming + census.)</summary>
        public const int MaxStatePacketBytes = 1000;
    }

    public enum HandshakeState : byte
    {
        Disconnected,
        AwaitingHello,    // host: peer connected, waiting for client Hello
        AwaitingWelcome,  // client: Hello sent, waiting for host verdict
        Established,      // handshake complete - gameplay messages flow
        Refused,          // terminal: version/mode mismatch or handshake timeout
    }
}
