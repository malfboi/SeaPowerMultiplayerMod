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
        // 15 was AutoFireWeapon (v1 AI auto-attack replay) - do not reuse
        DropSonobuoy   = 16,  // Helicopter sonobuoy drop (PvP sync)
        SetAltitude     = 17,  // Aircraft/Helicopter preset altitude
        ReturnToBase    = 18,  // Aircraft/Helicopter RTB
        ClassifyContact = 19,  // Radar contact classification (hostile/friendly/neutral)
        ManualGunFire   = 20,  // v2: client gun trigger → host (Heading=mount idx, TargetX/Y/Z=solution dir, AmmoId)
        LaunchAircraft  = 21,  // v2: client carrier launch intent → host (Speed=vehicle, Heading=loadout, DestX=squadron, DestY=callsign, DestZ=count, ShotsToFire=missionType, TargetEntityId=allowLaunch)
        LaunchChaff     = 22,  // v2: client manual chaff → host (clouds replicate back as decoys)
        AbortLaunch     = 23,  // v2: client aborts a pending carrier launch → host (AmmoId = task Guid string)
        LaunchNoisemaker = 24, // v2: client manual noisemaker (Shift+D) → host (decoy replicates back as a spawn)
        SetRudder       = 25,  // manual rudder keys (A/D) → host (Speed = rudder angle, -25..+25)
        AllowLaunch     = 26,  // v2: client releases a readied pending launch → host (AmmoId = task Guid string)
        SetSpeedCustom  = 27,  // speed slider / typed entry (Speed = commanded knots)
        SetHeightCustom = 28,  // depth or altitude slider / typed entry (Speed = DesiredAltitude, Unity units)
        AttackAtWaypoint = 29, // attack/sonobuoy-drop waypoint (Dest=waypoint geo, Target=attack geo,
                               // ShotsToFire=salvo, AmmoId=ammo, Speed=packed salvoType+flags, Heading=areaRadius)
        RequestIdentify  = 30, // co-op: "request: identify yourself" (Source=asking unit, Target=contact)
        SetFormationMode = 31, // UnitFormation.SelectedControlMode (Source=leader unit, Speed=ControlMode)
        FormationCommand = 32, // formation membership/shape/orders - ShotsToFire = FormationOp
        AttackTarget    = 33,  // "attack that contact" designation (TargetEntityId, 0 = clear)
        DropFuelTanks   = 34,  // aircraft/helicopter fuel-tank jettison (Speed = combatDrop flag)
    }

    /// <summary>
    /// Sub-command for <see cref="OrderType.FormationCommand"/>. Formations have no
    /// id of their own, so every op is addressed by a UNIT: the member it acts on, or
    /// the formation's leader for formation-wide orders. Both machines hold the same
    /// membership (replicated at spawn) and apply the ops in the same order, so the
    /// key stays valid even across a leader swap.
    /// </summary>
    public enum FormationOp
    {
        Create     = 0, // Source becomes the leader of a new formation
        Join       = 1, // Source joins the formation led by TargetEntityId
        Detach     = 2, // Source leaves its formation
        SwapLeader = 3, // Source takes over as leader
        CeaseFire  = 4, // formation-wide cease fire      (Speed = recall flag)
        RecallAll  = 5, // all units back to station       (Speed = ceaseFire flag)
        ReturnUnit = 6, // Source back to its own station
        Rename     = 7, // AmmoId = new name
        StationPos = 8, // Speed = station index, DestX/Y/Z = new station position
        Disband    = 9, // formation dissolved
        StationOffset = 10, // Speed = station index, DestX/Y/Z = offset from station,
                            // Heading = flags (1 = setStationHeight, 2 = reachable)
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
