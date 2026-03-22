using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public class AircraftRecoveryRequestMessage : INetMessage
    {
        public MessageType Type => MessageType.AircraftRecoveryRequest;
        public int MissingAircraftId;

        public void Serialize(NetDataWriter w) => w.Put(MissingAircraftId);

        public static AircraftRecoveryRequestMessage Deserialize(NetDataReader r)
            => new() { MissingAircraftId = r.GetInt() };
    }

    public class AircraftRecoveryResponseMessage : INetMessage
    {
        public MessageType Type => MessageType.AircraftRecoveryResponse;

        public int    AircraftId;
        public int    CarrierVesselId;
        public string VehicleTypeName;
        public int    LoadoutIndex;
        public int    SquadronIndex;
        public int    CallsignIndex;
        public byte   MissionType;
        public bool   IsMultipleLaunch;
        public bool   NotFound;  // true if aircraft doesn't exist or was destroyed

        public void Serialize(NetDataWriter w)
        {
            w.Put(AircraftId);
            w.Put(NotFound);
            if (NotFound) return;

            w.Put(CarrierVesselId);
            w.Put(VehicleTypeName ?? "");
            w.Put(LoadoutIndex);
            w.Put(SquadronIndex);
            w.Put(CallsignIndex);
            w.Put(MissionType);
            w.Put(IsMultipleLaunch);
        }

        public static AircraftRecoveryResponseMessage Deserialize(NetDataReader r)
        {
            var msg = new AircraftRecoveryResponseMessage
            {
                AircraftId = r.GetInt(),
                NotFound   = r.GetBool(),
            };
            if (msg.NotFound) return msg;

            msg.CarrierVesselId = r.GetInt();
            msg.VehicleTypeName = r.GetString();
            msg.LoadoutIndex    = r.GetInt();
            msg.SquadronIndex   = r.GetInt();
            msg.CallsignIndex   = r.GetInt();
            msg.MissionType     = r.GetByte();
            msg.IsMultipleLaunch = r.GetBool();
            return msg;
        }
    }
}
