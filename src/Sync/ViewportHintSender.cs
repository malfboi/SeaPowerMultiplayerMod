using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Client-side: tells the host where the camera is looking so it can stream the
    /// handful of units actually on screen at a higher rate. Cheap and best-effort -
    /// the host falls back to a flat rate for everything if these never arrive.
    /// </summary>
    public static class ViewportHintSender
    {
        private const float SendIntervalSec = 0.5f;

        // The world's axes are not the same scale: horizontal is ~67 m per Unity unit
        // (see UnitReplicaDriver.UnityPerKnotSecond) while transform.y is raw metres,
        // so camera height has to be converted before it can be used as a horizontal
        // radius. Getting this wrong pins the radius at its ceiling and quietly makes
        // every unit "near".
        private const float MetresPerUnityUnit = 67.2f;

        // The camera sits above what it looks at, so its height is a fair proxy for how
        // much world is on screen. Slightly wider than the strict frustum, so a unit is
        // already smooth by the time it comes into view.
        private const float ViewFactor     = 1.5f;
        private const float MinRadiusUnity = 2f;    // ~130 m
        private const float MaxRadiusUnity = 200f;  // ~13 km

        private static float _nextSendRealTime;

        public static void Tick()
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (!NetworkManager.Instance.IsEstablished) return;
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;

            float now = Time.unscaledTime;
            if (now < _nextSendRealTime) return;
            _nextSendRealTime = now + SendIntervalSec;

            if (!Singleton<CameraManager>.InstanceExists(false)) return;
            var cam = Singleton<CameraManager>.Instance;

            Vector3 camPos = cam.getCameraUnityPosition();
            var geo = Utils.worldPositionFromUnityToLongLat(camPos, Globals._currentCenterTile);

            NetworkManager.Instance.SendToServer(new ViewportHintMessage
            {
                LonDeg      = geo._longitude,
                LatDeg      = geo._latitude,
                RadiusUnity = Mathf.Clamp(
                    cam.getCameraHeight() / MetresPerUnityUnit * ViewFactor,
                    MinRadiusUnity, MaxRadiusUnity),
            }, LiteNetLib.DeliveryMethod.Unreliable);
        }

        public static void Reset() => _nextSendRealTime = 0f;
    }
}
