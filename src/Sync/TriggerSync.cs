using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Mission triggers are decided on the host alone.
    ///
    /// The game evaluates every trigger each frame (MissionManager.OnUpdate →
    /// Trigger.CheckTrigger) and a firing runs TryActionTrigger, which mixes two kinds
    /// of action: ones that change the simulation (teleport, systems, EMCON, despawn,
    /// destroy, waypoints, attack, relations, troop delivery, deck preparation...) and
    /// ones that only show the player something (mission messages, intel, forecast,
    /// mission timers, objectives, voice, victory music, free-content windows). A guest
    /// used to do both against its own replicas - conditions read replica state, so
    /// they could fire at a different moment or not at all, and the simulation half
    /// then acted on units the host owns.
    ///
    /// Now the guest evaluates nothing. Each firing on the host is announced, and the
    /// guest runs the game's own TryActionTrigger for it with no action units - the
    /// whole unit block is skipped - and with the global steps that would act on the
    /// simulation switched off for the call. Everything else is the game's own
    /// presentation code, filtered per side by the guest's own player taskforce exactly
    /// as on the host. Simulation results arrive through the usual streams.
    /// </summary>
    public static class TriggerSync
    {
        /// <summary>True while the guest replays a host firing.</summary>
        private static bool _replaying;

        private static readonly AccessTools.FieldRef<Trigger, bool> _prepareFlightDeck =
            AccessTools.FieldRefAccess<Trigger, bool>("_prepareFlightDeck");
        private static readonly AccessTools.FieldRef<Trigger, bool> _useMatchingConditionUnits =
            AccessTools.FieldRefAccess<Trigger, bool>("_useMatchingConditionUnits");

        private static Trigger? Find(string section)
        {
            if (string.IsNullOrEmpty(section) || !Singleton<MissionManager>.InstanceExists(false)) return null;
            return Singleton<MissionManager>.Instance._triggers.FirstOrDefault(t => t._section == section);
        }

        /// <summary>Guest: play out a trigger the host fired.</summary>
        public static void ApplyFired(TriggerFiredMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;

            var trigger = Find(msg.Section);
            if (trigger == null)
            {
                Plugin.Log.LogWarning($"[Trigger] Host fired unknown trigger '{msg.Section}' - ignored");
                return;
            }

            // As on the host: CheckTrigger marked it completed before acting, and the
            // action's own "reactivate" list may clear that again.
            trigger._completed = true;
            trigger._currentUnit = msg.UnitId != 0 ? StateSerializer.FindById(msg.UnitId) : null;

            // Off for the call, each because it acts on the simulation even with no
            // action units: a matching-units trigger aborts on an empty list, troop
            // delivery aborts when no unit delivers, deck preparation falls back to
            // every carrier of the local player's side, and an air strike is queued on
            // the authored airbases. The deck's default message only applies to
            // OnMissionStart, which fires before any guest has joined.
            // The backing field, not UseMatchingConditionUnits: the setter picks a
            // condition when set true.
            bool matching = _useMatchingConditionUnits(trigger);
            bool prepare  = _prepareFlightDeck(trigger);
            int  troops   = trigger.DeliverTroops;
            var  strike   = trigger._airstrikeType;
            _useMatchingConditionUnits(trigger) = false;
            _prepareFlightDeck(trigger) = false;
            trigger.DeliverTroops = 0;
            trigger._airstrikeType = AirStrike.Type.None;
            _replaying = true;
            try { trigger.ActionTrigger(); }
            finally
            {
                _replaying = false;
                _useMatchingConditionUnits(trigger) = matching;
                _prepareFlightDeck(trigger) = prepare;
                trigger.DeliverTroops = troops;
                trigger._airstrikeType = strike;
            }
            Plugin.Log.LogInfo($"[Trigger] Played host trigger '{msg.Section}' ({trigger.Name})");
        }

        /// <summary>Host: a guest chose a trigger in a free-content window. Fired the way
        /// the window fires it; the firing is then announced like any other.</summary>
        public static void ApplyRequest(byte slot, TriggerRequestMessage msg)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            var trigger = Find(msg.Section);
            if (trigger == null)
            {
                Plugin.Log.LogWarning($"[Trigger] Slot {slot} requested unknown trigger '{msg.Section}' - ignored");
                return;
            }
            Plugin.Log.LogInfo($"[Trigger] Slot {slot} fires '{msg.Section}' ({trigger.Name})");
            trigger.Disabled = false;
            trigger._completed = true;
            trigger.ActionTrigger();
        }

        /// <summary>Guest: conditions are not evaluated - the host decides every firing.</summary>
        [HarmonyPatch(typeof(Trigger), nameof(Trigger.CheckTrigger))]
        public static class Patch_Trigger_CheckTrigger_ClientGate
        {
            static bool Prefix(ref bool __result)
            {
                if (!Suppression.ClientActive) return true;
                __result = false;
                return false;
            }
        }

        /// <summary>Every firing runs through here: CheckTrigger, and the public
        /// ActionTrigger the free-content window and the scripting runtime call.</summary>
        [HarmonyPatch(typeof(Trigger), "TryActionTrigger")]
        public static class Patch_Trigger_TryActionTrigger_Sync
        {
            static bool Prefix(Trigger __instance, ref bool __result)
            {
                if (!Suppression.ClientActive || _replaying) return true;

                // With CheckTrigger gated and the scripting runtime's FireTrigger
                // dropped below, what is left is a player's free-content choice: the
                // host fires it, and announces it back like any other.
                if (!string.IsNullOrEmpty(__instance._section))
                {
                    NetworkManager.Instance.SendToServer(new TriggerRequestMessage { Section = __instance._section });
                    Plugin.Log.LogInfo($"[Trigger] Asked host to fire '{__instance._section}' ({__instance.Name})");
                }
                __result = false;
                return false;
            }

            static void Postfix(Trigger __instance, bool __result)
            {
                if (!__result || !CaptureState.HostCaptureActive) return;
                NetworkManager.Instance.BroadcastToClients(new TriggerFiredMessage
                {
                    Section = __instance._section ?? "",
                    UnitId  = __instance._currentUnit != null ? __instance._currentUnit.UniqueID : 0,
                });
            }
        }

        /// <summary>The empty unit list that skips TryActionTrigger's unit block while a
        /// guest replays a host firing.</summary>
        [HarmonyPatch(typeof(Trigger), "ResolveActionUnits")]
        public static class Patch_Trigger_ResolveActionUnits_Replay
        {
            static void Postfix(ref List<ObjectBase> __result)
            {
                if (_replaying) __result = new List<ObjectBase>();
            }
        }

        /// <summary>Guest: the mission's script runs on both machines, and the host's
        /// copy fires the trigger for real.</summary>
        [HarmonyPatch]
        public static class Patch_CoreService_FireTrigger_ClientDrop
        {
            private static MethodBase? Target() => AccessTools.Method("SeaPower.CoreService:FireTrigger");

            // Internal to the game and part of an experimental runtime - skip the patch
            // rather than fail PatchAll if it is ever renamed.
            static bool Prepare() => Target() != null;

            static MethodBase TargetMethod() => Target()!;

            static bool Prefix() => !Suppression.ClientActive;
        }
    }
}
