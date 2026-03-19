using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public class DamageDecalMessage : INetMessage
    {
        public MessageType Type => MessageType.DamageDecal;

        public int    TargetEntityId;
        public float  LocalX, LocalY, LocalZ;       // position relative to unit
        public float  NormalX, NormalY, NormalZ;     // normal relative to unit
        public string DecalClass;
        public float  Scale;

        public void Serialize(NetDataWriter w)
        {
            w.Put(TargetEntityId);
            w.Put(LocalX); w.Put(LocalY); w.Put(LocalZ);
            w.Put(NormalX); w.Put(NormalY); w.Put(NormalZ);
            w.Put(DecalClass);
            w.Put(Scale);
        }

        public static DamageDecalMessage Deserialize(NetDataReader r) => new DamageDecalMessage
        {
            TargetEntityId = r.GetInt(),
            LocalX  = r.GetFloat(), LocalY  = r.GetFloat(), LocalZ  = r.GetFloat(),
            NormalX = r.GetFloat(), NormalY = r.GetFloat(), NormalZ = r.GetFloat(),
            DecalClass = r.GetString(),
            Scale  = r.GetFloat(),
        };
    }
}
