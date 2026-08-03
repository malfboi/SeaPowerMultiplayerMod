using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Formation command replication, both directions, both modes.
    ///
    /// Only formation MEMBERSHIP was replicated before this, and only at spawn
    /// (EntitySpawn carries the leader id; UnitIdentityApplier rebuilds the group).
    /// Everything a player then did to a formation stayed on the machine that did it.
    ///
    /// Not everything needed a patch. The formation-wide orders that the UI already
    /// implements as a LOOP over members - air-defence status
    /// (ObjectBaseViewModel.SetFormationRuleOfEngagement), EMCON, air/surface search
    /// and active sonar (ToggleFormation*) - reach the per-unit patches once per unit
    /// and already synced correctly. What is added here is everything that acts on
    /// the formation OBJECT, where a single call has no per-unit equivalent to catch.
    ///
    /// Addressing: formations carry no id, so each op names a unit - the member it
    /// acts on, or the leader for formation-wide orders. Both sides hold the same
    /// membership and apply ops in the same order, so the key survives a leader swap.
    ///
    /// Echo safety: every path that applies replicated formation state already sets
    /// OrderHandler.ApplyingFromNetwork (StateSerializer.Apply for orders,
    /// UnitIdentityApplier for spawn-time joins), which is the first thing
    /// OrderSyncHelper checks. Nothing here can loop back.
    /// </summary>
    internal static class FormationSync
    {
        internal static PlayerOrderMessage Msg(ObjectBase unit, FormationOp op) =>
            new PlayerOrderMessage
            {
                SourceEntityId = unit.UniqueID,
                Order          = OrderType.FormationCommand,
                ShotsToFire    = (int)op,
            };

        /// <summary>Send in whichever direction applies: Prefix carries the client's
        /// order upstream, Postfix broadcasts the host's. Run back to back so a single
        /// call site covers both roles; a refusal tagged by Prefix is consumed by the
        /// matching Postfix, so nothing refused is ever broadcast.</summary>
        internal static void Send(ObjectBase unit, PlayerOrderMessage msg)
        {
            if (unit == null || unit.UniqueID == 0) return;
            OrderSyncHelper.Prefix(unit, msg);
            OrderSyncHelper.Postfix(unit, msg);
        }

        internal static ObjectBase? Leader(UnitFormation f) => f?.LeaderStation?.UnitObject;
    }

    /// <summary>Create (isLeader) and join. The constructor reaches a new formation's
    /// leader through this same call, so both cases are caught here.</summary>
    [HarmonyPatch(typeof(UnitFormation), nameof(UnitFormation.AddUnit))]
    public static class Patch_UnitFormation_AddUnit
    {
        static void Postfix(UnitFormation __instance, ObjectBase obj, bool isLeader)
        {
            if (obj == null) return;

            if (isLeader)
            {
                FormationSync.Send(obj, FormationSync.Msg(obj, FormationOp.Create));
                return;
            }

            var leader = FormationSync.Leader(__instance);
            if (leader == null) return;

            var msg = FormationSync.Msg(obj, FormationOp.Join);
            msg.TargetEntityId = leader.UniqueID;
            FormationSync.Send(obj, msg);
        }
    }

    [HarmonyPatch(typeof(UnitFormation), nameof(UnitFormation.DetachUnit))]
    public static class Patch_UnitFormation_DetachUnit
    {
        static void Postfix(ObjectBase obj)
        {
            if (obj != null) FormationSync.Send(obj, FormationSync.Msg(obj, FormationOp.Detach));
        }
    }

    [HarmonyPatch(typeof(UnitFormation), nameof(UnitFormation.SwapLeader))]
    public static class Patch_UnitFormation_SwapLeader
    {
        static void Postfix(ObjectBase obj)
        {
            if (obj != null) FormationSync.Send(obj, FormationSync.Msg(obj, FormationOp.SwapLeader));
        }
    }

    [HarmonyPatch(typeof(UnitFormation), nameof(UnitFormation.Disband))]
    public static class Patch_UnitFormation_Disband
    {
        // Read the leader in the PREFIX: by the time Disband returns the formation
        // has been torn down and there is no leader left to address the order to.
        static void Prefix(UnitFormation __instance, ref ObjectBase? __state)
            => __state = FormationSync.Leader(__instance);

        static void Postfix(ObjectBase? __state)
        {
            if (__state != null) FormationSync.Send(__state, FormationSync.Msg(__state, FormationOp.Disband));
        }
    }

    /// <summary>Formation cease fire. Distinct from ObjectBase.CeaseFire, which the
    /// per-unit patch handles - this one calls into its members with report:false,
    /// and that patch deliberately ignores unreported cease-fires, so the formation
    /// order was travelling nowhere.</summary>
    [HarmonyPatch(typeof(UnitFormation), nameof(UnitFormation.CeaseFire))]
    public static class Patch_UnitFormation_CeaseFire
    {
        static void Postfix(UnitFormation __instance, bool recall)
        {
            var leader = FormationSync.Leader(__instance);
            if (leader == null) return;
            var msg = FormationSync.Msg(leader, FormationOp.CeaseFire);
            msg.Speed = recall ? 1f : 0f;
            FormationSync.Send(leader, msg);
        }
    }

    [HarmonyPatch(typeof(UnitFormation), nameof(UnitFormation.AllReturnToFormation))]
    public static class Patch_UnitFormation_AllReturnToFormation
    {
        static void Postfix(UnitFormation __instance, bool ceaseFire)
        {
            var leader = FormationSync.Leader(__instance);
            if (leader == null) return;
            var msg = FormationSync.Msg(leader, FormationOp.RecallAll);
            msg.Speed = ceaseFire ? 1f : 0f;
            FormationSync.Send(leader, msg);
        }
    }

    [HarmonyPatch(typeof(UnitFormation), nameof(UnitFormation.ReturnToFormation))]
    public static class Patch_UnitFormation_ReturnToFormation
    {
        static void Postfix(ObjectBase obj)
        {
            if (obj != null) FormationSync.Send(obj, FormationSync.Msg(obj, FormationOp.ReturnUnit));
        }
    }

    [HarmonyPatch(typeof(UnitFormation), "set_Name")]
    public static class Patch_UnitFormation_Name
    {
        static void Postfix(UnitFormation __instance, string value)
        {
            var leader = FormationSync.Leader(__instance);
            if (leader == null) return;
            var msg = FormationSync.Msg(leader, FormationOp.Rename);
            msg.AmmoId = value ?? "";
            FormationSync.Send(leader, msg);
        }
    }

    /// <summary>Station drag in the formation (bullseye) editor.
    ///
    /// Filtered on setStationHeight: StationViewModel.UpdateStation - the drag - is
    /// the only caller that leaves it false. The station-keeping callers that would
    /// otherwise flood this (FlightDeck recovery slots, HoldingPattern) all pass true,
    /// and the drift/sprint states do not come through here at all - they use
    /// OffsetStationPosition, which writes a relative waypoint instead.</summary>
    [HarmonyPatch(typeof(UnitFormation), nameof(UnitFormation.ChangeStationPosition),
        new[] { typeof(Station), typeof(Vector3), typeof(bool) })]
    public static class Patch_UnitFormation_ChangeStationPosition
    {
        static void Postfix(UnitFormation __instance, Station station,
                            Vector3 newStationPosition, bool setStationHeight)
        {
            if (setStationHeight || station == null) return;

            var leader = FormationSync.Leader(__instance);
            if (leader == null) return;

            int index = __instance.Stations.IndexOf(station);
            if (index < 0) return;

            var msg = FormationSync.Msg(leader, FormationOp.StationPos);
            msg.Speed = index;
            msg.DestX = newStationPosition.x;
            msg.DestY = newStationPosition.y;
            msg.DestZ = newStationPosition.z;
            FormationSync.Send(leader, msg);
        }
    }
}
