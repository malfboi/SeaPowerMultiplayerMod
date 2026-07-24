using HarmonyLib;
using SeaPower;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// CLIENT-side: overlay the host's tactical picture onto the locally-simulated
    /// one. Vehicle.UpdateFromECS rebuilds Class/UnitTaskforce from the client's own
    /// sensor pipeline every plotting-table tick and ends with
    /// <c>Identified.Value = Class.HasValue</c>, so the overlay has to run after it -
    /// a one-shot assignment when the packet arrives would be overwritten on the
    /// next tick.
    ///
    /// Track number is taken from the host unconditionally: a shared number is the
    /// entire point. Side and class are only taken when the host actually has them,
    /// so a contact the client identified first is never pushed back to unknown.
    /// </summary>
    [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.UpdateFromECS))]
    public static class Patch_Vehicle_UpdateFromECS_ContactSync
    {
        static void Postfix(Vehicle __instance)
        {
            if (!Suppression.ClientActive) return;
            if (Plugin.Instance.CfgPvP.Value) return; // co-op only - opponents keep separate pictures
            if (!ContactSyncManager.HasOverrides) return;

            // Only the picture the local player actually looks at.
            if (__instance.ReportingSide != Globals._playerTaskforce) return;

            var obj = __instance.BaseObject;
            if (obj == null || obj.UniqueID == 0) return;
            if (!ContactSyncManager.TryGet(obj.UniqueID, out var ov)) return;

            __instance.Id = ov.TrackId;

            // "Classified" means the host has worked out whose the contact is; the
            // side itself is resolved locally from the contact's real taskforce
            // rather than sent, so nothing perspective-dependent crosses the wire.
            if (ov.Classified && obj._taskforce != null)
                __instance.UnitTaskforce.Value = obj._taskforce;

            if (ov.BoxedClass != null && ContactSyncManager.ApplyClass(__instance, ov.BoxedClass))
                __instance.Identified.Value = true;
        }
    }
}
