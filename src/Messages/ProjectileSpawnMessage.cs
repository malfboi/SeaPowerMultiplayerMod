using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → client: sent immediately when a projectile spawns on the host.
    /// Client uses (SourceUnitId, AmmoName) to match against local projectiles
    /// from the same launcher and ammo type in FIFO order.
    /// </summary>
    public class ProjectileSpawnMessage : INetMessage
    {
        public MessageType Type => MessageType.ProjectileSpawn;

        public int HostProjectileId;  // the projectile's UniqueID on the host
        public int SourceUnitId;      // the unit that launched it
        public string AmmoName = "";  // ammo type name for per-type FIFO matching

        // Target info — used by TryForceSpawn on the receiving side so guided missiles
        // are launched at the correct target rather than at a null/self position.
        // In PvP, X/Z are longitude/latitude (geo coords, floating-origin safe).
        // TargetEntityId uses the sender's local entity ID (receiver looks it up by ID,
        // falling back to FindByHostId for projectile-mapped IDs).
        public int    TargetEntityId;
        public float  TargetX;
        public float  TargetY;
        public float  TargetZ;

        public void Serialize(NetDataWriter w)
        {
            w.Put(HostProjectileId);
            w.Put(SourceUnitId);
            w.Put(AmmoName);
            w.Put(TargetEntityId);
            w.Put(TargetX);
            w.Put(TargetY);
            w.Put(TargetZ);
        }

        public static ProjectileSpawnMessage Deserialize(NetDataReader r) => new ProjectileSpawnMessage
        {
            HostProjectileId = r.GetInt(),
            SourceUnitId     = r.GetInt(),
            AmmoName         = r.GetString(),
            TargetEntityId   = r.GetInt(),
            TargetX          = r.GetFloat(),
            TargetY          = r.GetFloat(),
            TargetZ          = r.GetFloat(),
        };
    }
}
