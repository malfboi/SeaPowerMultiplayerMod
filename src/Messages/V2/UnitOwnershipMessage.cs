using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → clients: who commands which units.
    ///
    /// <see cref="Full"/> replaces the whole table; otherwise the entries are merged, and
    /// a slot of <c>FormationOwnership.Unowned</c> means "released". Sent
    /// ReliableOrdered - a lost delta would leave two machines disagreeing about who may
    /// command a ship, which is exactly the silent divergence the whole design avoids.
    ///
    /// <see cref="LockActive"/> rides along rather than being configured independently on
    /// each machine, so the table and the rule that governs it can never disagree.
    ///
    /// A full table for a 60-unit side is a few hundred bytes; there is no need for
    /// anything cleverer.
    /// </summary>
    public class UnitOwnershipMessage : INetMessage
    {
        public bool Full;
        public bool LockActive;
        public int  Epoch;
        public List<(int UnitId, byte Slot)> Entries = new();

        public MessageType Type => MessageType.UnitOwnership;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Full);
            w.Put(LockActive);
            w.Put(Epoch);
            w.Put((ushort)Entries.Count);
            foreach (var (id, slot) in Entries)
            {
                w.Put(id);
                w.Put(slot);
            }
        }

        public static UnitOwnershipMessage Deserialize(NetDataReader r)
        {
            var msg = new UnitOwnershipMessage
            {
                Full       = r.GetBool(),
                LockActive = r.GetBool(),
                Epoch      = r.GetInt(),
            };
            int count = r.GetUShort();
            for (int i = 0; i < count; i++)
                msg.Entries.Add((r.GetInt(), r.GetByte()));
            return msg;
        }
    }
}
