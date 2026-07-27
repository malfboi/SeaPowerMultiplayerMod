using HarmonyLib;
using SeaPower;
using SeapowerUI.ViewModels;

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
            // Read live so switching the setting off on THIS machine reverts to our
            // own sensors immediately, without waiting on the host's clearing sweep.
            if (!Plugin.Instance.CfgContactSync.Value) return;
            if (!ContactSyncManager.HasOverrides) return;

            // Only the picture the local player actually looks at.
            if (__instance.ReportingSide != Globals._playerTaskforce) return;

            var obj = __instance.BaseObject;
            if (obj == null || obj.UniqueID == 0) return;
            if (!ContactSyncManager.TryGet(obj.UniqueID, out var ov))
            {
                // Not in the host's picture. Its number came from the client's own
                // counter, which runs over the same range the host allocates from -
                // leave it there and it collides with a host number the overlay
                // stamps on some other contact.
                if (obj._taskforce != Globals._playerTaskforce)
                    ContactSyncManager.EnsurePrivateTrackId(__instance, obj.UniqueID);
                return;
            }

            __instance.Id = ov.TrackId;

            // "Classified" means the host has worked out whose the contact is; the
            // side itself is resolved locally from the contact's real taskforce
            // rather than sent, so nothing perspective-dependent crosses the wire.
            if (ov.Classified && obj._taskforce != null)
            {
                __instance.UnitTaskforce.Value = obj._taskforce;

                // UnitTaskforce alone colours the contact but does not let the
                // player look at it. MainGameViewModel resolves every click to a
                // plotting-table Vehicle and only attaches the camera when Side
                // AND Class are both set; Side comes solely from the ECS
                // DetectedSide component, which the client's own sensors may
                // never produce for a ship the host has identified. Without this
                // a PID'd contact read as identified on the client and still
                // could not be viewed - host only.
                ContactSyncManager.ApplySideIfMissing(__instance, obj._taskforce);
            }

            if (ov.BoxedClass != null && ContactSyncManager.ApplyClass(__instance, ov.BoxedClass))
                __instance.Identified.Value = true;
        }
    }

    /// <summary>
    /// CLIENT-side: title the contact information window with the track number the
    /// rest of the UI is showing.
    ///
    /// The window reads its number from the ECS VehicleComponent.Id, which is
    /// stamped once when the plotting table creates the vehicle and never revised.
    /// The shared picture is applied to the managed Vehicle.Id instead, so the map
    /// says one number and this window says another - the client's original local
    /// one. Rewriting the ECS component would mean referencing Unity.Entities;
    /// re-titling the window from the managed Vehicle costs nothing and is the only
    /// place the stale number is user-visible.
    /// </summary>
    [HarmonyPatch(typeof(VehicleInfoViewModel), MethodType.Constructor, new[] { typeof(Vehicle) })]
    public static class Patch_VehicleInfoViewModel_Title
    {
        static void Postfix(VehicleInfoViewModel __instance, Vehicle target)
        {
            if (!Suppression.ClientActive) return;
            if (Plugin.Instance.CfgPvP.Value) return;
            if (target == null) return;

            // DictionaryWithFallbacks yields placeholder text rather than throwing
            // on a missing key, so this cannot break the window.
            string format = Singleton<LanguageResourceHandler>.Instance
                .LanguageDictionary["windowsvehicleinfoheader"];
            __instance.Name = string.Format(format, target.Id);
        }
    }
}
