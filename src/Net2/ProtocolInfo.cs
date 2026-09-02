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
        // 230 batches every wire change made since 229 shipped: FlightDeckState carries
        // the accountable-ammo pools, UnitStatus carries aircraft fuel and each mount's
        // target, SubmarineMast reinterprets Speed as the desired state rather than a
        // toggle, and Hello/Welcome each carry the sender's packed gameplay options plus
        // its enabled-mod fingerprint. One bump, because none of them had shipped yet -
        // keep adding to this line until it does.
        // 233 is 0.3.7's wire changes and the N-player overhaul landing together. Both
        // branches independently claimed a number (231 here, 232 there), so neither is
        // safe to reuse - a peer that saw the OTHER 231 would pair and then disagree
        // about the Welcome layout. 233 is the first value that means "both".
        //
        // From 0.3.7: Welcome carries the host's "disable F10 debug menu" rule;
        // PlayerOrder's AttackAtWaypoint Speed packing widened to carry the waypoint
        // insertion index; ContactSync's per-contact Classified bool widened into a side
        // CODE (unknown / neutral cover / actual side) so a disguised spy unit's cover
        // identity stops resolving to its real taskforce on the client.
        //
        // From the N-player work: Hello carries a requested TEAM and a display name
        // instead of a PvP flag, Welcome carries the assigned slot/team and a per-slot
        // UID band, and PlayerRoster and UnitOwnership are new. The session-wide
        // PvP/co-op mode is gone - team is a property of each player now, because a 2v1
        // is co-op and PvP at the same time and no single flag could say so.
        // 234: UnitStatus carries each air unit's home base id. The client could never
        // derive one itself (vanilla's SearchForHomeBase runs only from AI.OnFixedUpdate,
        // which is suppressed there), and all three of the game's return-to-base entry
        // points are guarded on _homeBase being non-null - so a guest's RTB order either
        // never left the machine or arrived with no base attached.
        public const ushort ProtocolVersion = 234;

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

        /// <summary>Width of each guest's private UID band.</summary>
        public const int ClientUidBandSize = 100_000_000;

        /// <summary>
        /// Per-slot UID band. Guests used to share one band, which was fine while there
        /// was only ever one of them and a guaranteed collision the moment there were
        /// two: both would floor their allocator to the same number and hand out the same
        /// ids for unrelated local objects.
        ///
        /// Slots 1/2/3 → 100M/200M/300M, comfortably inside int.MaxValue. The wire format
        /// already carried this per-client in Welcome, so nothing else has to change.
        /// </summary>
        public static int UidBaseForSlot(byte slot)
            => slot < 1 ? 0 : ClientUidBase + (slot - 1) * ClientUidBandSize;

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
