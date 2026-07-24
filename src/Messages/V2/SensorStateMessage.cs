using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → client: which sensors are actually emitting, per unit.
    ///
    /// Sensor DETECTION is simulated locally on both machines, but only the host
    /// decides when an AI unit lights up its radar - the client's AI is suppressed
    /// entirely. So a SAM site that brought its tracking radar up on the host stayed
    /// dark on the client: the client's ESM had nothing to hear, produced no contact,
    /// and the player could not engage it with anti-radiation missiles.
    ///
    /// The existing SensorToggle order does not cover this. It is a player action
    /// addressed by GROUP (air search / surface search / active sonar), and
    /// GetRadarGroup deliberately returns -1 for fire-control and targeting radars
    /// because players do not toggle those - which is exactly the sensor that matters
    /// here. This addresses sensors individually instead, by their index in
    /// <c>_obp._sensorSystems</c>, which is built from the unit's ini definition and
    /// is therefore the same list in the same order on both machines.
    ///
    /// State, not events: a missed toggle would otherwise leave a radar wrong until
    /// the next time it happened to change. The periodic full sweep repairs it.
    /// </summary>
    public class SensorStateMessage : INetMessage
    {
        /// <summary>
        /// One unit's sensor picture, as two bitmasks over <c>_sensorSystems</c>.
        ///
        /// <see cref="OnMask"/> is the switch - <c>IsOn</c> - which is what the crew
        /// or the player set. <see cref="EmitMask"/> is whether the thing is actually
        /// RADIATING, and the two are not the same for a fire-control radar: it only
        /// radiates while it holds a target, and target assignment is AI state the
        /// client does not have. Syncing only the switch left client-side FCRs
        /// switched on but silent, which is why a SAM site that had gone active on
        /// the host produced no ESM contact and could not be hit with a HARM.
        ///
        /// <see cref="GuideMask"/> is <c>_isGuiding</c>, carried separately from
        /// EmitMask (<c>Radar._isActive</c>) rather than folded in: the consumer
        /// checks are different. RadarCalculator's first gate accepts either flag,
        /// but its Targeting-type branch demands <c>_isGuiding</c> specifically -
        /// an illuminator "emits" by guiding, and merging the two bits made a
        /// client-side FCR pass the first gate only to be skipped by the second,
        /// which is why ESM heard nothing and a HARM reported NotEmitting even
        /// after _isActive replicated correctly.
        /// </summary>
        public struct Entry
        {
            public int   UniqueId;
            public ulong OnMask;
            public ulong EmitMask;
            public ulong GuideMask;
        }

        /// <summary>True on the periodic sweep. Incremental packets carry only
        /// units whose mask changed.</summary>
        public bool IsFull;

        public readonly List<Entry> Entries = new(64);

        public MessageType Type => MessageType.SensorState;

        public void Reset()
        {
            IsFull = false;
            Entries.Clear();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(IsFull);
            writer.Put((ushort)Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                writer.Put(Entries[i].UniqueId);
                writer.Put(Entries[i].OnMask);
                writer.Put(Entries[i].EmitMask);
                writer.Put(Entries[i].GuideMask);
            }
        }

        public static SensorStateMessage Deserialize(NetDataReader reader)
        {
            var msg = new SensorStateMessage { IsFull = reader.GetBool() };
            int count = reader.GetUShort();
            for (int i = 0; i < count; i++)
                msg.Entries.Add(new Entry
                {
                    UniqueId  = reader.GetInt(),
                    OnMask    = reader.GetULong(),
                    EmitMask  = reader.GetULong(),
                    GuideMask = reader.GetULong(),
                });
            return msg;
        }
    }
}
