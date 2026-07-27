using System;
using LiteNetLib.Utils;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer.Net2
{
    /// <summary>
    /// Wire codec for positions and angles.
    /// Lat/lon travel as int32 fixed-point at 1e-7 degrees (~1.1 cm) - float32 degrees
    /// would quantize to ~1 m and visibly jitter fast weapons. Height stays float32.
    /// Unity-space conversions go through the game's own helpers so floating-origin
    /// (center tile) handling matches the game exactly.
    /// </summary>
    public static class GeoCodec
    {
        public const double LatLonScale = 1e7;

        /// <summary>
        /// Horizontal world scale. Derived from the game's own knots-to-Unity constant:
        /// one knot-second is 0.514444 m and 0.0076554087 Unity units, so a unit is
        /// ~67.2 m. (Utils.longLatToLocal agrees - it maps 0.2 deg of latitude onto
        /// 330.71396 units.) Note this applies to X/Z only: transform.y is raw metres,
        /// so the axes are NOT the same scale and must never be mixed in one distance.
        /// </summary>
        public const float MetresPerUnityUnit = 67.2f;

        public static int PackLatLon(double degrees) => (int)Math.Round(degrees * LatLonScale);
        public static double UnpackLatLon(int fixedPoint) => fixedPoint / LatLonScale;

        public static void PutGeo(NetDataWriter w, GeoPosition g)
        {
            w.Put((int)Math.Round(g._longitude * LatLonScale));
            w.Put((int)Math.Round(g._latitude * LatLonScale));
            w.Put((float)g._height);
        }

        public static GeoPosition GetGeo(NetDataReader r)
        {
            int lon  = r.GetInt();
            int lat  = r.GetInt();
            float h  = r.GetFloat();
            return new GeoPosition(lat / LatLonScale, lon / LatLonScale, h);
        }

        public static void PutGeoFromUnity(NetDataWriter w, Vector3 worldPos)
            => PutGeo(w, ToGeo(worldPos));

        public static Vector3 GetGeoAsUnity(NetDataReader r)
        {
            var g = GetGeo(r);
            return ToUnity(g._latitude, g._longitude, (float)g._height);
        }

        // ── Precise geo <-> Unity ────────────────────────────────────────────
        //
        // The game's own Utils.longLatToLocal / worldPositionFromUnityToLongLat do
        // this arithmetic in float32, and both push the longitude through a value of
        // ~180 before scaling. A float32 near 180 resolves 1.5e-5 deg, so BOTH
        // directions snap east-west position to a ~1.1 m staircase (north-south fares
        // better at ~0.21 m, because 90-lat is a smaller magnitude).
        //
        // The host never notices: it renders its own transform and never round-trips.
        // The client does nothing but round-trip - it converts the host's geo into a
        // Unity target every frame - so the staircase lands directly on the replica,
        // and on a roughly north-south heading it lands almost entirely on the LATERAL
        // axis. That was the side-to-side jitter: a ~1 m tread, not smoothing error.
        //
        // These are the identical linear mapping in double, so results agree with the
        // game's world to well under a millimetre while dropping the staircase. Keep
        // them exact inverses of each other.
        private const double UnitsPerTile = 330.71396d;
        private const double DegPerTile   = 0.2d;
        private const double TileEdgeDeg  = 1d / 120d;

        public static Vector3 ToUnity(double latDeg, double lonDeg, float heightM)
        {
            var centre = Globals._currentCenterTile;

            double lonTiles = (lonDeg + 180d - TileEdgeDeg) / DegPerTile - Mathf.FloorToInt(centre.x);
            // Antimeridian handling, mirroring Utils.calculateCorrectionFor180EW
            if (System.Math.Abs(lonTiles) > 1795d) lonTiles += lonTiles > 1795d ? -1800d : 1800d;

            double latTiles = (90d - latDeg - TileEdgeDeg) / DegPerTile - Mathf.FloorToInt(centre.y);

            return new Vector3(
                (float)(UnitsPerTile * (lonTiles - 0.5d)),
                heightM,
                (float)(-UnitsPerTile * (latTiles - 0.5d)));
        }

        public static GeoPosition ToGeo(Vector3 worldPos)
        {
            var centre = Globals._currentCenterTile;

            double lon = DegPerTile * (worldPos.x / UnitsPerTile + Mathf.FloorToInt(centre.x) + 0.5d)
                         + TileEdgeDeg - 180d;
            double lat = 90d - (DegPerTile * (-worldPos.z / UnitsPerTile + Mathf.FloorToInt(centre.y) + 0.5d)
                         + TileEdgeDeg);

            return new GeoPosition(Utils.WrapAngle90(lat), Utils.WrapAngle(lon), worldPos.y);
        }

        // ── Angle / speed quantizers ─────────────────────────────────────────

        /// <summary>Heading 0-360° → u16 (0.0055° steps). 360 wraps to 0.</summary>
        public static ushort PackHeading(float deg)
            => unchecked((ushort)Mathf.RoundToInt(Mathf.Repeat(deg, 360f) * (65536f / 360f)));

        public static float UnpackHeading(ushort v) => v * (360f / 65536f);

        /// <summary>Signed angle (pitch/roll) → i16 centidegrees. Input normalized to [-180, 180).</summary>
        public static short PackAngleCdeg(float deg)
        {
            deg = Mathf.Repeat(deg + 180f, 360f) - 180f;
            return (short)Mathf.RoundToInt(deg * 100f);
        }

        public static float UnpackAngleCdeg(short v) => v / 100f;

        /// <summary>Speed in knots → u16 at 0.1 kt resolution (max 6553.5 kts).</summary>
        public static ushort PackSpeedKts(float kts)
            => (ushort)Mathf.Clamp(Mathf.RoundToInt(kts * 10f), 0, ushort.MaxValue);

        public static float UnpackSpeedKts(ushort v) => v / 10f;
    }
}
