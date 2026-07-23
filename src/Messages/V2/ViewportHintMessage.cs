using LiteNetLib.Utils;
using SeaPower;
using SeapowerMultiplayer.Net2;

namespace SeapowerMultiplayer.Messages
{
    /// <summary>
    /// Client → host, ~2 Hz, unreliable: where the client's camera is looking and how
    /// much of the world it covers. The host streams units inside that circle at a
    /// higher rate, because those are the only ones whose smoothness the player can
    /// actually see. Dropping one of these costs nothing - the host just keeps using
    /// the previous focus until the next arrives.
    ///
    /// The focus travels as geo, not Unity coordinates: floating-origin center tiles
    /// are per-machine, so a raw Unity position would land somewhere else on the host.
    /// </summary>
    public class ViewportHintMessage : INetMessage
    {
        public double LonDeg;
        public double LatDeg;
        public float  RadiusUnity;   // Unity units (~67 m each)

        public MessageType Type => MessageType.ViewportHint;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(GeoCodec.PackLatLon(LonDeg));
            writer.Put(GeoCodec.PackLatLon(LatDeg));
            writer.Put(RadiusUnity);
        }

        public static ViewportHintMessage Deserialize(NetDataReader reader) => new()
        {
            LonDeg      = GeoCodec.UnpackLatLon(reader.GetInt()),
            LatDeg      = GeoCodec.UnpackLatLon(reader.GetInt()),
            RadiusUnity = reader.GetFloat(),
        };

        /// <summary>Host-side: the focus in this machine's Unity space.</summary>
        public UnityEngine.Vector3 ToLocalUnity()
            => Utils.longLatToLocalV3(new GeoPosition(LatDeg, LonDeg, 0.0), Globals._currentCenterTile);
    }
}
