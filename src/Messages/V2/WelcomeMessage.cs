using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → client handshake verdict. On acceptance carries the session parameters
    /// the client needs before any gameplay traffic (mode, UID band, stream rate).
    /// </summary>
    public class WelcomeMessage : INetMessage
    {
        public bool   Accepted;
        public string RefusalReason = "";
        public bool   IsPvP;
        public byte   AssignedTaskforce;       // reserved (used from P1)
        public int    ClientUidBase;           // client-local UID band start (used from P2)
        public byte   StateRateHz;
        /// <summary>The host's Options → Gameplay settings, packed - the return half of
        /// the Hello exchange. See <see cref="RemoteGameplayOptions"/>.</summary>
        public byte   GameplayOptions;
        /// <summary>The host's enabled mod set - see <see cref="ModSetCheck"/>.</summary>
        public uint   ModFingerprint;
        public byte   ModCount;

        public MessageType Type => MessageType.Welcome;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Accepted);
            writer.Put(RefusalReason);
            writer.Put(IsPvP);
            writer.Put(AssignedTaskforce);
            writer.Put(ClientUidBase);
            writer.Put(StateRateHz);
            writer.Put(GameplayOptions);
            writer.Put(ModFingerprint);
            writer.Put(ModCount);
        }

        public static WelcomeMessage Deserialize(NetDataReader reader) => new()
        {
            Accepted          = reader.GetBool(),
            RefusalReason     = reader.GetString(),
            IsPvP             = reader.GetBool(),
            AssignedTaskforce = reader.GetByte(),
            ClientUidBase     = reader.GetInt(),
            StateRateHz       = reader.GetByte(),
            GameplayOptions   = reader.AvailableBytes > 0 ? reader.GetByte() : (byte)0,
            ModFingerprint    = reader.AvailableBytes >= 4 ? reader.GetUInt() : 0u,
            ModCount          = reader.AvailableBytes > 0 ? reader.GetByte() : (byte)0,
        };
    }
}
