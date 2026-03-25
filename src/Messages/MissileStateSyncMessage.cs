using System.Collections.Generic;
using LiteNetLib.Utils;

namespace SeapowerMultiplayer.Messages
{
    public struct MissileStateEntry
    {
        public int   MissileId;
        // Bools packed into 2 bytes during serialization
        public bool  Jammed;
        public bool  FuseActive;
        public bool  IgnoreCollisions;
        public bool  ConnectionLost;
        public bool  ConnectionLostForever;
        public bool  TargetLost;
        public bool  CollisionNoticed;
        public bool  PoppingUp;
        public bool  HasPoppedUp;
        // Floats
        public float DeviationMagnitudeWithJam;
        public float CurrentPitch;
        public float X, Y, Z;   // GeoPosition: lon, height, lat
        public float Heading;
        public float Speed;
    }

    public class MissileStateSyncMessage : INetMessage
    {
        public MessageType Type => MessageType.MissileStateSync;

        public List<MissileStateEntry> Entries = new(32);

        public void Reset()
        {
            Entries.Clear();
        }

        public void Serialize(NetDataWriter w)
        {
            w.Put((ushort)Entries.Count);
            foreach (var e in Entries)
            {
                w.Put(e.MissileId);

                // Pack 8 bools into byte 0
                byte b0 = 0;
                if (e.Jammed)               b0 |= 1;
                if (e.FuseActive)           b0 |= 2;
                if (e.IgnoreCollisions)     b0 |= 4;
                if (e.ConnectionLost)       b0 |= 8;
                if (e.ConnectionLostForever) b0 |= 16;
                if (e.TargetLost)           b0 |= 32;
                if (e.CollisionNoticed)     b0 |= 64;
                if (e.PoppingUp)            b0 |= 128;
                w.Put(b0);

                // byte 1: remaining bools
                byte b1 = 0;
                if (e.HasPoppedUp) b1 |= 1;
                w.Put(b1);

                w.Put(e.DeviationMagnitudeWithJam);
                w.Put(e.CurrentPitch);
                w.Put(e.X);
                w.Put(e.Y);
                w.Put(e.Z);
                w.Put(e.Heading);
                w.Put(e.Speed);
            }
        }

        public static MissileStateSyncMessage Deserialize(NetDataReader r)
        {
            var msg = new MissileStateSyncMessage();
            int count = r.GetUShort();
            for (int i = 0; i < count; i++)
            {
                var e = new MissileStateEntry();
                e.MissileId = r.GetInt();

                byte b0 = r.GetByte();
                e.Jammed               = (b0 & 1)   != 0;
                e.FuseActive           = (b0 & 2)   != 0;
                e.IgnoreCollisions     = (b0 & 4)   != 0;
                e.ConnectionLost       = (b0 & 8)   != 0;
                e.ConnectionLostForever = (b0 & 16)  != 0;
                e.TargetLost           = (b0 & 32)  != 0;
                e.CollisionNoticed     = (b0 & 64)  != 0;
                e.PoppingUp            = (b0 & 128) != 0;

                byte b1 = r.GetByte();
                e.HasPoppedUp = (b1 & 1) != 0;

                e.DeviationMagnitudeWithJam = r.GetFloat();
                e.CurrentPitch              = r.GetFloat();
                e.X       = r.GetFloat();
                e.Y       = r.GetFloat();
                e.Z       = r.GetFloat();
                e.Heading = r.GetFloat();
                e.Speed   = r.GetFloat();

                msg.Entries.Add(e);
            }
            return msg;
        }
    }
}
