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

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)EventType);
            w.Put(SourceEntityId);
            w.Put(TargetEntityId);
            w.Put(Param);
        }

        public static GameEventMessage Deserialize(NetDataReader r) => new GameEventMessage
        {
            EventType      = (GameEventType)r.GetByte(),
            SourceEntityId = r.GetInt(),
            TargetEntityId = r.GetInt(),
            Param          = r.GetFloat(),
        };
    }
}
