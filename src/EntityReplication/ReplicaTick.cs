using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Runs the client's replica transform drivers exactly once per frame, and
    /// crucially BEFORE the camera samples the unit it is following.
    ///
    /// The camera does not follow <c>transform.position</c> - RenderPosition.OnUpdate
    /// calls <c>setGlobalPosition(SelectedObject.getGeoPosition())</c>, i.e. it reads
    /// the <c>_geoPosition</c> FIELD. That runs from GameUpdater.update() inside
    /// GameMain.Update(), while our drivers ran from Plugin.Update() - a separate
    /// MonoBehaviour whose Update lands after it. So the camera sampled the previous
    /// frame's geo position while the mesh rendered at the current frame's transform,
    /// and the two were exactly one frame of travel apart.
    ///
    /// On a ship that is 0.09 m and invisible. On a 540 kt missile it is 2.19 m - about
    /// half a Harpoon's length - so the camera framed the tail instead of the middle,
    /// and any variation in the per-frame step showed up as the weapon sliding back and
    /// forth INSIDE the view even when its world motion was fine. The host never had
    /// it: the game moves weapons inside GameMain.Update() before RenderPosition reads
    /// them, so its camera and mesh always agree.
    ///
    /// Driving from a prefix on the read site makes the ordering deterministic instead
    /// of depending on Unity's arbitrary MonoBehaviour order. Plugin.Update calls this
    /// too, as a fallback for when no RenderPosition exists (menus, scene loads); the
    /// frame guard means whichever fires first in a frame does the work.
    /// </summary>
    internal static class ReplicaTick
    {
        private static int _lastFrame = -1;

        internal static void RunOnce()
        {
            if (_lastFrame == Time.frameCount) return;
            _lastFrame = Time.frameCount;

            // Clock first: everything below resolves against it.
            UnitReplicaDriver.TickRenderClock();
            WeaponReplicaDriver.Tick();
            UnitReplicaDriver.Tick();
            DeckPuppetDriver.Tick();
        }
    }

    [HarmonyPatch(typeof(RenderPosition), nameof(RenderPosition.OnUpdate))]
    public static class Patch_RenderPosition_OnUpdate_DriveReplicas
    {
        static void Prefix() => ReplicaTick.RunOnce();
    }
}
