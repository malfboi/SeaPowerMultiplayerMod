using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Bidirectional: the map drawing layer (markers, relative markers, rulers,
    /// circles, polygons, text) as the game's own `[DrawingLayer]` ini key/value
    /// pairs - the exact format SaveLoadManager writes and reads, so both ends
    /// serialize and rebuild with the game's code rather than a parallel one.
    ///
    /// Drawings are player annotations, not simulation state: either side may
    /// place them, so whoever changed theirs sends the whole layer and the other
    /// side replaces its own. With two players and hand-placed markers the
    /// last-writer-wins race is not worth a merge protocol.
    /// </summary>
    public class DrawingSyncMessage : INetMessage
    {
        public readonly List<(string key, string value)> Entries = new(32);

        public MessageType Type => MessageType.DrawingSync;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((ushort)Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                writer.Put(Entries[i].key);
                writer.Put(Entries[i].value);
            }
        }

        public static DrawingSyncMessage Deserialize(NetDataReader reader)
        {
            var msg = new DrawingSyncMessage();
            int count = reader.GetUShort();
            for (int i = 0; i < count; i++)
                msg.Entries.Add((reader.GetString(), reader.GetString()));
            return msg;
        }
    }
}
