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
        /// <summary>Which team the host seated this player on (0=Blue, 1=Red). This is
        /// the field formerly reserved as AssignedTaskforce - same wire position, finally
        /// used. It is what the guest's save swap is decided from, which is why it must
        /// arrive before any SessionSync (BlockedPreHandshake guarantees that).</summary>
        public byte   AssignedTeam;
        /// <summary>Slot 1..3. Per-recipient, so it can only live in this unicast
        /// message - the broadcast roster cannot say "you".</summary>
        public byte   AssignedSlot;
        public int    ClientUidBase;           // this slot's private UID band start
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
            writer.Put(AssignedTeam);
            writer.Put(ClientUidBase);
            writer.Put(StateRateHz);
            writer.Put(GameplayOptions);
            writer.Put(ModFingerprint);
            writer.Put(ModCount);
            writer.Put(AssignedSlot);
        }

        public static WelcomeMessage Deserialize(NetDataReader reader) => new()
        {
            Accepted        = reader.GetBool(),
            RefusalReason   = reader.GetString(),
            AssignedTeam    = reader.GetByte(),
            ClientUidBase   = reader.GetInt(),
            StateRateHz     = reader.GetByte(),
            GameplayOptions = reader.AvailableBytes > 0 ? reader.GetByte() : (byte)0,
            ModFingerprint  = reader.AvailableBytes >= 4 ? reader.GetUInt() : 0u,
            ModCount        = reader.AvailableBytes > 0 ? reader.GetByte() : (byte)0,
            // Appended, so it follows the established trailing-field pattern.
            AssignedSlot    = reader.AvailableBytes > 0 ? reader.GetByte() : (byte)1,
        };
    }
}
