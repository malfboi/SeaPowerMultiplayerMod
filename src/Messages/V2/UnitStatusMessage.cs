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

            /// <summary>What this mount is training on (WeaponSystem._targetObject), or
            /// 0. The client knew a mount was engaging but not at WHAT, so its mounts
            /// never trained: the slew is WeaponSystem.alignToTarget → _mount.rotate,
            /// driven by the launcher's own engagement, and on a client that engagement
            /// never exists - the shot is relayed and the round comes back as a replica.
            ///
            /// CIWS were the exception, and the tell: CosmeticEventHandler sets
            /// _currentClosestTarget when the CiwsStart burst event arrives, so they
            /// slewed - but only from the moment they opened fire, which is why they
            /// were seen shooting off to one side and turning in. Cannons and missile
            /// launchers, which get no target at all, never moved.</summary>
            public int TargetId;
        }

        public struct Entry
        {
            public int    UniqueId;
            public string OrderText;
            public List<Mount> Mounts;

            /// <summary>Air units only (0 elsewhere): <c>ObjectBase.RangeInKm</c>, the
            /// one number the whole fuel picture is derived from.
            ///
            /// Both machines were burning their own. UpdateFuelConsumption runs from
            /// the flight physics, which the client runs too - its replica flies a
            /// slightly different path at its own command Mach and altitude, and the
            /// consumption coefficient is a function of exactly those two, so the two
            /// tanks separate from the first second and never re-converge. The bingo
            /// verdict is evaluated on the HOST's copy
            /// (Aircraft.cs:312, RangeOnMap &lt; 0.1), so the owner watched their
            /// aircraft turn for home against an endurance readout computed from a
            /// different aeroplane: playtest 37's "aircraft are reporting bingo fuel
            /// long before they are actually bingo fuel".
            ///
            /// Sending this one value is enough because ActualRangeInKm and RangeOnMap
            /// are recomputed from it every physics tick (Aircraft.cs:1538-1539), and
            /// the home base already replicates - so the readout, the map ring and the
            /// bingo threshold all follow.</summary>
            public float RangeKm;
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
                writer.Put(e.RangeKm);
                int count = e.Mounts?.Count ?? 0;
                writer.Put((byte)count);
                for (int m = 0; m < count; m++)
                {
                    var mount = e.Mounts![m];
                    byte flags = (byte)((mount.ExecutingEngageTask ? 1 : 0) | (mount.AutoEngaging ? 2 : 0));
                    writer.Put(flags);
                    writer.Put(mount.EngageState);
                    writer.Put(mount.TargetId);
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
                    RangeKm   = reader.GetFloat(),
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
                        TargetId            = reader.GetInt(),
                    });
                }
                msg.Entries.Add(entry);
            }
            return msg;
        }
    }
}
