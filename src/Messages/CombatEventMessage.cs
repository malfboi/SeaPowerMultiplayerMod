using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public enum CombatEventType : byte
    {
        ProjectileDestroyed   = 0, // missile/torpedo hit target and detonated
        ProjectileIntercepted = 1, // missile/torpedo intercepted by CIWS or SAM
        UnitDestroyed         = 2, // unit destroyed by combat damage
        MissileImpact         = 3, // PvP: "your missile X hit my unit, destroy it"
    }

    /// <summary>
    /// Host-authoritative combat outcome. Sent host → client via ReliableOrdered.
    /// Client suppresses local combat resolution and applies these instead.
    /// </summary>
    public class CombatEventMessage : INetMessage
    {
        public MessageType Type => MessageType.CombatEvent;

        public CombatEventType EventType;
        public int TargetEntityId;  // the projectile or unit affected
        public int SourceEntityId;  // who caused it (CIWS unit, SAM launcher, etc.)

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)EventType);
            w.Put(TargetEntityId);
            w.Put(SourceEntityId);
        }

        public static CombatEventMessage Deserialize(NetDataReader r) => new CombatEventMessage
        {
            EventType      = (CombatEventType)r.GetByte(),
            TargetEntityId = r.GetInt(),
            SourceEntityId = r.GetInt(),
        };
    }
}
