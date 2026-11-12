using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → every established client: the seatable sides of the current mission, in
    /// <see cref="SideTable"/> order (0 = the host's side). A player's team byte is an
    /// index into this. Sent with every roster broadcast and before every session send,
    /// so a guest always holds the table its save is reseated by.
    /// </summary>
    public class SideTableMessage : INetMessage
    {
        public struct Entry
        {
            public string Name;         // stable mission name (Taskforce._nameInMissionFile)
            public string DisplayName;  // localised label
        }

        public List<Entry> Entries = new();

        public MessageType Type => MessageType.SideTable;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)Entries.Count);
            foreach (var e in Entries)
            {
                writer.Put(e.Name ?? "");
                writer.Put(e.DisplayName ?? "");
            }
        }

        public static SideTableMessage Deserialize(NetDataReader reader)
        {
            var msg = new SideTableMessage();
            int count = reader.GetByte();
            for (int i = 0; i < count; i++)
            {
                msg.Entries.Add(new Entry
                {
                    Name        = reader.GetString(),
                    DisplayName = reader.GetString(),
                });
            }
            return msg;
        }
    }

    /// <summary>
    /// Host → every established client: one side pair's relationship changed during
    /// play (a mission trigger's SetRelations, or the AI scripting runtime). Relations
    /// at load time ride in the save, which writes every pair; only runtime changes
    /// need this, and they only ever happen on the host because client trigger
    /// evaluation is gated.
    /// </summary>
    public class SideRelationMessage : INetMessage
    {
        public string SideA = "";
        public string SideB = "";
        public byte   State;          // SeaPower.RelationsState
        public bool   SensorSharing;

        public MessageType Type => MessageType.SideRelation;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(SideA ?? "");
            writer.Put(SideB ?? "");
            writer.Put(State);
            writer.Put(SensorSharing);
        }

        public static SideRelationMessage Deserialize(NetDataReader reader) => new SideRelationMessage
        {
            SideA         = reader.GetString(),
            SideB         = reader.GetString(),
            State         = reader.GetByte(),
            SensorSharing = reader.GetBool(),
        };
    }
}
