using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public enum OrderType : byte
    {
        SetSpeed     = 0,
        SetHeading   = 1,
        MoveTo       = 2,   // absolute world position
        FireWeapon   = 3,   // EngageTask
        Stop            = 4,
        ClearOrders     = 5,
        SetDepth        = 6,
        CeaseFire       = 7,
        SetWeaponStatus = 8,
        SetEMCON        = 9,
        SensorToggle    = 10,  // Speed=1/0 enable/disable, Heading=group (0=AirSearch, 1=SurfaceSearch, 2=ActiveSonar)
        SubmarineMast   = 11,  // Heading=mast (0=Snorkel, 1=Periscope, 2=Radar, 3=ESM)
        RemoveWaypoints = 12,  // Clear all waypoints for a unit
        DeleteWaypoint  = 13,  // Delete waypoint at index (index in Speed field)
        EditWaypoint    = 14,  // Move waypoint at index to new position (index in Speed, pos in DestX/Y/Z)
        AutoFireWeapon  = 15,  // AI auto-attack (host → client), Heading carries priority
        DropSonobuoy   = 16,  // Helicopter sonobuoy drop (PvP sync)
    }

    /// <summary>
    /// A player command. Sent client → host; host validates and applies.
    /// Propagates back to client implicitly via next StateUpdate.
    /// </summary>
    public class PlayerOrderMessage : INetMessage
    {
        public MessageType Type => MessageType.PlayerOrder;

        public int       SourceEntityId;   // unit receiving the order
        public OrderType Order;

        // SetSpeed
        public float Speed;                // telegraph int cast to float (-1..5)

        // SetHeading
        public float Heading;              // absolute degrees

        // MoveTo
        public float DestX, DestY, DestZ; // world position

        // FireWeapon
        public int   TargetEntityId;       // target unit UniqueID (0 = position-based)
        public float TargetX, TargetY, TargetZ; // target position (if no target unit)
        public int   ShotsToFire;
        public string AmmoId = "";         // ammo name string (e.g. "RIM-7_Sea_Sparrow")

        public void Serialize(NetDataWriter w)
        {
            w.Put(SourceEntityId);
            w.Put((byte)Order);
            w.Put(Speed);
            w.Put(Heading);
            w.Put(DestX); w.Put(DestY); w.Put(DestZ);
            w.Put(TargetEntityId);
            w.Put(TargetX); w.Put(TargetY); w.Put(TargetZ);
            w.Put(ShotsToFire);
            w.Put(AmmoId);
        }

        public static PlayerOrderMessage Deserialize(NetDataReader r) => new PlayerOrderMessage
        {
            SourceEntityId  = r.GetInt(),
            Order           = (OrderType)r.GetByte(),
            Speed           = r.GetFloat(),
            Heading         = r.GetFloat(),
            DestX           = r.GetFloat(),
            DestY           = r.GetFloat(),
            DestZ           = r.GetFloat(),
            TargetEntityId  = r.GetInt(),
            TargetX         = r.GetFloat(),
            TargetY         = r.GetFloat(),
            TargetZ         = r.GetFloat(),
            ShotsToFire     = r.GetInt(),
            AmmoId          = r.GetString(),
        };
    }
}
