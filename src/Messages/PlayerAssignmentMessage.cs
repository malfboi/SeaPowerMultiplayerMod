using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public class PlayerAssignmentEntry
    {
        public byte PlayerId;
        public byte TeamSide;
        public int AssignedTfIndex;
        public string DisplayName;
    }

    public class PlayerAssignmentMessage : INetMessage
    {
        public MessageType Type => MessageType.PlayerAssignment;

        public byte YourPlayerId;
        public List<PlayerAssignmentEntry> Entries = new();

        public void Serialize(NetDataWriter w)
        {
            w.Put(YourPlayerId);
            w.Put((byte)Entries.Count);
            foreach (var e in Entries)
            {
                w.Put(e.PlayerId);
                w.Put(e.TeamSide);
                w.Put(e.AssignedTfIndex);
                w.Put(e.DisplayName ?? "");
            }
        }

        public static PlayerAssignmentMessage Deserialize(NetDataReader r)
        {
            var msg = new PlayerAssignmentMessage
            {
                YourPlayerId = r.GetByte(),
            };
            int count = r.GetByte();
            for (int i = 0; i < count; i++)
            {
                msg.Entries.Add(new PlayerAssignmentEntry
                {
                    PlayerId = r.GetByte(),
                    TeamSide = r.GetByte(),
                    AssignedTfIndex = r.GetInt(),
                    DisplayName = r.GetString(),
                });
            }
            return msg;
        }
    }
}
