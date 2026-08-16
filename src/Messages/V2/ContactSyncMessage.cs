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
        /// <summary>The host has not worked out whose the contact is.</summary>
        public const byte SideUnknown = 0;

        /// <summary>The host resolved the contact to the NEUTRAL taskforce even though
        /// the contact does not belong to it - i.e. a disguised spy ship the host has
        /// not seen through yet, wearing its cover identity.</summary>
        public const byte SideCover = 1;

        /// <summary>The host resolved the contact to its own real taskforce.</summary>
        public const byte SideActual = 2;

        /// <summary>Per-contact wire record. ClassName is empty when the host has
        /// not identified the contact.
        ///
        /// Side travels as a code rather than a taskforce identifier: the client
        /// resolves it against the contact's own <c>BaseObject._taskforce</c> and
        /// <c>Globals._neutralTaskforce</c>, both of which are the same objects on
        /// both machines. Nothing order- or perspective-dependent goes on the wire,
        /// so this stays correct across the PvP side swap - where the two players
        /// disagree about which taskforce is "the player's" and a list index would
        /// not survive.
        ///
        /// It is a code and not a bool because "classified" and "classified as its
        /// real side" are different answers for a disguised spy unit
        /// (<c>ObjectBaseParameters._disguisedAs</c> + the Spy role). The game
        /// resolves those to the NEUTRAL taskforce until a sensor gets inside truth
        /// distance, so a bare flag read as "host knows" and the client then filled
        /// in the real taskforce - revealing a disguise the host had not seen
        /// through, and, worse, disagreeing with the client's own sensors on every
        /// plotting-table tick.</summary>
        public struct Entry
        {
            public int    UniqueId;
            public int    TrackId;
            public byte   Side;       // SideUnknown / SideCover / SideActual
            public string ClassName;  // object ini name, "" = not identified

            /// <summary>The contact's AI.Compliance, so both players share ONE answer
            /// to "identify yourself". The game rolls it lazily per machine and caches
            /// it, so each player was getting an independent 60% roll off the same
            /// merchant - ask on both screens and the odds of a hit went from 60% to
            /// 84%. 0 (Unknown) = the host has no answer yet, keep the local one.</summary>
            public byte   Compliance;
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
                writer.Put(e.Side);
                writer.Put(e.ClassName ?? "");
                writer.Put(e.Compliance);
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
                    Side       = reader.GetByte(),
                    ClassName  = reader.GetString(),
                    Compliance = reader.GetByte(),
                });
            }
            return msg;
        }
    }
}
