using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → client: the bottom-row status of a unit - its order/status line and
    /// the engagement state of each weapon mount.
    ///
    /// Both are written only by systems the client does not run. The status line
    /// (<c>ObjectBase.CurrentOrderText</c>) comes from the aircraft/submarine state
    /// machines, formation leadership, morale and refuelling - all host-side, so a
    /// client-side unit showed a blank or join-time-stale line. The mount status
    /// (<c>WeaponSystem.GetStatus()</c>) reads <c>_executingEngageTask</c>,
    /// <c>_isAutoEngaging</c> and <c>_engageState</c>, which only the host's engage
    /// pipeline (HandleEngageTasks) sets - so a ship the player had ordered to
    /// engage read "Ready" on every mount client-side.
    ///
    /// State, not events: a missed change would leave a unit's line wrong until it
    /// happened to change again, so the periodic full sweep repairs it.
    ///
    /// The status line travels as the host's already-localized string. The sources
    /// interpolate track numbers, weapon names and battery percentages into their
    /// text, so there is no key + argument form to send instead; a client running a
    /// different language sees the host's.
    /// </summary>
    public class UnitStatusMessage : INetMessage
    {
        /// <summary>Per-mount engagement state, parallel to <c>_obp._weaponSystems</c>
        /// (built from the unit's ini, so it is the same list in the same order on
        /// both machines - the same assumption MountIndexOf already relies on).</summary>
        public struct Mount
        {
            public bool ExecutingEngageTask;
            public bool AutoEngaging;
            public byte EngageState;   // WeaponSystem.EngageState (40 values)
        }

        public struct Entry
        {
            public int    UniqueId;
            public string OrderText;
            public List<Mount> Mounts;
        }

        /// <summary>True on the periodic sweep. Incremental packets carry only
        /// units whose status changed.</summary>
        public bool IsFull;

        public readonly List<Entry> Entries = new(64);

        public MessageType Type => MessageType.UnitStatus;

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
                var e = Entries[i];
                writer.Put(e.UniqueId);
                writer.Put(e.OrderText ?? "");
                int count = e.Mounts?.Count ?? 0;
                writer.Put((byte)count);
                for (int m = 0; m < count; m++)
                {
                    var mount = e.Mounts![m];
                    byte flags = (byte)((mount.ExecutingEngageTask ? 1 : 0) | (mount.AutoEngaging ? 2 : 0));
                    writer.Put(flags);
                    writer.Put(mount.EngageState);
                }
            }
        }

        public static UnitStatusMessage Deserialize(NetDataReader reader)
        {
            var msg = new UnitStatusMessage { IsFull = reader.GetBool() };
            int count = reader.GetUShort();
            for (int i = 0; i < count; i++)
            {
                var entry = new Entry
                {
                    UniqueId  = reader.GetInt(),
                    OrderText = reader.GetString(),
                    Mounts    = new List<Mount>(),
                };
                int mountCount = reader.GetByte();
                for (int m = 0; m < mountCount; m++)
                {
                    byte flags = reader.GetByte();
                    entry.Mounts.Add(new Mount
                    {
                        ExecutingEngageTask = (flags & 1) != 0,
                        AutoEngaging        = (flags & 2) != 0,
                        EngageState         = reader.GetByte(),
                    });
                }
                msg.Entries.Add(entry);
            }
            return msg;
        }
    }
}
