using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public class PlayerAssignmentEntry
    {
        public byte PlayerId;
        public byte TeamSide;
        public List<string> AssignedTfNames = new();
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
                w.Put((byte)e.AssignedTfNames.Count);
                foreach (var name in e.AssignedTfNames)
                    w.Put(name ?? "");
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
                var entry = new PlayerAssignmentEntry
                {
                    PlayerId = r.GetByte(),
                    TeamSide = r.GetByte(),
                };
                int tfCount = r.GetByte();
                for (int j = 0; j < tfCount; j++)
                    entry.AssignedTfNames.Add(r.GetString());
                entry.DisplayName = r.GetString();
                msg.Entries.Add(entry);
            }
            return msg;
        }
    }
}
