using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public enum DespawnCause : byte
    {
        Impact      = 0, // hit something (ImpactEvent already carried the VFX)
        Intercepted = 1,
        FuelExpired = 2,
        Splashed    = 3, // hit the water
        Landed      = 4,
        Stored      = 5,
        Expired     = 6,
        Removed     = 7, // generic/scripted removal - silent
        Departed    = 8, // a unit left the mission (ObjectBase.despawnObject: trigger, departing carrier)
    }

    /// <summary>Host → client, ReliableOrdered: entity left the world.</summary>
    public class EntityDespawnMessage : INetMessage
    {
        public int          EntityId;
        public DespawnCause Cause;
        public double LonDeg, LatDeg;
        public float  HeightM;

        public MessageType Type => MessageType.EntityDespawn;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(EntityId);
            writer.Put((byte)Cause);
            writer.Put(LonDeg);
            writer.Put(LatDeg);
            writer.Put(HeightM);
        }

        public static EntityDespawnMessage Deserialize(NetDataReader reader) => new()
        {
            EntityId = reader.GetInt(),
            Cause    = (DespawnCause)reader.GetByte(),
            LonDeg   = reader.GetDouble(),
            LatDeg   = reader.GetDouble(),
            HeightM  = reader.GetFloat(),
        };
    }

    /// <summary>
    /// Host → client, ReliableOrdered: a weapon detonated. The client snaps the
    /// replica to the impact point and plays the game's own context-correct
    /// destruction effects (no damage - DamageState carries that authoritatively).
    /// </summary>
    public class ImpactEventMessage : INetMessage
    {
        public int    WeaponId;
        public int    HitUnitId;      // 0 = terrain/water/airburst
        public double LonDeg, LatDeg;
        public float  HeightM;
        public ushort HeadingQ;
        public short  PitchQ;

        public MessageType Type => MessageType.ImpactEvent;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(WeaponId);
            writer.Put(HitUnitId);
            writer.Put(LonDeg);
            writer.Put(LatDeg);
            writer.Put(HeightM);
            writer.Put(HeadingQ);
            writer.Put(PitchQ);
        }

        public static ImpactEventMessage Deserialize(NetDataReader reader) => new()
        {
            WeaponId  = reader.GetInt(),
            HitUnitId = reader.GetInt(),
            LonDeg    = reader.GetDouble(),
            LatDeg    = reader.GetDouble(),
            HeightM   = reader.GetFloat(),
            HeadingQ  = reader.GetUShort(),
            PitchQ    = reader.GetShort(),
        };
    }

    /// <summary>
    /// Host → client, ReliableOrdered: a unit was destroyed (instant kill or
    /// sinking started). Reliable counterpart of the state-stream flags.
    /// </summary>
    public class DestroyEventMessage : INetMessage
    {
        public int  UnitId;
        public byte Mode;            // 0 = instant kill, 1 = start sinking
        public int  KillerWeaponId;
        public int  KillerUnitId;

        public const byte ModeInstantKill = 0;
        public const byte ModeStartSinking = 1;

        public MessageType Type => MessageType.DestroyEvent;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(UnitId);
            writer.Put(Mode);
            writer.Put(KillerWeaponId);
            writer.Put(KillerUnitId);
        }

        public static DestroyEventMessage Deserialize(NetDataReader reader) => new()
        {
            UnitId         = reader.GetInt(),
            Mode           = reader.GetByte(),
            KillerWeaponId = reader.GetInt(),
            KillerUnitId   = reader.GetInt(),
        };
    }
}
