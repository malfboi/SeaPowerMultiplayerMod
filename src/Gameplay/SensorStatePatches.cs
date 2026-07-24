using HarmonyLib;
using SeaPower;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// CLIENT-side: the host decides whether a radar is radiating, and that verdict
    /// has to survive the local sensor update.
    ///
    /// <c>SensorSystemRadar.OnUpdate</c> drives <c>Radar._isActive</c> from local
    /// state every frame - straight off <c>IsOn</c> for a search radar, and off
    /// target assignment for a fire-control radar. Neither is trustworthy on the
    /// client: AI is host-only so no target is ever assigned, and Enable/Disable
    /// warm up over several ticks so IsOn lags the host by a noticeable window.
    ///
    /// The consequence was concrete: writing <c>_isActive</c> from the host stream
    /// was undone within one frame, the radar never actually radiated client-side,
    /// and <c>RadarCalculator</c> - which gates anti-radiation homing on
    /// <c>_radar._isActive || _isGuiding</c> - refused to let a HARM take the shot
    /// even once both players could see the site.
    ///
    /// A postfix wins that race by construction: it runs after the local logic has
    /// had its say, every frame, so there is no window in which the wrong value is
    /// visible to the sensor pipeline.
    /// </summary>
    [HarmonyPatch(typeof(SensorSystemRadar), nameof(SensorSystemRadar.OnUpdate))]
    public static class Patch_SensorSystemRadar_OnUpdate_ClientEmit
    {
        static void Postfix(SensorSystemRadar __instance)
        {
            if (!Suppression.ClientActive) return;
            if (!SensorStateManager.DesiredEmit.TryGetValue(__instance, out var want)) return;

            // A destroyed sensor must stay dark whatever the host last said - the
            // kill may simply not have reached us yet.
            if (__instance.IsDestroyed || __instance.Inoperable.Value) return;

            // _isGuiding as well as _isActive: RadarCalculator skips a Targeting
            // radar outright unless it is guiding, so for an illuminator this bit
            // IS the emission. ClearGuiding() may zero it locally at any time -
            // this runs after, every frame, so the host's verdict stands.
            if (__instance._isGuiding != want.guide)
                __instance._isGuiding = want.guide;

            var radar = __instance.getRadar();
            if (radar != null && radar._isActive != want.emit)
                radar.setActive(want.emit);
        }
    }
}
