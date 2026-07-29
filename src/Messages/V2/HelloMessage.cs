using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Client → host, first message after transport connect.
    /// Carries protocol identity + session mode for the host's accept/refuse verdict.
    /// </summary>
    public class HelloMessage : INetMessage
    {
        public ushort ProtocolVersion;
        public string PluginVersion = "";
        public bool   IsPvP;
        /// <summary>Sea Power build the client is running (added in protocol 221).</summary>
        public string GameVersion = "";

        public MessageType Type => MessageType.Hello;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ProtocolVersion);
            writer.Put(PluginVersion);
            writer.Put(IsPvP);
            writer.Put(GameVersion);
        }

        // GameVersion is read only if the sender actually wrote it: a pre-221 client
        // still has to parse cleanly here so it reaches the protocol-mismatch refusal
        // and gets told why, instead of throwing and timing out with no explanation.
        public static HelloMessage Deserialize(NetDataReader reader) => new()
        {
            ProtocolVersion = reader.GetUShort(),
            PluginVersion   = reader.GetString(),
            IsPvP           = reader.GetBool(),
            GameVersion     = reader.AvailableBytes > 0 ? reader.GetString() : "",
        };
    }
}
