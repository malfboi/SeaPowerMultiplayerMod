using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public enum SpawnKind : byte
    {
        Weapon   = 0, // missiles, torpedoes, sonobuoy bombs (P2)
        Unit     = 1, // aircraft / helicopters (P4)
        Decoy    = 3, // chaff / noisemakers (P5)
    }

    /// <summary>
    /// Host → client, ReliableOrdered: an entity came into existence on the host.
    /// Common header + per-kind payload. Positions are float degrees here - spawn
    /// placement is immediately corrected by the 10 Hz state stream.
    /// </summary>
    public class EntitySpawnMessage : INetMessage
    {
        // ── Common ────────────────────────────────────────────────────────────
        public SpawnKind Kind;
        public int    EntityId;
        public double LonDeg, LatDeg;
        public float  HeightM;
        public ushort HeadingQ;
        public short  PitchQ;
        public ushort SpeedQ;

        // ── Weapon payload ────────────────────────────────────────────────────
        public byte   WeaponClass;       // 0 = Missile, 1 = Torpedo, 2 = Sonobuoy bomb
        public string AmmoName = "";
        public int    ShooterId;         // launch platform UniqueID (0 = unknown)
        public int    TargetId;          // intended target UniqueID (0 = bearing/position shot)
        public double AimLonDeg, AimLatDeg;
        public float  AimHeightM;
        public byte   Flags;             // bit0 = isSubmunition

        public const byte FlagSubmunition = 1;

        // ── Unit payload (aircraft / helicopter) ──────────────────────────────
        public byte   UnitKind;          // UnitType.Aircraft / UnitType.Helicopter
        public string UnitIniName = "";
        public string SquadronRef = "";
        public string LoadoutVariant = "";
        public byte   UnitNumber;
        public int    HomeBaseId;        // carrier/airbase UniqueID (0 = none)
        public byte   TaskforceSide;     // Taskforce.TfType - fallback when home base unresolvable
        public string Nation = "";
        public byte   UnitFlags;         // bit0 = deck phase (on the carrier, DeckState-driven)

        /// <summary>Host's <c>_obp._objectName</c> - the resolved squadron callsign
        /// ("Diamond 3"). FlightDeck assigns it in getObjectToLaunch AFTER
        /// createAircraft returns, so it is empty on the deck-phase spawn and carries
        /// the real value on the wheels-up re-send. Empty = leave the ini default.</summary>
        public string UnitName = "";

        /// <summary>UniqueID of this unit's UnitFormation leader, or 0 for none;
        /// equal to <see cref="EntityId"/> when the unit IS the leader. FlightDeck
        /// .launchVehicle builds the formation after getObjectToLaunch returns, so
        /// like UnitName this only carries a value on the wheels-up re-send.</summary>
        public int FormationLeaderId;

        /// <summary>Unit is in the flight-deck pipeline (parented to the carrier,
        /// _isInFlight=false, colliders off). Re-sent without this flag at
        /// wheels-up - the client flips the existing puppet to airborne.</summary>
        public const byte UnitFlagDeckPhase = 1;

        public MessageType Type => MessageType.EntitySpawn;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)Kind);
            writer.Put(EntityId);
            writer.Put(LonDeg);
            writer.Put(LatDeg);
            writer.Put(HeightM);
            writer.Put(HeadingQ);
            writer.Put(PitchQ);
            writer.Put(SpeedQ);

            if (Kind == SpawnKind.Unit)
            {
                writer.Put(UnitKind);
                writer.Put(UnitIniName);
                writer.Put(SquadronRef);
                writer.Put(LoadoutVariant);
                writer.Put(UnitNumber);
                writer.Put(HomeBaseId);
                writer.Put(TaskforceSide);
                writer.Put(Nation);
                writer.Put(UnitFlags);
                writer.Put(UnitName);
                writer.Put(FormationLeaderId);
            }
            else
            {
                writer.Put(WeaponClass);
                writer.Put(AmmoName);
                writer.Put(ShooterId);
                writer.Put(TargetId);
                writer.Put(AimLonDeg);
                writer.Put(AimLatDeg);
                writer.Put(AimHeightM);
                writer.Put(Flags);
            }
        }

        public static EntitySpawnMessage Deserialize(NetDataReader reader)
        {
            var msg = new EntitySpawnMessage
            {
                Kind     = (SpawnKind)reader.GetByte(),
                EntityId = reader.GetInt(),
                LonDeg   = reader.GetDouble(),
                LatDeg   = reader.GetDouble(),
                HeightM  = reader.GetFloat(),
                HeadingQ = reader.GetUShort(),
                PitchQ   = reader.GetShort(),
                SpeedQ   = reader.GetUShort(),
            };

            if (msg.Kind == SpawnKind.Unit)
            {
                msg.UnitKind       = reader.GetByte();
                msg.UnitIniName    = reader.GetString();
                msg.SquadronRef    = reader.GetString();
                msg.LoadoutVariant = reader.GetString();
                msg.UnitNumber     = reader.GetByte();
                msg.HomeBaseId     = reader.GetInt();
                msg.TaskforceSide  = reader.GetByte();
                msg.Nation         = reader.GetString();
                msg.UnitFlags      = reader.GetByte();
                msg.UnitName       = reader.GetString();
                msg.FormationLeaderId = reader.GetInt();
            }
            else
            {
                msg.WeaponClass = reader.GetByte();
                msg.AmmoName    = reader.GetString();
                msg.ShooterId   = reader.GetInt();
                msg.TargetId    = reader.GetInt();
                msg.AimLonDeg   = reader.GetDouble();
                msg.AimLatDeg   = reader.GetDouble();
                msg.AimHeightM  = reader.GetFloat();
                msg.Flags       = reader.GetByte();
            }
            return msg;
        }
    }
}
