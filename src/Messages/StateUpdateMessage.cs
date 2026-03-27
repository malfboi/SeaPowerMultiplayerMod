using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public enum UnitType : byte
    {
        Vessel     = 0,
        Submarine  = 1,
        Aircraft   = 2,
        Helicopter = 3,
        Biologic   = 4,
        LandUnit   = 5,
    }

    /// <summary>
    /// Snapshot of all units + in-flight projectiles.
    /// Sent host → client at 10 Hz.
    /// </summary>
    public class StateUpdateMessage : INetMessage
    {
        public MessageType Type => MessageType.StateUpdate;

        public long  Timestamp;
        public float GameSeconds;  // total seconds since midnight (Hour*3600 + Minutes*60 + Seconds)
        public List<UnitState>       Units       = new(64);
        public List<ProjectileState> Projectiles = new(32);
        public int ProjectileCount; // separate from Units.Count for DriftDetector

        public void Reset()
        {
            Timestamp = 0;
            GameSeconds = 0;
            Units.Clear();
            Projectiles.Clear();
            ProjectileCount = 0;
        }

        public void Serialize(NetDataWriter w)
        {
            w.Put(Timestamp);
            w.Put(GameSeconds);

            w.Put((ushort)Units.Count);
            foreach (var u in Units) u.Write(w);

            w.Put((ushort)Projectiles.Count);
            foreach (var p in Projectiles) p.Write(w);
            w.Put((ushort)ProjectileCount);
        }

        public static StateUpdateMessage Deserialize(NetDataReader r)
        {
            var msg = new StateUpdateMessage
            {
                Timestamp   = r.GetLong(),
                GameSeconds = r.GetFloat(),
            };

            int unitCount = r.GetUShort();
            for (int i = 0; i < unitCount; i++)
                msg.Units.Add(UnitState.Read(r));

            int projCount = r.GetUShort();
            for (int i = 0; i < projCount; i++)
                msg.Projectiles.Add(ProjectileState.Read(r));

            msg.ProjectileCount = r.GetUShort();

            return msg;
        }
    }

    public struct UnitState
    {
        public int      EntityId;
        public UnitType Kind;
        public float    X, Y, Z;        // world position (GeoPosition: lon, height, lat)
        public float    Heading;        // degrees (Y euler angle)
        public float    Speed;          // knots
        public bool     IsDestroyed;
        public bool     IsSinking;       // gradual sinking in progress
        public float    RudderAngle;     // commanded rudder angle (-25 to +25)
        public int      Telegraph;       // speed telegraph (-1 to 5)
        public float    DesiredAltitude; // aircraft/helicopter altitude command
        public float    Pitch;           // visual pitch (X euler angle)
        public float    Roll;            // visual roll (Z euler angle)
        public float    IntegrityPercent; // hull integrity for damage visuals

        public void Write(NetDataWriter w)
        {
            w.Put(EntityId);
            w.Put((byte)Kind);
            w.Put(X); w.Put(Y); w.Put(Z);
            w.Put(Heading);
            w.Put(Speed);
            w.Put(IsDestroyed);
            w.Put(IsSinking);
            w.Put(RudderAngle);
            w.Put((sbyte)Telegraph);
            w.Put(DesiredAltitude);
            w.Put(Pitch);
            w.Put(Roll);
            w.Put(IntegrityPercent);
        }

        public static UnitState Read(NetDataReader r) => new UnitState
        {
            EntityId         = r.GetInt(),
            Kind             = (UnitType)r.GetByte(),
            X                = r.GetFloat(),
            Y                = r.GetFloat(),
            Z                = r.GetFloat(),
            Heading          = r.GetFloat(),
            Speed            = r.GetFloat(),
            IsDestroyed      = r.GetBool(),
            IsSinking        = r.GetBool(),
            RudderAngle      = r.GetFloat(),
            Telegraph        = r.GetSByte(),
            DesiredAltitude  = r.GetFloat(),
            Pitch            = r.GetFloat(),
            Roll             = r.GetFloat(),
            IntegrityPercent = r.GetFloat(),
        };
    }

    public struct ProjectileState
    {
        public int   EntityId;
        public byte  Kind;       // 0=missile, 1=torpedo, 2=gun shell
        public float X, Y, Z;   // PvP: GeoPosition (lon, height, lat); Co-op: world coords
        public float Heading;
        public float Speed;      // for puppet extrapolation
        public float Pitch;      // nose angle for visual

        public void Write(NetDataWriter w)
        {
            w.Put(EntityId);
            w.Put(Kind);
            w.Put(X); w.Put(Y); w.Put(Z);
            w.Put(Heading);
            w.Put(Speed);
            w.Put(Pitch);
        }

        public static ProjectileState Read(NetDataReader r) => new ProjectileState
        {
            EntityId = r.GetInt(),
            Kind     = r.GetByte(),
            X        = r.GetFloat(),
            Y        = r.GetFloat(),
            Z        = r.GetFloat(),
            Heading  = r.GetFloat(),
            Speed    = r.GetFloat(),
            Pitch    = r.GetFloat(),
        };
    }
}
