using System.Collections.Generic;
using LiteNetLib.Utils;
using SeaPower;
using SeapowerMultiplayer.Net2;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// One quantized unit-state record. Positions ride as int32 fixed-point 1e-7°
    /// (decoded to LonDeg/LatDeg doubles on receive); angles/speed quantized per
    /// GeoCodec. Wire size: 35 bytes.
    /// </summary>
    public struct EntityState
    {
        public UnitType Kind;
        public int      EntityId;
        public double   LonDeg;        // wire: int32 ×1e7
        public double   LatDeg;        // wire: int32 ×1e7
        public float    HeightM;
        public ushort   HeadingQ;      // GeoCodec.PackHeading
        public short    PitchQ;        // centidegrees
        public short    RollQ;         // centidegrees
        public ushort   SpeedQ;        // knots ×10
        public sbyte    Telegraph;
        public sbyte    RudderQ;       // 0.5° steps
        public float    DesiredAlt;    // raw DesiredAltitude.Value domain (negative = sub depth)
        public short    CmdSpeedQ;     // custom commanded knots ×10, signed; only with FlagCustomSpeed
        public byte     Flags;         // bit0 destroyed, bit1 sinking, bit2 custom speed command
        public byte     Integrity;     // 0.5% steps (IntegrityPercentage ×2)

        public const byte FlagDestroyed   = 1;
        public const byte FlagSinking     = 2;
        /// <summary>Speed is a slider/typed value, not a preset telegraph - read CmdSpeedQ, not Telegraph.</summary>
        public const byte FlagCustomSpeed = 4;
    }

    /// <summary>
    /// v2 host→client unit state batch (10 Hz, unreliable). The host streamer
    /// self-packetizes so a single message never exceeds ~1100 B on the wire -
    /// LiteNetTransport silently upgrades larger unreliable payloads to reliable,
    /// which would head-of-line-block fresh state behind stale retransmits.
    /// </summary>
    public class EntityStateBatchMessage : INetMessage
    {
        public uint  ServerTick;
        public float GameSeconds;
        public readonly List<EntityState> Entries = new(64);

        public MessageType Type => MessageType.EntityStateBatch;

        /// <summary>Serialized size of one entry (wire bytes).</summary>
        public const int EntryWireSize = 35;
        public const int HeaderWireSize = 1 + 4 + 4 + 2; // msgType + tick + gameSeconds + count

        public void Reset()
        {
            ServerTick = 0;
            GameSeconds = 0f;
            Entries.Clear();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ServerTick);
            writer.Put(GameSeconds);
            writer.Put((ushort)Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                writer.Put((byte)e.Kind);
                writer.Put(e.EntityId);
                writer.Put(GeoCodec.PackLatLon(e.LonDeg));
                writer.Put(GeoCodec.PackLatLon(e.LatDeg));
                writer.Put(e.HeightM);
                writer.Put(e.HeadingQ);
                writer.Put(e.PitchQ);
                writer.Put(e.RollQ);
                writer.Put(e.SpeedQ);
                writer.Put(e.Telegraph);
                writer.Put(e.RudderQ);
                writer.Put(e.DesiredAlt);
                writer.Put(e.CmdSpeedQ);
                writer.Put(e.Flags);
                writer.Put(e.Integrity);
            }
        }

        public static EntityStateBatchMessage Deserialize(NetDataReader reader)
        {
            var msg = new EntityStateBatchMessage
            {
                ServerTick  = reader.GetUInt(),
                GameSeconds = reader.GetFloat(),
            };
            int count = reader.GetUShort();
            for (int i = 0; i < count; i++)
            {
                msg.Entries.Add(new EntityState
                {
                    Kind       = (UnitType)reader.GetByte(),
                    EntityId   = reader.GetInt(),
                    LonDeg     = GeoCodec.UnpackLatLon(reader.GetInt()),
                    LatDeg     = GeoCodec.UnpackLatLon(reader.GetInt()),
                    HeightM    = reader.GetFloat(),
                    HeadingQ   = reader.GetUShort(),
                    PitchQ     = reader.GetShort(),
                    RollQ      = reader.GetShort(),
                    SpeedQ     = reader.GetUShort(),
                    Telegraph  = reader.GetSByte(),
                    RudderQ    = reader.GetSByte(),
                    DesiredAlt = reader.GetFloat(),
                    CmdSpeedQ  = reader.GetShort(),
                    Flags      = reader.GetByte(),
                    Integrity  = reader.GetByte(),
                });
            }
            return msg;
        }
    }
}
