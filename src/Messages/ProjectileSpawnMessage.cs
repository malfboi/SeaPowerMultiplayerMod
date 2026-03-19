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

        public void Serialize(NetDataWriter w)
        {
            w.Put(HostProjectileId);
            w.Put(SourceUnitId);
            w.Put(AmmoName);
        }

        public static ProjectileSpawnMessage Deserialize(NetDataReader r) => new ProjectileSpawnMessage
        {
            HostProjectileId = r.GetInt(),
            SourceUnitId     = r.GetInt(),
            AmmoName         = r.GetString(),
        };
    }
}
