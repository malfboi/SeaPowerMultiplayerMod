using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>Host → clients, ReliableOrdered: a mission trigger fired on the host.
    /// The client plays out the trigger's presentation (messages, intel, timers,
    /// objectives...) - see TriggerSync.</summary>
    public class TriggerFiredMessage : INetMessage
    {
        public string Section = "";   // Trigger._section - stable across machines
        public int    UnitId;         // Trigger._currentUnit (message text names it), 0 = none

        public MessageType Type => MessageType.TriggerFired;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Section ?? "");
            writer.Put(UnitId);
        }

        public static TriggerFiredMessage Deserialize(NetDataReader reader) => new TriggerFiredMessage
        {
            Section = reader.GetString(),
            UnitId  = reader.GetInt(),
        };
    }

    /// <summary>Client → host, ReliableOrdered: the player chose a trigger to fire (a
    /// mission free-content window's button). The host fires it for real.</summary>
    public class TriggerRequestMessage : INetMessage
    {
        public string Section = "";

        public MessageType Type => MessageType.TriggerRequest;

        public void Serialize(NetDataWriter writer) => writer.Put(Section ?? "");

        public static TriggerRequestMessage Deserialize(NetDataReader reader) => new TriggerRequestMessage
        {
            Section = reader.GetString(),
        };
    }
}
