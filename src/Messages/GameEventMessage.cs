using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public enum GameEventType : byte
    {
        WeaponFired     = 0,
        WeaponImpact    = 1,
        UnitDestroyed   = 2,
        TimeChanged          = 3,   // pause / speed multiplier
        ScenarioLoaded       = 4,
        TaskforceAssigned    = 5,   // host → client, which TfType the client controls
        HardSyncRequest      = 6,   // client → host: request full session resync
        TimeProposal         = 7,   // vote mode: propose a time change to the other side
        TimeProposalResponse = 8,   // vote mode: accept (Param=1) or decline (Param=0)
        UnitSelected         = 9,   // co-op: notify remote player which unit we selected
        UnitDeselected       = 10,  // co-op: notify remote player we deselected our unit
        MissionEnd           = 11,  // v2: host → client, mission ended (host-decided)
        TimeVoteMode         = 12,  // host → client, CfgTimeVote changed mid-session (Param: 1=on, 0=off)
        /// <summary>Client → host: "give this formation to that player".
        /// SourceEntityId = a unit of the formation, Param = target slot.</summary>
        AssignOwnership      = 13,
    }

    /// <summary>
    /// Discrete game events sent bidirectionally on event occurrence.
    /// </summary>
    public class GameEventMessage : INetMessage
    {
        public MessageType Type => MessageType.GameEvent;

        public GameEventType EventType;
        public int           SourceEntityId;
        public int           TargetEntityId;
        public float         Param;          // e.g. time scale, damage amount

        /// <summary>
        /// Which player this originated from.
        ///
        /// Needed because these events are RELAYED: without it, a teammate's unit claim
        /// arrives at the other teammate via the host and there is no longer any
        /// connection to read the sender off - every relayed event would look like it
        /// came from the host.
        ///
        /// Left at <see cref="PlayerRegistry.NoSender"/> by callers and stamped centrally
        /// on the way out, so no send site can forget. The host OVERWRITES it from the
        /// connection on receipt, so what a guest writes here is never trusted.
        /// </summary>
        public byte Slot = PlayerRegistry.NoSender;

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)EventType);
            w.Put(SourceEntityId);
            w.Put(TargetEntityId);
            w.Put(Param);
            w.Put(Slot);
        }

        public static GameEventMessage Deserialize(NetDataReader r) => new GameEventMessage
        {
            EventType      = (GameEventType)r.GetByte(),
            SourceEntityId = r.GetInt(),
            TargetEntityId = r.GetInt(),
            Param          = r.GetFloat(),
            // Appended, so it follows the established trailing-field pattern.
            Slot           = r.AvailableBytes > 0 ? r.GetByte() : PlayerRegistry.NoSender,
        };
    }
}
