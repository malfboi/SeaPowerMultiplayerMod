using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Client → host: which foreign contacts the client's plotting table currently
    /// holds. The counterpart to <see cref="ContactSyncMessage"/>, which carries the
    /// host's picture the other way.
    ///
    /// Sensors run locally on both machines, so each side detects things the other
    /// has not - and the host has no other way to learn what its partner can see. A
    /// contact only the client holds is invisible to the host, and because the host
    /// is the authority for track numbers it cannot number one it does not know
    /// about, which is how the two ended up disagreeing about which contact "7010"
    /// is.
    ///
    /// Only object ids travel. What the client thinks the contact IS never does:
    /// classification stays the host's to decide and comes back the other way, so
    /// this cannot push a client misidentification into the shared picture.
    /// </summary>
    public class ContactReportMessage : INetMessage
    {
        public readonly List<int> UniqueIds = new(64);

        public MessageType Type => MessageType.ContactReport;

        public void Reset() => UniqueIds.Clear();

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((ushort)UniqueIds.Count);
            for (int i = 0; i < UniqueIds.Count; i++)
                writer.Put(UniqueIds[i]);
        }

        public static ContactReportMessage Deserialize(NetDataReader reader)
        {
            var msg = new ContactReportMessage();
            int count = reader.GetUShort();
            for (int i = 0; i < count; i++)
                msg.UniqueIds.Add(reader.GetInt());
            return msg;
        }
    }
}
