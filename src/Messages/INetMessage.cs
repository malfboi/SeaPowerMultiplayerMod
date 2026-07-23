using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public enum MessageType : byte
    {
        // Explicit values - gaps are deleted legacy v1 messages (0 StateUpdate,
        // 5 CombatEvent, 8 ProjectileSpawn, 9 FlightOps, 10 MissileStateSync,
        // 11 ChaffLaunch, 12/13 AircraftRecovery, 14 ProjectileReconciliation).
        // Do NOT renumber: wire compatibility.
        PlayerOrder              = 1,   // bidirectional: unit commands
        GameEvent                = 2,   // bidirectional: discrete events
        SessionSync              = 3,   // host → client: full session state
        SessionReady             = 4,   // client → host: "I finished loading"
        DamageState              = 6,   // host → client: compartment damage snapshot
        DamageDecal              = 7,   // host → client: visual damage decal

        // ── v2 protocol (Net2) ───────────────────────────────────────────────
        Hello                    = 15,  // client → host: protocol version + mode handshake
        Welcome                  = 16,  // host → client: handshake verdict + session params
        EntityStateBatch         = 17,  // host → client: 10 Hz quantized unit states (all taskforces)
        EntitySpawn              = 18,  // host → client: entity came into existence (weapons P2+, aircraft P4+)
        EntityDespawn            = 19,  // host → client: entity left the world (cause-coded)
        ImpactEvent              = 20,  // host → client: weapon detonation (VFX, no damage)
        DestroyEvent             = 21,  // host → client: unit destroyed/sinking (reliable)
        GunBurstEvent            = 22,  // host → client: gun/CIWS firing cosmetics (tracers, muzzle)
        AmmoStateEvent           = 23,  // host → client: authoritative magazine counts (UI honesty)
        EntityCensus             = 24,  // host → client: periodic full entity manifest (self-heal)
        CensusDiffRequest        = 25,  // client → host: replay spawns for these missing ids
        DeckState                = 26,  // host → client: carrier-relative deck transform (flight ops mimicry)
        FlightOpsAnim            = 27,  // host → client: aircraft/carrier animation events (gear, elevators, deflectors, steam)
        WeaponHatchEvent         = 28,  // host → client: VLS/torpedo-tube hatch open/close cosmetics
        FlightDeckState          = 29,  // host → client: per-carrier flight-ops queue + availability (UI mirror)
        ViewportHint             = 30,  // client → host: where the client is looking (drives near-unit stream rate)
    }

    public interface INetMessage
    {
        MessageType Type { get; }
        void Serialize(NetDataWriter writer);
    }
}
