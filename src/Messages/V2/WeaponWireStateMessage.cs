using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → client, ReliableOrdered: wire-guided torpedo guidance state changed.
    ///
    /// The guest's tactical map draws the target line / aim indicator from the
    /// weapon's LOCAL fields (AimPointGeoPosition, InitialTargetGeoPosition,
    /// CurrentIntendedTargetObject, _onWire, ConnectionLost). The guest's wire
    /// command forward patches return false so those fields never update locally;
    /// only the host's torpedo actually changes. This message carries the new
    /// guidance state so the guest's map follows the host's authoritative torpedo.
    ///
    /// Sent only on retarget/cut/speed/depth events (not per-frame) - these fields
    /// change rarely, so a dedicated reliable event is cheaper than adding them to
    /// the EntityStateBatchMessage stream.
    /// </summary>
    public class WeaponWireStateMessage : INetMessage
    {
        public int WeaponId;
        public int TargetEntityId;   // CurrentIntendedTargetObject?.UniqueID ?? 0
        public double AimLonDeg, AimLatDeg;
        public float  AimHeight;
        public double InitLonDeg, InitLatDeg;
        public float  InitHeight;
        public byte   Flags;         // bit0=_onWire, bit1=ConnectionLost, bit2=ConnectionLostForever
        public byte   WireSpeedIndex; // 255 = unchanged
        public float  WireDepthFeet;

        public const byte FlagOnWire = 1;
        public const byte FlagConnectionLost = 2;
        public const byte FlagConnectionLostForever = 4;
        public const byte WireSpeedUnchanged = 255;

        public MessageType Type => MessageType.WeaponWireState;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(WeaponId);
            writer.Put(TargetEntityId);
            writer.Put(AimLonDeg);
            writer.Put(AimLatDeg);
            writer.Put(AimHeight);
            writer.Put(InitLonDeg);
            writer.Put(InitLatDeg);
            writer.Put(InitHeight);
            writer.Put(Flags);
            writer.Put(WireSpeedIndex);
            writer.Put(WireDepthFeet);
        }

        public static WeaponWireStateMessage Deserialize(NetDataReader reader) => new()
        {
            WeaponId        = reader.GetInt(),
            TargetEntityId  = reader.GetInt(),
            AimLonDeg       = reader.GetDouble(),
            AimLatDeg       = reader.GetDouble(),
            AimHeight       = reader.GetFloat(),
            InitLonDeg      = reader.GetDouble(),
            InitLatDeg      = reader.GetDouble(),
            InitHeight      = reader.GetFloat(),
            Flags           = reader.GetByte(),
            WireSpeedIndex  = reader.GetByte(),
            WireDepthFeet   = reader.GetFloat(),
        };
    }
}
