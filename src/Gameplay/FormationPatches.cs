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

    /// <summary>
    /// Raised around formation internals whose waypoint churn is DERIVED state - both
    /// machines compute it identically from the same membership - rather than anything
    /// a player asked for. While it is up, OrderSyncHelper still lets the call execute
    /// locally but sends nothing, in either direction.
    ///
    /// Depth-counted like <see cref="Authority"/>, and raised from a Prefix/Finalizer
    /// pair rather than Prefix/Postfix so a throw inside the game method cannot strand
    /// the flag up and silence every order after it.
    /// </summary>
    internal static class FormationInternal
    {
        private static int _depth;

        internal static bool Active => _depth > 0;
        internal static void Enter() => _depth++;
        internal static void Exit()  { if (_depth > 0) _depth--; }
    }

    /// <summary>Raised for the duration of <see cref="UnitFormation.OnUpdate"/>, purely
    /// so the calls it makes can be told apart from the ones a player makes. Nothing is
    /// suppressed by this flag on its own - see
    /// <see cref="Patch_UnitFormation_ReturnToFormation"/>, its only reader.
    ///
    /// Deliberately NOT wired to <see cref="FormationInternal"/> wholesale: OnUpdate also
    /// reaches the AI radar sweep (CheckAutomation → CheckForRadarScan) and unit launch
    /// (CheckAI → CheckForUnitLaunch), which are host decisions the client does have to
    /// be told about.</summary>
    internal static class FormationUpdate
    {
        private static int _depth;

        internal static bool Active => _depth > 0;
        internal static void Enter() => _depth++;
        internal static void Exit()  { if (_depth > 0) _depth--; }
    }

    [HarmonyPatch(typeof(UnitFormation), nameof(UnitFormation.OnUpdate))]
    public static class Patch_UnitFormation_OnUpdate
    {
        static void Prefix()    => FormationUpdate.Enter();
        static void Finalizer() => FormationUpdate.Exit();
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

    /// <summary>Relay a unit's return to station - but only when the call actually did
    /// something.
    ///
    /// ReturnToFormation returns without touching the unit if the formation has no
    /// leader, or if the unit has no station in it, and a postfix runs on those paths
    /// too. Relaying a no-op is not merely wasteful here, it is unbounded: OnUpdate calls
    /// ReturnToFormation for every follower that has no waypoints, and the early return
    /// leaves it with none, so the condition still holds on the next frame. A formation
    /// whose leader has been destroyed and not yet replaced therefore re-sent ReturnUnit
    /// for every follower EVERY FRAME - on a path that is exempt from order dedup and has
    /// no rate floor, reliable-ordered, with an unthrottled log line per message at the
    /// far end, and a RemoveWaypoints + re-add on the receiver each time.
    ///
    /// Both conditions are read after the call because the method mutates neither, so
    /// this needs no prefix to capture them first.
    ///
    /// STATION KEEPING IS NOT AN ORDER. UnitFormation.OnUpdate calls ReturnToFormation
    /// once a frame for every follower that has run out of waypoints, and that is derived
    /// state in the FormationInternal sense: both machines hold the same membership and
    /// the same station positions, so both re-issue the station task unprompted. Relaying
    /// it was an unbounded per-frame send on a path with no dedup and no rate floor -
    /// and worse, self-sustaining, because ReturnToFormation opens with RemoveWaypoints,
    /// which IS relayed while the SetRelativeWaypointTask that follows it is not. The
    /// clear and the re-add travel as two separate reliable messages, so any frame in
    /// which the receiver drains the clear without its companion re-add leaves that
    /// follower waypointless - re-arming the same watchdog on the far side, which fires
    /// the pair straight back. One waypoint completion was enough to start it and nothing
    /// stopped it: a live session logged a solid run of ReturnUnit for a single destroyer,
    /// reliable-ordered, with an unthrottled log line and a RemoveWaypoints + re-add per
    /// message at the far end, and both players at ~1 FPS.
    ///
    /// So the OnUpdate-driven call is executed and sent NOTHING, in either direction -
    /// FormationInternal covers the whole call, silencing its RemoveWaypoints too, not
    /// just the ReturnUnit below. Every other caller is a discrete one-shot (the station
    /// context menu, a route that finished, a formation cease-fire-and-recall) and still
    /// travels.</summary>
    [HarmonyPatch(typeof(UnitFormation), nameof(UnitFormation.ReturnToFormation))]
    public static class Patch_UnitFormation_ReturnToFormation
    {
        static void Prefix(ref bool __state)
        {
            __state = FormationUpdate.Active;
            if (__state) FormationInternal.Enter();
        }

        static void Postfix(UnitFormation __instance, ObjectBase obj, bool __state)
        {
            if (__state) return; // station keeping - derived, see above
            if (obj == null) return;
            if (__instance.LeaderStation?.UnitObject == null) return;
            if (__instance.GetStationForUnit(obj) == null) return;

            FormationSync.Send(obj, FormationSync.Msg(obj, FormationOp.ReturnUnit));
        }

        // Finalizer, not the Postfix, so a throw inside the game method cannot strand
        // FormationInternal up and silence every order after it.
        static void Finalizer(bool __state)
        {
            if (__state) FormationInternal.Exit();
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

    /// <summary>Temporary station offsets - the sprint-and-drift ASW cycle
    /// (FormationSprint / FormationDrift / FormationClearBafflesListening all offset
    /// their station on onEnter, so a towed-array ship can run ahead and then coast
    /// quietly behind the group).
    ///
    /// Unlike ChangeStationPosition this leaves the station itself alone and writes a
    /// relative-to-station waypoint, and SetRelativeToStationWaypointTask is not a
    /// patched call - so the RemoveWaypoints it opens with travelled and the task that
    /// replaced it did not. The other side was left holding a follower with no
    /// waypoints at all, which its own UnitFormation.OnUpdate answers by calling
    /// ReturnToFormation - sending back a RemoveWaypoints + ReturnUnit pair that
    /// overwrote the offset with a plain station task. One machine's ASW state and the
    /// other machine's station keeping then took turns undoing each other.
    ///
    /// The opening RemoveWaypoints still travels; this rides behind it, exactly as
    /// ReturnToFormation's ReturnUnit does, so the receiving side clears and then
    /// re-offsets in the same drain.</summary>
    [HarmonyPatch(typeof(UnitFormation), nameof(UnitFormation.OffsetStationPosition))]
    public static class Patch_UnitFormation_OffsetStationPosition
    {
        static void Postfix(UnitFormation __instance, Station station, Vector3 offset,
                            bool setStationHeight, bool reachable)
        {
            if (station == null) return;

            var leader = FormationSync.Leader(__instance);
            if (leader == null) return;

            int index = __instance.Stations.IndexOf(station);
            if (index < 0) return;

            var msg = FormationSync.Msg(leader, FormationOp.StationOffset);
            msg.Speed   = index;
            msg.DestX   = offset.x;
            msg.DestY   = offset.y;
            msg.DestZ   = offset.z;
            msg.Heading = (setStationHeight ? 1 : 0) | (reachable ? 2 : 0);
            FormationSync.Send(leader, msg);
        }
    }

    /// <summary>Automatic leader reassignment - the leader died or started sinking
    /// (UnitFormation.OnUpdate → UnitOnStationNotValid → here, and again from
    /// DetachUnit). Nothing it does may travel.
    ///
    /// It is not a player order and not a decision either machine has to be told
    /// about: both run it off the same station list, in the same order, against a
    /// death both already know about, so both pick the same new leader unprompted.
    /// What they did NOT do the same way was the waypoint handover. The two
    /// RemoveWaypoints calls (the new leader dropping its station keeping, the old one
    /// picking it up) are patched and went on the wire; the CopyWaypointsFrom between
    /// them, which is what actually moves the dead leader's route onto its
    /// replacement, is not patched and stayed local. So each side cleared what the
    /// other had just copied, and a formation that lost its leader came out of it with
    /// no route on either machine.
    ///
    /// Suppressing the send fixes it in both directions at once: each machine keeps
    /// its own handover, which is the same handover.</summary>
    [HarmonyPatch(typeof(UnitFormation), "AssignNewLeaderFromAvailableUnits")]
    public static class Patch_UnitFormation_AssignNewLeader
    {
        static void Prefix()    => FormationInternal.Enter();
        static void Finalizer() => FormationInternal.Exit();
    }
}
