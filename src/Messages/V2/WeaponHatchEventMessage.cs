using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → client, ReliableOrdered: a launcher's weapon-container hatch (VLS lid,
    /// torpedo-tube door) started opening or closing. Pure cosmetics - the client
    /// plays the same ini-defined open/close animation on its twin container. Mount
    /// and container indices are stable across machines (both build them from the
    /// same vessel ini), so they resolve to the identical container client-side.
    /// </summary>
    public class WeaponHatchEventMessage : INetMessage
    {
        public int   UnitId;
        public short  MountIndex;    // index into unit._obp._weaponSystems
        public byte   ContainerId;   // index into launcher._containers
        public bool   Open;          // true = open hatches, false = close

        /// <summary>Close only: how long the host waits before the lid actually starts
        /// moving. WeaponSystemLauncher.closeHatches SCHEDULES the close - the engage
        /// path asks for a 3 s delay so the lid stays open behind a launch - so sending
        /// the close at call time made the client shut its hatch the instant the host
        /// finished opening it. From the player's chair the hatch never opened.</summary>
        public float DelaySec;

        /// <summary>True for the launcher's SYSTEM animation rather than one container's
        /// hatch - the outer hull door over a whole launcher bank, which is a separate
        /// animation with its own state machine (BaseSystem._openSystemAnimation, driven
        /// by WeaponSystem.openSystem/closeSystem). On a boat like an Oscar the container
        /// hatch is the inner tube cover and this is the door you actually see move, so
        /// replicating only the former looked like "the internal hatches open but the
        /// external ones don't". ContainerId is unused when this is set.</summary>
        public bool IsSystem;

        public MessageType Type => MessageType.WeaponHatchEvent;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(UnitId);
            writer.Put(MountIndex);
            writer.Put(ContainerId);
            writer.Put(Open);
            writer.Put(DelaySec);
            writer.Put(IsSystem);
        }

        public static WeaponHatchEventMessage Deserialize(NetDataReader reader) => new()
        {
            UnitId      = reader.GetInt(),
            MountIndex  = reader.GetShort(),
            ContainerId = reader.GetByte(),
            Open        = reader.GetBool(),
            DelaySec    = reader.GetFloat(),
            IsSystem    = reader.GetBool(),
        };
    }
}
