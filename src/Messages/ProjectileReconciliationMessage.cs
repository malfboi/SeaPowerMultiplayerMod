using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Periodic reconciliation message sent by the host listing all active projectiles.
    /// Allows the client to detect and correct projectile tracking mismatches.
    /// </summary>
    public class ProjectileReconciliationMessage : INetMessage
    {
        public MessageType Type => MessageType.ProjectileReconciliation;

        public struct ActiveProjectile
        {
            public int HostId;
            public int SourceUnitId;
            public float X, Y, Z;
        }

        public List<ActiveProjectile> Projectiles = new(64);

        public void Reset()
        {
            Projectiles.Clear();
        }

        public void Serialize(NetDataWriter w)
        {
            // Note: type byte is written by NetworkManager before calling Serialize()
            w.Put((ushort)Projectiles.Count);
            foreach (var p in Projectiles)
            {
                w.Put(p.HostId);
                w.Put(p.SourceUnitId);
                w.Put(p.X);
                w.Put(p.Y);
                w.Put(p.Z);
            }
        }

        public static ProjectileReconciliationMessage Deserialize(NetDataReader r)
        {
            var msg = new ProjectileReconciliationMessage();
            int count = r.GetUShort();
            for (int i = 0; i < count; i++)
            {
                msg.Projectiles.Add(new ActiveProjectile
                {
                    HostId = r.GetInt(),
                    SourceUnitId = r.GetInt(),
                    X = r.GetFloat(),
                    Y = r.GetFloat(),
                    Z = r.GetFloat(),
                });
            }
            return msg;
        }
    }
}
