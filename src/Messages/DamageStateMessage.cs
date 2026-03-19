using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public class DamageStateMessage : INetMessage
    {
        public MessageType Type => MessageType.DamageState;

        public int  TargetEntityId;
        public byte CompartmentCount;
        public bool IsSinking;

        // Per FloodingCompartment (count * 2 entries: port[0..n], starboard[0..n])
        // Each: _currentIntegrity, _currentFlooding, _currentWaterLevel, _floodingRate
        public float[] FloodingData;   // length = count * 2 * 4

        // Per SystemCompartment (count entries)
        // Each: FireSeverity, _fireGrowRate
        public float[] SystemData;     // length = count * 2

        // Damage control teams (count + 1 entries)
        public int[] DcTeams;

        // Per-system integrity (variable length across compartments)
        public byte   TotalSystemCount;
        public byte[] SystemCountsPerCompartment;  // length = count
        public float[] SystemIntegrities;           // length = TotalSystemCount
        public byte[]  SystemInoperables;           // length = TotalSystemCount (0 or 1)

        public void Serialize(NetDataWriter w)
        {
            w.Put(TargetEntityId);
            w.Put(CompartmentCount);
            w.Put(IsSinking);

            for (int i = 0; i < FloodingData.Length; i++)
                w.Put(FloodingData[i]);

            for (int i = 0; i < SystemData.Length; i++)
                w.Put(SystemData[i]);

            for (int i = 0; i < DcTeams.Length; i++)
                w.Put(DcTeams[i]);

            // System integrity
            w.Put(TotalSystemCount);
            for (int i = 0; i < CompartmentCount; i++)
                w.Put(SystemCountsPerCompartment[i]);
            for (int i = 0; i < TotalSystemCount; i++)
                w.Put(SystemIntegrities[i]);
            for (int i = 0; i < TotalSystemCount; i++)
                w.Put(SystemInoperables[i]);
        }

        public static DamageStateMessage Deserialize(NetDataReader r)
        {
            var msg = new DamageStateMessage();
            msg.TargetEntityId  = r.GetInt();
            msg.CompartmentCount = r.GetByte();
            msg.IsSinking       = r.GetBool();

            int floodLen = msg.CompartmentCount * 2 * 4;
            msg.FloodingData = new float[floodLen];
            for (int i = 0; i < floodLen; i++)
                msg.FloodingData[i] = r.GetFloat();

            int sysLen = msg.CompartmentCount * 2;
            msg.SystemData = new float[sysLen];
            for (int i = 0; i < sysLen; i++)
                msg.SystemData[i] = r.GetFloat();

            int dcLen = msg.CompartmentCount + 1;
            msg.DcTeams = new int[dcLen];
            for (int i = 0; i < dcLen; i++)
                msg.DcTeams[i] = r.GetInt();

            // System integrity
            msg.TotalSystemCount = r.GetByte();
            msg.SystemCountsPerCompartment = new byte[msg.CompartmentCount];
            for (int i = 0; i < msg.CompartmentCount; i++)
                msg.SystemCountsPerCompartment[i] = r.GetByte();
            msg.SystemIntegrities = new float[msg.TotalSystemCount];
            for (int i = 0; i < msg.TotalSystemCount; i++)
                msg.SystemIntegrities[i] = r.GetFloat();
            msg.SystemInoperables = new byte[msg.TotalSystemCount];
            for (int i = 0; i < msg.TotalSystemCount; i++)
                msg.SystemInoperables[i] = r.GetByte();

            return msg;
        }
    }
}
