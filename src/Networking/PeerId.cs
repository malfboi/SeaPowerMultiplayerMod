namespace SeapowerMultiplayer.Transport
{
    /// <summary>
    /// Well-known peer ids. A peer id is a session-local handle for one end of one
    /// connection; it is assigned by the transport and means nothing to the peer on
    /// the other side of it.
    ///
    /// The host is always <see cref="Host"/> as seen from a client, so client-side
    /// code that used to have nothing to name can now say which link it means
    /// without caring that there is only one.
    /// </summary>
    public static class PeerId
    {
        /// <summary>No peer. Never assigned to a real connection.</summary>
        public const int None = -1;

        /// <summary>The host, as seen from a client. A host never assigns this to
        /// one of its own clients.</summary>
        public const int Host = 0;

        /// <summary>First id a host hands out. Ids climb from here and are never
        /// reused - see <see cref="PeerTable{TConn}"/>.</summary>
        public const int FirstClient = 1;
    }
}
