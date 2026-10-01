using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Bidirectional (guests → host, host → the sender's team): Standing Orders
    /// (the game's <c>TacticalDoctrine</c>) for one team's taskforce, its formations
    /// and its units.
    ///
    /// Standing orders are read by the SIMULATION - auto-attack-surface, winchester
    /// RTB rules, AAW salvo size, alignment, stale-engage timeout - and the
    /// simulation only runs on the host. So an order set on a guest's machine has to
    /// reach the host's copy of the same doctrine, and the host's taskforce root
    /// (which is read from the host's own usersettings) has to reach its teammates so
    /// everyone's panel shows what the sim is actually using.
    ///
    /// Addressing: the root by TEAM (resolved through each machine's own save swap),
    /// a formation by its LEADER's unit id, a unit by its own id - formations carry
    /// no id of their own (see FormationOp).
    ///
    /// Small by construction: senders put at most <see cref="MaxEntries"/> entries in
    /// one message, because a reliable packet above the MTU floor arrives corrupted
    /// past the first fragment in the game's Mono runtime (see JamStateMessage).
    /// </summary>
    public class DoctrineSyncMessage : INetMessage
    {
        public const int MaxEntries = 20;

        public enum ScopeKind : byte { Root = 0, Formation = 1, Unit = 2 }

        public struct Entry
        {
            public ScopeKind Scope;
            public int    Id;      // Formation: leader unit id; Unit: unit id; Root: 0
            public string Key;     // TacticalDoctrine property name, or "@<ObjectBase property>"
            public bool   IsSet;   // override active (formation/unit); always true for root
            public int    Value;   // bool as 0/1, enum as its integer value
        }

        public byte Team;          // PlayerRegistry.Team of the taskforce this concerns

        /// <summary>A guest's first full root snapshot of the session. The host adopts
        /// it only if nobody on that team has established the root yet, and otherwise
        /// answers with its own - see DoctrineSyncManager.</summary>
        public bool InitialRoot;

        public readonly List<Entry> Entries = new(MaxEntries);

        public MessageType Type => MessageType.DoctrineSync;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Team);
            writer.Put(InitialRoot);
            writer.Put((byte)Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                writer.Put((byte)e.Scope);
                writer.Put(e.Id);
                writer.Put(e.Key ?? "");
                writer.Put(e.IsSet);
                writer.Put(e.Value);
            }
        }

        public static DoctrineSyncMessage Deserialize(NetDataReader reader)
        {
            var msg = new DoctrineSyncMessage
            {
                Team        = reader.GetByte(),
                InitialRoot = reader.GetBool(),
            };
            int count = reader.GetByte();
            for (int i = 0; i < count; i++)
            {
                msg.Entries.Add(new Entry
                {
                    Scope = (ScopeKind)reader.GetByte(),
                    Id    = reader.GetInt(),
                    Key   = reader.GetString(),
                    IsSet = reader.GetBool(),
                    Value = reader.GetInt(),
                });
            }
            return msg;
        }
    }
}
