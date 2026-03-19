using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public class ChaffLaunchMessage : INetMessage
    {
        public MessageType Type => MessageType.ChaffLaunch;
        public int UnitId;

        public void Serialize(NetDataWriter writer) => writer.Put(UnitId);

        public static ChaffLaunchMessage Deserialize(NetDataReader reader)
            => new() { UnitId = reader.GetInt() };
    }
}
