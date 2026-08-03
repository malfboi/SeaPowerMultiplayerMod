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
    /// CLIENT-side: answer "identify yourself" with the HOST's roll, not our own.
    ///
    /// AI.GetLegacyCompliance rolls Globals._rnd once per neutral unit and caches it,
    /// lazily, on first read - so the two machines roll independently and disagree.
    /// Each player therefore got their own 60% chance off the same merchant, and
    /// asking on both screens raised the odds of a successful identification to 84%.
    /// The host's roll travels in ContactSync; here it becomes the answer both
    /// players get. Unknown means the host has not reported the contact yet, in
    /// which case the local value stands.
    /// </summary>
    [HarmonyPatch(typeof(AI), "get_CurrentCompliance")]
    public static class Patch_AI_CurrentCompliance_Shared
    {
        static void Postfix(ObjectBase ____baseObject, ref AI.Compliance __result)
        {
            if (!Suppression.ClientActive) return;
            if (Plugin.Instance.CfgPvP.Value) return; // co-op only, like the rest of the shared picture
            if (____baseObject == null || ____baseObject.UniqueID == 0) return;

            var shared = ContactSyncManager.ComplianceFor(____baseObject.UniqueID);
            if (shared != AI.Compliance.Unknown) __result = shared;
        }
    }

    /// <summary>
    /// CLIENT-side: an identification obtained by radio has to reach the host too.
    ///
    /// "Request: identify yourself" ends in Utils.RevealContactToObject with a "Comms"
    /// sensor type and the identifying classificationOverride - a purely local ECS
    /// write. The shared picture only flows host → client, so a merchant the CLIENT
    /// talked into identifying itself stayed unknown on the host's screen forever.
    /// Forward the request; the host performs the same reveal against its own picture
    /// and ContactSync carries the resulting class back down. The local reveal still
    /// runs, so the asking player sees the answer immediately.
    ///
    /// Only the comms path is forwarded. ContactRevealManager's own sweep also calls
    /// this method (sensorType "None") and must not bounce back upstream.
    /// </summary>
    [HarmonyPatch(typeof(Utils), nameof(Utils.RevealContactToObject))]
    public static class Patch_Utils_RevealContactToObject_Share
    {
        static void Prefix(ObjectBase hostObject, ObjectBase contactObject, string sensorType)
        {
            if (sensorType != "Comms") return;
            if (!Suppression.ClientActive) return;
            if (Plugin.Instance.CfgPvP.Value) return;
            if (OrderHandler.ApplyingFromNetwork) return;
            if (hostObject == null || contactObject == null) return;
            if (hostObject.UniqueID == 0 || contactObject.UniqueID == 0) return;

            NetworkManager.Instance.SendToServer(new Messages.PlayerOrderMessage
            {
                SourceEntityId = hostObject.UniqueID,
                Order          = Messages.OrderType.RequestIdentify,
                TargetEntityId = contactObject.UniqueID,
            });
            Plugin.Log.LogInfo($"[Contacts] Upstream identify request: asker={hostObject.UniqueID} " +
                $"contact={contactObject.UniqueID}");
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
