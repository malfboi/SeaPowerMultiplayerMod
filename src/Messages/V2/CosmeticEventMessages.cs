using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public enum GunBurstKind : byte
    {
        GunBurst  = 0,
        CiwsStart = 1,
        CiwsStop  = 2,
    }

    /// <summary>
    /// Host → client, unreliable: a gun mount fired / a CIWS started or stopped
    /// firing. Pure cosmetics - the client replays the mount's own native fire
    /// path (muzzle flash, tracers, audio); damage never happens client-side.
    /// </summary>
    public class GunBurstEventMessage : INetMessage
    {
        public int    ShooterId;
        public short  MountIndex;         // index into unit._obp._weaponSystems
        public GunBurstKind Kind;
        public int    TargetId;           // CIWS aim target (usually a weapon replica)
        public ushort SolutionHeadingQ;   // gun fire solution direction
        public short  SolutionPitchQ;
        public float  ToTargetTime;       // host ballistic solve - Projectile lerps to aim over this
        public double AimLatDeg, AimLonDeg;
        public float  AimHeightM;
        public string AmmoName = "";

        public MessageType Type => MessageType.GunBurstEvent;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ShooterId);
            writer.Put(MountIndex);
            writer.Put((byte)Kind);
            writer.Put(TargetId);
            writer.Put(SolutionHeadingQ);
            writer.Put(SolutionPitchQ);
            writer.Put(ToTargetTime);
            writer.Put(AimLatDeg);
            writer.Put(AimLonDeg);
            writer.Put(AimHeightM);
            writer.Put(AmmoName);
        }

        public static GunBurstEventMessage Deserialize(NetDataReader reader) => new()
        {
            ShooterId        = reader.GetInt(),
            MountIndex       = reader.GetShort(),
            Kind             = (GunBurstKind)reader.GetByte(),
            TargetId         = reader.GetInt(),
            SolutionHeadingQ = reader.GetUShort(),
            SolutionPitchQ   = reader.GetShort(),
            ToTargetTime     = reader.GetFloat(),
            AimLatDeg        = reader.GetDouble(),
            AimLonDeg        = reader.GetDouble(),
            AimHeightM       = reader.GetFloat(),
            AmmoName         = reader.GetString(),
        };
    }

    /// <summary>
    /// Host → client, reliable, throttled: authoritative ammo state for one ammo
    /// type on one unit.
    ///
    /// <see cref="DisplayTotal"/> is the number the player actually reads -
    /// ObjectBase.AmmunitionAmountDictionary, which the weapon panel binds to via
    /// ObserveReplace. It is sent as the host's absolute value rather than being
    /// derived client-side, because the client cannot derive it: the total is the
    /// sum of loaded rounds, seated containers and magazine contents, and the
    /// client's weapon systems never run launch() or its reload, so their loaded
    /// counts and container state stop tracking the host's the moment anything
    /// fires. Mirroring the one number the host shows is the only version of this
    /// that stays correct across launches, reloads and refills.
    ///
    /// <see cref="MagazineCount"/> still carries the magazine separately - the
    /// client's own engage/reload checks read it - and is -1 when the change did
    /// not come from a magazine.
    /// </summary>
    public class AmmoStateEventMessage : INetMessage
    {
        public int    UnitId;
        public string AmmoName = "";
        public int    MagazineCount;   // -1 = not a magazine change, leave it alone
        public int    DisplayTotal;

        public MessageType Type => MessageType.AmmoStateEvent;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(UnitId);
            writer.Put(AmmoName);
            writer.Put(MagazineCount);
            writer.Put(DisplayTotal);
        }

        public static AmmoStateEventMessage Deserialize(NetDataReader reader) => new()
        {
            UnitId        = reader.GetInt(),
            AmmoName      = reader.GetString(),
            MagazineCount = reader.GetInt(),
            DisplayTotal  = reader.GetInt(),
        };
    }
}
