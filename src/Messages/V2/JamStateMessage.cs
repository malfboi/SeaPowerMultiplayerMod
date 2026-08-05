using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → client: every offensive ECM jam assignment currently in force.
    ///
    /// The companion to <see cref="SensorStateMessage"/>, and needed for the same
    /// reason. That message carries whether a sensor is SWITCHED ON and, for radars,
    /// whether it is radiating - which is enough for a radar, because a radar's
    /// effect follows from those two flags alone. An offensive jammer's effect does
    /// not: <c>SensorSystemECM.OnFixedUpdate</c> only registers itself on anybody's
    /// <c>_ECMSystemsTryingToJamMe</c> once it has been pointed at something, and
    /// <c>RadarCalculator</c>'s jamming branch requires <c>Jams.Value</c>, which is
    /// set from that assignment. So an EA-6B whose pod replicated as "on" still
    /// jammed nothing client-side.
    ///
    /// Nothing on the client can derive the assignment. The player's jam order is
    /// executed by the host (OrderType.JamSystem), and AI jamming - auto-jam plus
    /// the AirStrike jam missions - is decided in the AI class, which is suppressed
    /// client-side entirely.
    ///
    /// Both modes, like the emitter picture and for the same reason: a jammer really
    /// is radiating into the other player's radars, and withholding it does not hide
    /// anything - it just leaves the client's own detection sim wrong.
    ///
    /// STATE, NOT EVENTS: the whole set travels every time, so a client that missed
    /// a packet, joined late, or optimistically ran a jam order the host refused
    /// converges on the next snapshot. Sent on change plus a 10 s heartbeat.
    ///
    /// CHUNKED for the same reason FlightDeckState is: a reliable packet above the
    /// MTU floor arrives corrupted past the first fragment in the game's Mono
    /// runtime. Chunks are ReliableOrdered, so the client accumulates from ChunkIdx
    /// 0 and applies the union when the last one lands.
    /// </summary>
    public class JamStateMessage : INetMessage
    {
        /// <summary>One jam assignment. The system is addressed by its index in
        /// <c>_obp._sensorSystems</c> - the same addressing SensorStateMessage uses,
        /// built from the unit's ini and therefore identical on both machines.</summary>
        public struct Entry
        {
            public int   UnitId;      // the jammer
            public byte  SensorIdx;   // index into _obp._sensorSystems
            public int   TargetId;    // jammed unit, or 0 for a bearing jam
            public float Lon, Lat, Height; // bearing-jam aim point (TargetId == 0)
        }

        public byte ChunkIdx;
        public byte ChunkCount = 1;

        public readonly List<Entry> Entries = new(8);

        public MessageType Type => MessageType.JamState;

        public void Reset()
        {
            ChunkIdx   = 0;
            ChunkCount = 1;
            Entries.Clear();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ChunkIdx);
            writer.Put(ChunkCount);
            writer.Put((ushort)Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                writer.Put(Entries[i].UnitId);
                writer.Put(Entries[i].SensorIdx);
                writer.Put(Entries[i].TargetId);
                writer.Put(Entries[i].Lon);
                writer.Put(Entries[i].Lat);
                writer.Put(Entries[i].Height);
            }
        }

        public static JamStateMessage Deserialize(NetDataReader reader)
        {
            var msg = new JamStateMessage
            {
                ChunkIdx   = reader.GetByte(),
                ChunkCount = reader.GetByte(),
            };
            int count = reader.GetUShort();
            for (int i = 0; i < count; i++)
                msg.Entries.Add(new Entry
                {
                    UnitId    = reader.GetInt(),
                    SensorIdx = reader.GetByte(),
                    TargetId  = reader.GetInt(),
                    Lon       = reader.GetFloat(),
                    Lat       = reader.GetFloat(),
                    Height    = reader.GetFloat(),
                });
            return msg;
        }
    }
}
