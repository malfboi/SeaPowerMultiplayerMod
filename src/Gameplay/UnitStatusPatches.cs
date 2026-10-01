using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Manual drum/box reload - the reload icon on the Unit Status window's mount
    /// ledger (Shift+Y, EngagementMountViewModel.ReloadCommand) and the weapons context
    /// menu (ObjectEffectorsViewModel). A player-only action: nothing in the sim calls
    /// ForceReload, so every call reaching here is a click and is forwarded through
    /// OrderSyncHelper like any other order. WeaponSystemGun overrides it without
    /// calling base, so both bodies are patched.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_WeaponSystem_ForceReload
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(WeaponSystem), nameof(WeaponSystem.ForceReload));
            yield return AccessTools.Method(typeof(WeaponSystemGun), nameof(WeaponSystemGun.ForceReload));
        }

        private static PlayerOrderMessage? Msg(WeaponSystem ws)
        {
            var unit = ws._baseObject;
            int index = unit?._obp?._weaponSystems?.IndexOf(ws) ?? -1;
            if (unit == null || unit.UniqueID == 0 || index < 0) return null;
            return new PlayerOrderMessage
            {
                SourceEntityId = unit.UniqueID,
                Order          = OrderType.ForceReload,
                Heading        = index,
            };
        }

        static bool Prefix(WeaponSystem __instance, out PlayerOrderMessage? __state)
        {
            // CanForceReload is read BEFORE the call - the reload itself makes it false.
            // A refused click is the game's own no-op and has nothing to relay.
            __state = __instance.CanForceReload() ? Msg(__instance) : null;
            return __state == null || OrderSyncHelper.Prefix(__instance._baseObject, __state);
        }

        static void Postfix(WeaponSystem __instance, PlayerOrderMessage? __state)
        {
            if (__state != null) OrderSyncHelper.Postfix(__instance._baseObject, __state);
        }
    }

    /// <summary>
    /// CLIENT-side: render the host's per-mount engagement into the weapons panel
    /// without touching the mount's simulation state.
    ///
    /// <c>WeaponSystem.GetStatus()</c> exists only to fill <c>DisplayState</c> (its
    /// two call sites are both that assignment), so overriding its result changes
    /// what the player reads and nothing else. The obvious alternative - writing the
    /// host's <c>_executingEngageTask</c> / <c>_engageState</c> onto the client's
    /// weapon system - is actively harmful: <c>WeaponSystemLauncher.OnUpdate</c>
    /// treats <c>_executingEngageTask</c> as "run the engage pipeline", so a client
    /// mount that never ran <c>launch()</c> walked into abort checks, container
    /// selection and hatch alignment with no ammo or target and threw an NRE every
    /// frame. That loop runs inside <c>ObjectBase.OnLazyUpdate</c>, so the throw also
    /// killed every system after it on that unit - a firing submarine's propeller
    /// stopped turning and its hatches stopped animating.
    ///
    /// The engagement label is rebuilt from the client's OWN localization keys, so it
    /// appears in the client's language rather than the host's.
    /// </summary>
    [HarmonyPatch(typeof(WeaponSystem), nameof(WeaponSystem.GetStatus))]
    public static class Patch_WeaponSystem_GetStatus_ClientEngage
    {
        static void Postfix(WeaponSystem __instance, ref string __result)
        {
            if (!Suppression.ClientActive) return;
            if (!UnitStatusManager.DesiredEngage.TryGetValue(__instance, out var want)) return;
            if (!want.exec && !want.auto) return;

            var lang = Singleton<LanguageResourceHandler>.Instance;
            if (lang == null) return;

            // Only an idle "Ready" reading gets replaced. Everything the game puts
            // ahead of Engaging in GetStatus - Inoperable, Offline, Empty, Reloading,
            // SensorsOff - is locally true and matters more to the player than the
            // host's engagement label, and the local "Ready(4/8)" suffix carries a
            // count we would otherwise throw away.
            string ready = lang.getText("BottomRow", "Ready");
            if (string.IsNullOrEmpty(ready) || __result == null || !__result.StartsWith(ready)) return;

            var state = (WeaponSystem.EngageState)want.state;
            __result = state != WeaponSystem.EngageState.NotObserved
                ? lang.getText("BottomRow", "Engaging") + " (" + lang.getText("Status", $"Weapon_{state}") + ")"
                : lang.getText("BottomRow", "Engaging");
        }
    }
}
