using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → client, ReliableOrdered, throttled + change-detected: the flight-ops
    /// state of one carrier - its full task queue (pending launches plus the active
    /// launch/recovery/cooldown rows) and the squadron/vehicle availability counts
    /// and ammo stores the Flight Ops UI reads. The client runs no flight-deck logic
    /// of its own (its task pump is suppressed); it mirrors this snapshot so its
    /// Flight Ops window matches the host. Indices are stable across machines (both
    /// build the deck from the same vessel ini). An empty Tasks list with the
    /// carrier id clears the client's queue.
    ///
    /// CHUNKED: a supercarrier's snapshot (40+ vehicle entries, ~300 squadron
    /// entries) exceeds the ~1000-byte packet floor, and >MTU reliable packets
    /// arrive corrupted past the first fragment in the game's Mono runtime
    /// (verified live; plain LiteNetLib on .NET is fine). One logical snapshot is
    /// therefore split across ChunkCount sub-MTU messages sharing CarrierId/ammo;
    /// ReliableOrdered delivery makes the train contiguous and in-order, so the
    /// client just accumulates until the last chunk and applies the union.
    /// </summary>
    public class FlightDeckStateMessage : INetMessage
    {
        public int   CarrierId;
        public byte  ChunkIdx;
        public byte  ChunkCount = 1;
        public float CurrentAmmo;

        // Authoritative availability the UI shows for "aircraft to prepare".
        public readonly List<VehicleCount>  VehicleNumbers  = new();
        public readonly List<SquadronCount> SquadronNumbers = new();
        public readonly List<AmmoCategory> AccountableAmmo = new();

        // The full task queue, in the host's display order. Pending rows carry the
        // indices to rebuild an interactive PendingLaunchTask client-side; all other
        // task types (launching / recovery / cooldown) are display-only rows.
        public readonly List<TaskRow> Tasks = new();

        public struct VehicleCount  { public byte VehicleIdx; public short Numbers; }
        public struct SquadronCount { public byte VehicleIdx; public byte SquadronIdx; public short Numbers; }
        public struct AmmoCategory  { public string Name; public int Count; }

        public struct TaskRow
        {
            public bool   IsPending;    // pending-launch row vs display-only row
            public Guid   Uid;          // stable task identity for cross-snapshot matching
            public string Label;        // host-computed FlightDeckTaskLabel (state text)
            public string Info;         // host-computed Info (progress / remaining text)

            // Pending rows only:
            public byte   VehicleIdx;
            public byte   LoadoutIdx;
            public byte   SquadronIdx;
            public byte   CallsignIdx;
            public short  LaunchCount;
            // The deck/crew a readying flight is occupying. FlightDeck.OnUpdate sums
            // these across its pending tasks for the Flight Ops utilisation readouts,
            // and the client's deck pipeline - which is what assigns them - is
            // suppressed, so without them mirrored every carrier reads 0%.
            public short  DeckSpots;
            public short  GroundCrew;
            public bool   LaunchAllowed;
            public bool   AwaitingLaunch; // host task sits in HandleAwaitSpawnTask - the
                                          // player's LAUNCH command releases it

            // Display-only rows:
            public string AircraftType;
            public string SquadronName;
            public byte   CrewSkill;
        }

        public MessageType Type => MessageType.FlightDeckState;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(CarrierId);
            writer.Put(ChunkIdx);
            writer.Put(ChunkCount);
            writer.Put(CurrentAmmo);

            writer.Put((byte)VehicleNumbers.Count);
            for (int i = 0; i < VehicleNumbers.Count; i++)
            {
                writer.Put(VehicleNumbers[i].VehicleIdx);
                writer.Put(VehicleNumbers[i].Numbers);
            }

            writer.Put((byte)SquadronNumbers.Count);
            for (int i = 0; i < SquadronNumbers.Count; i++)
            {
                writer.Put(SquadronNumbers[i].VehicleIdx);
                writer.Put(SquadronNumbers[i].SquadronIdx);
                writer.Put(SquadronNumbers[i].Numbers);
            }

            writer.Put((byte)AccountableAmmo.Count);
            for (int i = 0; i < AccountableAmmo.Count; i++)
            {
                writer.Put(AccountableAmmo[i].Name ?? "");
                writer.Put(AccountableAmmo[i].Count);
            }

            writer.Put((byte)Tasks.Count);
            for (int i = 0; i < Tasks.Count; i++)
            {
                var t = Tasks[i];
                writer.Put(t.IsPending);
                var b = t.Uid.ToByteArray();
                for (int k = 0; k < 16; k++) writer.Put(b[k]);
                writer.Put(t.Label ?? "");
                writer.Put(t.Info ?? "");
                if (t.IsPending)
                {
                    writer.Put(t.VehicleIdx);
                    writer.Put(t.LoadoutIdx);
                    writer.Put(t.SquadronIdx);
                    writer.Put(t.CallsignIdx);
                    writer.Put(t.LaunchCount);
                    writer.Put(t.DeckSpots);
                    writer.Put(t.GroundCrew);
                    writer.Put(t.LaunchAllowed);
                    writer.Put(t.AwaitingLaunch);
                }
                else
                {
                    writer.Put(t.AircraftType ?? "");
                    writer.Put(t.SquadronName ?? "");
                    writer.Put(t.CrewSkill);
                }
            }
        }

        public static FlightDeckStateMessage Deserialize(NetDataReader reader)
        {
            var msg = new FlightDeckStateMessage
            {
                CarrierId   = reader.GetInt(),
                ChunkIdx    = reader.GetByte(),
                ChunkCount  = reader.GetByte(),
                CurrentAmmo = reader.GetFloat(),
            };

            int vCount = reader.GetByte();
            for (int i = 0; i < vCount; i++)
                msg.VehicleNumbers.Add(new VehicleCount
                {
                    VehicleIdx = reader.GetByte(),
                    Numbers    = reader.GetShort(),
                });

            int sCount = reader.GetByte();
            for (int i = 0; i < sCount; i++)
                msg.SquadronNumbers.Add(new SquadronCount
                {
                    VehicleIdx  = reader.GetByte(),
                    SquadronIdx = reader.GetByte(),
                    Numbers     = reader.GetShort(),
                });

            int aCount = reader.GetByte();
            for (int i = 0; i < aCount; i++)
                msg.AccountableAmmo.Add(new AmmoCategory
                {
                    Name  = reader.GetString(),
                    Count = reader.GetInt(),
                });

            int tCount = reader.GetByte();
            for (int i = 0; i < tCount; i++)
            {
                var t = new TaskRow { IsPending = reader.GetBool() };
                var b = new byte[16];
                for (int k = 0; k < 16; k++) b[k] = reader.GetByte();
                t.Uid   = new Guid(b);
                t.Label = reader.GetString();
                t.Info  = reader.GetString();
                if (t.IsPending)
                {
                    t.VehicleIdx     = reader.GetByte();
                    t.LoadoutIdx     = reader.GetByte();
                    t.SquadronIdx    = reader.GetByte();
                    t.CallsignIdx    = reader.GetByte();
                    t.LaunchCount    = reader.GetShort();
                    t.DeckSpots      = reader.GetShort();
                    t.GroundCrew     = reader.GetShort();
                    t.LaunchAllowed  = reader.GetBool();
                    t.AwaitingLaunch = reader.GetBool();
                }
                else
                {
                    t.AircraftType = reader.GetString();
                    t.SquadronName = reader.GetString();
                    t.CrewSkill    = reader.GetByte();
                }
                msg.Tasks.Add(t);
            }

            return msg;
        }
    }
}
