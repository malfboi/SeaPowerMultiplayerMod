using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → client: the host's tactical picture for the shared task force -
    /// track number, classified side and identified class, per detected object.
    ///
    /// Sensors run locally on both machines (they are not host-authoritative),
    /// and the plotting table allocates track numbers in DETECTION ORDER
    /// (PlottingTable._maxOwnTrackId/_maxForeignTrackId), so the two players end
    /// up with different numbers for the same contact and with classification
    /// that narrows at different times - one player sees "Kirov", the other an
    /// unknown. This message makes the host's picture the shared one.
    ///
    /// Only units travel, not weapons: in-flight missiles churn the table many
    /// times a second and their track numbers are throwaway.
    /// </summary>
    public class ContactSyncMessage : INetMessage
    {
        /// <summary>Per-contact wire record. ClassName is empty when the host has
        /// not identified the contact.
        ///
        /// Side travels as a bare "is it classified" flag rather than a taskforce
        /// identifier: the client resolves it to the contact's own
        /// <c>BaseObject._taskforce</c>, which is the same object on both machines.
        /// Nothing order- or perspective-dependent goes on the wire, so this stays
        /// correct across the PvP side swap - where the two players disagree about
        /// which taskforce is "the player's" and a list index would not survive.</summary>
        public struct Entry
        {
            public int    UniqueId;
            public int    TrackId;
            public bool   Classified; // false = the host has not worked out whose it is
            public string ClassName;  // object ini name, "" = not identified
        }

        /// <summary>True on the periodic full sweep: the client replaces its whole
        /// override table, which is how contacts the host has since dropped stop
        /// being forced. Incremental packets only carry what changed.</summary>
        public bool IsFull;

        public readonly List<Entry> Entries = new(64);

        public MessageType Type => MessageType.ContactSync;

        public void Reset()
        {
            IsFull = false;
            Entries.Clear();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(IsFull);
            writer.Put((ushort)Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                writer.Put(e.UniqueId);
                writer.Put(e.TrackId);
                writer.Put(e.Classified);
                writer.Put(e.ClassName ?? "");
            }
        }

        public static ContactSyncMessage Deserialize(NetDataReader reader)
        {
            var msg = new ContactSyncMessage { IsFull = reader.GetBool() };
            int count = reader.GetUShort();
            for (int i = 0; i < count; i++)
            {
                msg.Entries.Add(new Entry
                {
                    UniqueId   = reader.GetInt(),
                    TrackId    = reader.GetInt(),
                    Classified = reader.GetBool(),
                    ClassName  = reader.GetString(),
                });
            }
            return msg;
        }
    }
}
