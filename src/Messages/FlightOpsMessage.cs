using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public enum FlightOpsType : byte
    {
        Launch       = 0,  // createLaunchTask — notification that enemy is preparing aircraft
        SpawnId      = 1,  // (legacy, kept for compat) aircraft spawned via launchVehicle
        SpawnVehicle = 2,  // authoritative launchVehicle fired — remote calls launchVehicle directly
    }

    /// <summary>
    /// Bidirectional flight ops sync for PvP mode.
    /// Launch: sent when a player creates a launch task on their own carrier.
    ///   Remote side logs a notification (no gameplay effect).
    /// SpawnVehicle: sent when launchVehicle fires on the authoritative side.
    ///   Remote side calls launchVehicle directly, skipping PendingLaunchTask,
    ///   then runs the LaunchTask pipeline (elevator→taxi→takeoff) in parallel.
    /// </summary>
    public class FlightOpsMessage : INetMessage
    {
        public MessageType Type => MessageType.FlightOps;

        public FlightOpsType OpsType;
        public int  VesselId;        // which carrier (UniqueID)
        public int  VehicleIndex;    // index into _vehiclesOnBoard
        public int  LoadoutIndex;    // index into Loadouts
        public int  SquadronIndex;   // index into Squadrons
        public int  CallsignIndex;   // index into Callsigns
        public int  LaunchCount;     // LaunchTaskParameters._launchCount
        public byte MissionType;     // FlightDeckTask.MissionType enum
        public bool  AllowLaunch;      // immediate launch vs ready-only
        public float ReadyUpDuration;  // authoritative _duration (total ready-up time)
        public float HostTimingsMultiplier; // Globals._multipliers[FlightDeckTimingsMode] on sender
        public int   SpawnedUnitId;    // for SpawnVehicle/SpawnId: the aircraft's safe UniqueID
        public int   ElevatorIndex;    // for SpawnVehicle: which elevator was used
        public bool  IsMultipleLaunch; // for SpawnVehicle: formation launch flag

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)OpsType);
            w.Put(VesselId);

            switch (OpsType)
            {
                case FlightOpsType.Launch:
                    w.Put(VehicleIndex);
                    w.Put(LoadoutIndex);
                    w.Put(SquadronIndex);
                    w.Put(CallsignIndex);
                    w.Put(LaunchCount);
                    w.Put(MissionType);
                    w.Put(AllowLaunch);
                    w.Put(ReadyUpDuration);
                    w.Put(HostTimingsMultiplier);
                    break;

                case FlightOpsType.SpawnId:
                    w.Put(SpawnedUnitId);
                    break;

                case FlightOpsType.SpawnVehicle:
                    w.Put(VehicleIndex);
                    w.Put(LoadoutIndex);
                    w.Put(SquadronIndex);
                    w.Put(CallsignIndex);
                    w.Put(MissionType);
                    w.Put(SpawnedUnitId);
                    w.Put(ElevatorIndex);
                    w.Put(IsMultipleLaunch);
                    break;
            }
        }

        public static FlightOpsMessage Deserialize(NetDataReader r)
        {
            var msg = new FlightOpsMessage
            {
                OpsType  = (FlightOpsType)r.GetByte(),
                VesselId = r.GetInt(),
            };

            switch (msg.OpsType)
            {
                case FlightOpsType.Launch:
                    msg.VehicleIndex  = r.GetInt();
                    msg.LoadoutIndex  = r.GetInt();
                    msg.SquadronIndex = r.GetInt();
                    msg.CallsignIndex = r.GetInt();
                    msg.LaunchCount   = r.GetInt();
                    msg.MissionType      = r.GetByte();
                    msg.AllowLaunch      = r.GetBool();
                    msg.ReadyUpDuration  = r.GetFloat();
                    msg.HostTimingsMultiplier = r.GetFloat();
                    break;

                case FlightOpsType.SpawnId:
                    msg.SpawnedUnitId = r.GetInt();
                    break;

                case FlightOpsType.SpawnVehicle:
                    msg.VehicleIndex     = r.GetInt();
                    msg.LoadoutIndex     = r.GetInt();
                    msg.SquadronIndex    = r.GetInt();
                    msg.CallsignIndex    = r.GetInt();
                    msg.MissionType      = r.GetByte();
                    msg.SpawnedUnitId    = r.GetInt();
                    msg.ElevatorIndex    = r.GetInt();
                    msg.IsMultipleLaunch = r.GetBool();
                    break;
            }

            return msg;
        }
    }
}
