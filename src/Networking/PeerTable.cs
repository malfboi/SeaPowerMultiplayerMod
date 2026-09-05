using System.Collections.Generic;

namespace SeapowerMultiplayer.Transport
{
    /// <summary>
    /// Two-way map between a transport's native connection handle and the stable
    /// peer id the rest of the mod addresses it by. Both transports keep one, so
    /// the "which client is this?" question is answered the same way on each.
    ///
    /// Ids are monotonic and NEVER reused inside one transport instance. That
    /// matters more than it looks: LiteNetLib recycles its own NetPeer.Id as soon
    /// as a slot frees up, so a message or timer still holding the id of a peer
    /// that just left would silently start addressing whoever joined next. A
    /// counter that only ever climbs turns that into a lookup miss, which is a
    /// no-op, instead of a delivery to the wrong player.
    /// </summary>
    internal sealed class PeerTable<TConn> where TConn : notnull
    {
        private readonly Dictionary<TConn, int> _idOf = new();
        private readonly Dictionary<int, TConn> _connOf = new();
        private readonly List<int> _ids = new();
        private int _nextId = PeerId.FirstClient;

        /// <summary>Connected peer ids, in the order they joined.</summary>
        public IReadOnlyList<int> Ids => _ids;

        public int Count => _ids.Count;

        /// <summary>Register a connection and return its new id. Re-registering a
        /// connection already in the table returns the id it already has rather
        /// than issuing a second one.</summary>
        public int Add(TConn conn)
        {
            if (_idOf.TryGetValue(conn, out int existing)) return existing;

            int id = _nextId++;
            _idOf[conn] = id;
            _connOf[id] = conn;
            _ids.Add(id);
            return id;
        }

        /// <summary>Drop a connection and return the id it held, or
        /// <see cref="PeerId.None"/> if it was not registered.</summary>
        public int Remove(TConn conn)
        {
            if (!_idOf.TryGetValue(conn, out int id)) return PeerId.None;
            _idOf.Remove(conn);
            _connOf.Remove(id);
            _ids.Remove(id);
            return id;
        }

        public bool TryGetId(TConn conn, out int id) => _idOf.TryGetValue(conn, out id);

        public bool TryGetConn(int id, out TConn conn) => _connOf.TryGetValue(id, out conn!);

        /// <summary>Id of the longest-connected peer, or <see cref="PeerId.None"/>
        /// when there are none.</summary>
        public int FirstId => _ids.Count > 0 ? _ids[0] : PeerId.None;

        public void Clear()
        {
            _idOf.Clear();
            _connOf.Clear();
            _ids.Clear();
            // _nextId deliberately not reset - see the no-reuse note above.
        }
    }
}
