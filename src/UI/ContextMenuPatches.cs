using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using SeapowerUI.ViewModels;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Adds "Send formation to player" to the game's OWN right-click menu, and replaces
    /// that menu entirely for units somebody else commands.
    ///
    /// The menu is a Noesis ContextMenu built imperatively in C# from
    /// <see cref="ContextMenuItem"/>; the game's XAML is baked into Noesis assets and
    /// cannot be edited, so a Postfix appending to the returned collection is the only
    /// way in - and is all that is needed, since a non-null Items makes an entry a
    /// submenu for free.
    ///
    /// Everything this drives already exists: GameEventType.AssignOwnership on the wire,
    /// host-side validation of sender/recipient/team, and
    /// FormationOwnership.HostAssignFormationOf to perform and replicate the move.
    /// </summary>
    internal static class MpContextMenu
    {
        /// <summary>
        /// Build the "Send ... to player" entry, or null when it does not belong on this
        /// menu at all.
        ///
        /// Returning null rather than a disabled entry is deliberate: ContextMenuItem's
        /// IsEnabled is fixed at construction, so there is no way to grey one out
        /// afterwards, and an entry that silently does nothing is worse than no entry.
        /// </summary>
        internal static ContextMenuItem? BuildSendToPlayer(ObjectBase? anchor)
        {
            if (anchor == null || anchor.UniqueID == 0) return null;
            if (!OwnershipRules.LockActive) return null;              // free-for-all: nothing to send
            if (!NetworkManager.Instance.IsEstablished) return null;
            if (SessionManager.SceneLoading) return null;
            if (!FormationOwnership.IsMine(anchor)) return null;      // not yours to give away

            var mine = Teams.LocalTeam;
            var subs = new TrulyObservableCollection<ContextMenuItem>();

            foreach (var p in PlayerRegistry.All)
            {
                if (!p.Connected || !p.Established) continue;
                if (p.Slot == PlayerRegistry.LocalSlot) continue;
                if (p.Team != mine) continue;                         // never hand a fleet to the opposition

                // Copied per iteration: the closure below outlives this loop, and
                // capturing the loop variable would give every entry the last player.
                byte slot = p.Slot;
                int  id   = anchor.UniqueID;

                // Raw-string label ctor, NOT the (section, key) one:
                // LanguageResourceHandler.getText logs an error and returns
                // "MISSING TEXT - [Section]Key" for anything not in the game's own
                // locale files, which ours never will be.
                subs.Add(new ContextMenuItem(p.DisplayName, null, new DelegateCommand(delegate
                {
                    Send(id, slot);
                })));
            }

            if (subs.Count == 0) return null;                         // nobody to send to

            return new ContextMenuItem(
                anchor.Formation != null ? "Send formation to player" : "Send unit to player",
                subs);
        }

        /// <summary>Hand the whole formation over. The host owns the table, so it acts
        /// directly rather than sending itself a message and waiting for it to come
        /// back.</summary>
        private static void Send(int unitId, byte toSlot)
        {
            if (Plugin.Instance.CfgIsHost.Value)
            {
                FormationOwnership.HostAssignFormationOf(StateSerializer.FindById(unitId), toSlot);
                return;
            }

            NetworkManager.Instance.SendToServer(new GameEventMessage
            {
                EventType      = GameEventType.AssignOwnership,
                SourceEntityId = unitId,
                TargetEntityId = 1,      // whole formation (reserved for a future per-unit form)
                Param          = toSlot,
            });
            Plugin.Log.LogInfo($"[Ownership] Requested transfer of {unitId} to slot {toSlot}.");
        }

        /// <summary>
        /// Strip a menu down to a single inert "Controlled by X" row.
        ///
        /// REPLACING is necessary, not merely tidy. Several entries mutate the unit
        /// through captured fields rather than through any patchable method -
        /// UnitFormation's menu writes <c>UnitObject._weaponStatus</c> and the
        /// formation's Name and Spacing directly, and ObjectBaseViewModel's writes
        /// <c>_objectBase._playerCommandOverride</c> - so no order gate can ever see
        /// them. Leaving the menu in place would present a full set of controls, some of
        /// which quietly work on a unit the player does not command.
        ///
        /// <paramref name="keepFirst"/> preserves the leading Information entry on the
        /// per-unit menu, which only opens a reference window and is worth having for a
        /// teammate's ship. The formation menu's first entry is Navigate (waypoint
        /// removal), so it keeps nothing.
        /// </summary>
        internal static bool ReplaceWithReadOnly(ObjectBase? anchor,
                                                 TrulyObservableCollection<ContextMenuItem>? result,
                                                 bool keepFirst)
        {
            if (anchor == null || result == null) return false;
            if (!FormationOwnership.IsOwnedByOther(anchor)) return false;

            ContextMenuItem? keep = keepFirst && result.Count > 0 ? result[0] : null;
            result.Clear();
            if (keep != null) result.Add(keep);

            string owner = FormationOwnership.OwnerName(anchor);
            result.Add(new ContextMenuItem(
                owner.Length > 0 ? $"Controlled by {owner}" : "Controlled by another player"));
            return true;
        }
    }

    /// <summary>The formation marker's own menu (right-click a formation on the map).</summary>
    [HarmonyPatch(typeof(UnitFormation), nameof(UnitFormation.GetContextMenuItems))]
    public static class Patch_UnitFormation_ContextMenu
    {
        static void Postfix(UnitFormation __instance,
                            TrulyObservableCollection<ContextMenuItem> __result)
        {
            if (__result == null) return;

            var anchor = __instance?.LeaderStation?.UnitObject;
            if (anchor == null) return;

            // Nothing else belongs on a menu we have just replaced.
            if (MpContextMenu.ReplaceWithReadOnly(anchor, __result, keepFirst: false)) return;

            var item = MpContextMenu.BuildSendToPlayer(anchor);
            // APPENDED, never inserted at an index. The game's own entries shift with
            // every update; appending is the one position that cannot go stale, and the
            // bottom is where a mod-added action belongs anyway.
            if (item != null) __result.Add(item);
        }
    }

    /// <summary>The per-unit menu (right-click a unit, or a unit in a formation).</summary>
    [HarmonyPatch(typeof(ObjectBaseViewModel), nameof(ObjectBaseViewModel.GetContextMenuItems))]
    public static class Patch_ObjectBaseViewModel_ContextMenu
    {
        static void Postfix(ObjectBaseViewModel __instance,
                            TrulyObservableCollection<ContextMenuItem> __result)
        {
            if (__result == null) return;

            var anchor = __instance?._objectBase;
            if (anchor == null) return;

            if (MpContextMenu.ReplaceWithReadOnly(anchor, __result, keepFirst: true)) return;

            var item = MpContextMenu.BuildSendToPlayer(anchor);
            if (item != null) __result.Add(item);
        }
    }

    /// <summary>
    /// Drop formations the player does not own from "Join Formation".
    ///
    /// Stops a mixed-owner formation arising from the UI at all, which is what the
    /// one-owner-per-formation invariant rests on - HostNormaliseFormation is the
    /// backstop for the scripted and AI paths, but a player should not be offered a
    /// join that will immediately be normalised away from them.
    /// </summary>
    [HarmonyPatch(typeof(ObjectBaseViewModel), nameof(ObjectBaseViewModel.GetJoinableFormationItems))]
    public static class Patch_ObjectBaseViewModel_JoinableFormations
    {
        static void Postfix(ObjectBaseViewModel __instance,
                            TrulyObservableCollection<ContextMenuItem> __result)
        {
            if (__result == null || __result.Count == 0) return;
            if (!OwnershipRules.LockActive) return;
            if (!NetworkManager.Instance.IsEstablished) return;

            var unit = __instance?._objectBase;
            if (unit?._taskforce?.Formations == null) return;

            // Matched by NAME, not by index. The game's list is filtered by unit type
            // and excludes the unit's own formation, so entry N is not formation N -
            // index alignment would remove the wrong ones. The labels are the formation
            // names verbatim (raw-string ctor, not localized), which is the only link
            // back from an entry to its formation.
            //
            // Two formations sharing a name would both be hidden if either is foreign.
            // Renaming to a duplicate is possible but unusual, and erring towards hiding
            // is the safe direction: the cost is a join the player has to set up another
            // way, against offering one that would be normalised away from them.
            foreach (var formation in unit._taskforce.Formations)
            {
                var leader = formation?.LeaderStation?.UnitObject;
                if (leader == null) continue;
                if (!FormationOwnership.IsOwnedByOther(leader)) continue;

                string name = formation!.Name ?? "";
                for (int i = __result.Count - 1; i >= 0; i--)
                {
                    if (__result[i]?.Label as string == name) __result.RemoveAt(i);
                }
            }
        }
    }
}
