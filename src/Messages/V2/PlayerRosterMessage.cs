using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → every established client: who is in the session, on which team, in which
    /// slot. Broadcast whenever any of that changes (join, leave, team change, a persona
    /// name resolving, a readiness flip).
    ///
    /// Carries no "your slot" field on purpose - a broadcast cannot say anything
    /// per-recipient. A guest learns its own identity from its unicast Welcome and reads
    /// this only for everyone else.
    /// </summary>
    public class PlayerRosterMessage : INetMessage
    {
        public const byte FlagConnected   = 1;
        public const byte FlagEstablished = 2;
        public const byte FlagReady       = 4;

        public struct Entry
        {
            public byte   Slot;
            public byte   Team;
            public byte   Flags;
            public ulong  SteamId;
            public string Name;
        }

        public List<Entry> Entries = new();

        public MessageType Type => MessageType.PlayerRoster;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)Entries.Count);
            foreach (var e in Entries)
            {
                writer.Put(e.Slot);
                writer.Put(e.Team);
                writer.Put(e.Flags);
                writer.Put(e.SteamId);
                writer.Put(e.Name ?? "");
            }
        }

        public static PlayerRosterMessage Deserialize(NetDataReader reader)
        {
            var msg = new PlayerRosterMessage();
            int count = reader.GetByte();
            for (int i = 0; i < count; i++)
            {
                msg.Entries.Add(new Entry
                {
                    Slot    = reader.GetByte(),
                    Team    = reader.GetByte(),
                    Flags   = reader.GetByte(),
                    SteamId = reader.GetULong(),
                    Name    = reader.GetString(),
                });
            }
            return msg;
        }
    }
}
