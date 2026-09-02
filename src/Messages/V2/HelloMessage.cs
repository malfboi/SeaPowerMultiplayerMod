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
        /// <summary>Which team the joiner is asking for (0=Blue, 1=Red). A REQUEST, not a
        /// decision: it comes from the lobby's pending-invite key, which two people
        /// accepting the same invite would both read. The host seats them.
        /// Occupies the wire slot the old session-wide IsPvP flag had.</summary>
        public byte   RequestedTeam;
        /// <summary>Sea Power build the client is running (added in protocol 221).</summary>
        public string GameVersion = "";
        /// <summary>The sender's Options → Gameplay settings, packed - see
        /// <see cref="RemoteGameplayOptions"/>. Here rather than in a stream because
        /// they are per-machine facts about the PLAYER, which is exactly what the
        /// handshake is for.</summary>
        public byte GameplayOptions;
        /// <summary>Fingerprint and size of the sender's enabled mod set - see
        /// <see cref="ModSetCheck"/>. A hash rather than the names because the handshake
        /// must stay under the MTU floor.</summary>
        public uint ModFingerprint;
        public byte ModCount;
        /// <summary>Steam persona name, so the host can seat the player under a name the
        /// other players will recognise in the roster and the "send to player" menu.
        /// Empty on LiteNetLib, which has no identity concept - those players show as
        /// their slot number instead.</summary>
        public string DisplayName = "";

        public MessageType Type => MessageType.Hello;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ProtocolVersion);
            writer.Put(PluginVersion);
            writer.Put(RequestedTeam);
            writer.Put(GameVersion);
            writer.Put(GameplayOptions);
            writer.Put(ModFingerprint);
            writer.Put(ModCount);
            writer.Put(DisplayName ?? "");
        }

        // GameVersion is read only if the sender actually wrote it: a pre-221 client
        // still has to parse cleanly here so it reaches the protocol-mismatch refusal
        // and gets told why, instead of throwing and timing out with no explanation.
        public static HelloMessage Deserialize(NetDataReader reader) => new()
        {
            ProtocolVersion = reader.GetUShort(),
            PluginVersion   = reader.GetString(),
            RequestedTeam   = reader.GetByte(),
            GameVersion     = reader.AvailableBytes > 0 ? reader.GetString() : "",
            // Same tolerance as GameVersion above, for the same reason: an older
            // client has to parse cleanly enough to be TOLD its protocol is wrong.
            GameplayOptions = reader.AvailableBytes > 0 ? reader.GetByte() : (byte)0,
            ModFingerprint  = reader.AvailableBytes >= 4 ? reader.GetUInt() : 0u,
            ModCount        = reader.AvailableBytes > 0 ? reader.GetByte() : (byte)0,
            DisplayName     = reader.AvailableBytes > 0 ? reader.GetString() : "",
        };
    }
}
