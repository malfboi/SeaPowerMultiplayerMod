using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Host → client, ReliableOrdered, throttled + change-detected: the flight-ops
    /// state of one carrier - its pending launch queue (aircraft being readied) plus
    /// the squadron/vehicle availability counts and ammo stores the Flight Ops UI
    /// reads. The client runs no flight-deck logic of its own (its task pump is
    /// suppressed); it mirrors this snapshot so its Flight Ops window matches the host.
    /// Indices are stable across machines (both build the deck from the same vessel
    /// ini). An empty Tasks list with the carrier id clears the client's queue.
    /// </summary>
    public class FlightDeckStateMessage : INetMessage
    {
        public int   CarrierId;
        public float CurrentAmmo;

        // Authoritative availability the UI shows for "aircraft to prepare".
        public readonly List<VehicleCount>  VehicleNumbers  = new();
        public readonly List<SquadronCount> SquadronNumbers = new();

        // The pending-launch queue (aircraft being readied), in display order.
        public readonly List<PendingTask> Tasks = new();

        public struct VehicleCount  { public byte VehicleIdx; public short Numbers; }
        public struct SquadronCount { public byte VehicleIdx; public byte SquadronIdx; public short Numbers; }

        public struct PendingTask
        {
            public Guid   Uid;          // stable task identity for cross-snapshot matching
            public byte   VehicleIdx;
            public byte   LoadoutIdx;
            public byte   SquadronIdx;
            public byte   CallsignIdx;
            public short  LaunchCount;
            public bool   LaunchAllowed;
            public bool   AwaitingLaunch; // host task sits in HandleAwaitSpawnTask - the
                                          // player's LAUNCH command releases it
            public string Label;        // host-computed FlightDeckTaskLabel (state text)
            public string Info;         // host-computed Info (progress / remaining text)
        }

        public MessageType Type => MessageType.FlightDeckState;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(CarrierId);
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

            writer.Put((byte)Tasks.Count);
            for (int i = 0; i < Tasks.Count; i++)
            {
                var t = Tasks[i];
                var b = t.Uid.ToByteArray();
                for (int k = 0; k < 16; k++) writer.Put(b[k]);
                writer.Put(t.VehicleIdx);
                writer.Put(t.LoadoutIdx);
                writer.Put(t.SquadronIdx);
                writer.Put(t.CallsignIdx);
                writer.Put(t.LaunchCount);
                writer.Put(t.LaunchAllowed);
                writer.Put(t.AwaitingLaunch);
                writer.Put(t.Label ?? "");
                writer.Put(t.Info ?? "");
            }
        }

        public static FlightDeckStateMessage Deserialize(NetDataReader reader)
        {
            var msg = new FlightDeckStateMessage
            {
                CarrierId   = reader.GetInt(),
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

            int tCount = reader.GetByte();
            for (int i = 0; i < tCount; i++)
            {
                var b = new byte[16];
                for (int k = 0; k < 16; k++) b[k] = reader.GetByte();
                msg.Tasks.Add(new PendingTask
                {
                    Uid           = new Guid(b),
                    VehicleIdx    = reader.GetByte(),
                    LoadoutIdx    = reader.GetByte(),
                    SquadronIdx   = reader.GetByte(),
                    CallsignIdx   = reader.GetByte(),
                    LaunchCount   = reader.GetShort(),
                    LaunchAllowed  = reader.GetBool(),
                    AwaitingLaunch = reader.GetBool(),
                    Label         = reader.GetString(),
                    Info          = reader.GetString(),
                });
            }

            return msg;
        }
    }
}
