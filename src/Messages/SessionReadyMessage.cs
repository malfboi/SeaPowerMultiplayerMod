using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Sent client → host after the client has finished loading a session.
    /// Host uses this to know both sides are ready before unpausing.
    /// </summary>
    public class SessionReadyMessage : INetMessage
    {
        public MessageType Type => MessageType.SessionReady;

        public bool IsReady = true;

        public void Serialize(NetDataWriter w)
        {
            w.Put(IsReady);
        }

        public static SessionReadyMessage Deserialize(NetDataReader r) => new SessionReadyMessage
        {
            IsReady = r.GetBool(),
        };
    }
}
