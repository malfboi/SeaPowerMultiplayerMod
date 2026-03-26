using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public enum MessageType : byte
    {
        StateUpdate,                // host → client: unit positions/states
        PlayerOrder,                // bidirectional: unit commands
        GameEvent,                  // bidirectional: discrete events
        SessionSync,                // host → client: full session state
        SessionReady,               // client → host: "I finished loading"
        CombatEvent,                // host → client: authoritative combat outcome
        DamageState,                // host → client: compartment damage snapshot
        DamageDecal,                // host → client: visual damage decal
        ProjectileSpawn,            // host → client: projectile spawn with host ID
        FlightOps,                  // bidirectional: flight deck operations sync (PvP)
        MissileStateSync,           // bidirectional: missile guidance/position sync (PvP)
        ChaffLaunch,                // bidirectional: manual chaff deployment sync (PvP)
        AircraftRecoveryRequest,    // "I don't have aircraft X, send me its details"
        AircraftRecoveryResponse,   // "Here's how to spawn aircraft X"
        ProjectileReconciliation,   // host → client: periodic active projectile list
    }

    public interface INetMessage
    {
        MessageType Type { get; }
        void Serialize(NetDataWriter writer);
    }
}
