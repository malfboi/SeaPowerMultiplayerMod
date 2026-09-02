using System.Collections.Generic;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>Whether the session hands each player their own formations, or lets any
    /// teammate command anything on their side.</summary>
    internal static class OwnershipRules
    {
        private static bool _lockActive;

        /// <summary>
        /// Host reads its own config; a client holds whatever the host published.
        ///
        /// Not read straight from config on both ends, because a client's own setting is
        /// meaningless here - it is the HOST's session and one machine disagreeing about
        /// whether ownership applies is the one outcome that must not be possible.
        /// </summary>
        public static bool LockActive
        {
            get => Plugin.Instance.CfgIsHost.Value ? Plugin.Instance.CfgLockUnits.Value : _lockActive;
            set => _lockActive = value;
        }
    }

    /// <summary>
    /// Which player commands which units.
    ///
    /// KEYED PER UNIT, not per formation, even though the player-facing gesture is
    /// always "send this formation". Formations carry no id of their own, leaders swap
    /// and die, and a formation can be disbanded or merged mid-battle - so anything
    /// keyed on a formation needs re-keying at each of those moments. Storing
    /// <c>unitId -&gt; slot</c> makes leader swap, auto-leader reassignment, disband and
    /// detach all non-events, and makes a lone unit the same case as a formation member
    /// rather than a special one. GuestIdFloor already guarantees unit ids are unique
    /// across every machine, so an int key is safe.
    ///
    /// Host-authoritative. The host owns the table and replicates it verbatim; a client
    /// only ever applies what it is told.
    /// </summary>
    public static class FormationOwnership
    {
        /// <summary>Nobody owns this unit - anyone on its team may command it.</summary>
        public const byte Unowned = 255;

        // Only OWNED units appear. Absence means Unowned, so a session with the lock off
        // keeps this empty and every predicate below short-circuits on the first line.
        private static readonly Dictionary<int, byte> _owner = new();

        private static readonly List<(int, byte)> _pendingDelta = new();

        public static int Epoch { get; private set; }

        // ── Queries (every machine) ───────────────────────────────────────────

        public static byte OwnerOf(int unitId)
            => _owner.TryGetValue(unitId, out var slot) ? slot : Unowned;

        public static byte OwnerOf(ObjectBase? unit)
            => unit == null ? Unowned : OwnerOf(unit.UniqueID);

        public static bool IsMine(ObjectBase? unit)
            => unit != null && OwnerOf(unit.UniqueID) == PlayerRegistry.LocalSlot;

        /// <summary>Owned by another player ON MY TEAM - the predicate that drives ally
        /// rendering and the "[name]" tag.
        ///
        /// The team test is not decoration. The ownership table covers BOTH sides, so
        /// without it an opponent's units answered this too, and every display built on
        /// it named them: enemy contacts were labelled with the enemy player's name and
        /// their right-click menu read "Controlled by &lt;name&gt;". That hands over both
        /// who is behind a contact and which contacts are the human's, which is exactly
        /// what an opponent should have to work out.
        ///
        /// Semantically it is also what the name says everywhere it is used - an
        /// opponent's ship is not an ally, so it should not get ally treatment.</summary>
        public static bool IsOwnedByOther(ObjectBase? unit)
        {
            if (unit == null) return false;
            if (!OwnershipRules.LockActive) return false;
            if (!Teams.IsFriendly(unit)) return false;
            byte o = OwnerOf(unit.UniqueID);
            return o != Unowned && o != PlayerRegistry.LocalSlot;
        }

        public static string OwnerName(ObjectBase? unit)
        {
            byte o = OwnerOf(unit);
            return o == Unowned ? "" : PlayerRegistry.DisplayName(o);
        }

        /// <summary>
        /// THE gate: must this machine refuse an order for this unit?
        ///
        /// Note there is no CfgIsHost test. The host is a player now and has to be
        /// refused for its own local input exactly like anyone else - that role symmetry
        /// is the whole point, and every earlier version of unit locking exempted the
        /// host and was wrong for it.
        /// </summary>
        public static bool BlocksOrdersFor(ObjectBase? unit)
        {
            if (unit == null) return false;
            // Weapons are never owned, and must never be REFUSED - the same exemption
            // OrderSyncHelper.Prefix makes, asserted here so the callers that ask this
            // directly rather than going through OrderSyncHelper inherit it. The
            // submarine depth patch is one of those, and without it a wire-guided
            // torpedo could not be steered by its own owner. (Ported from 0.3.7's
            // "fix controllable torps", which made the same assertion in the transient
            // lock this predicate replaced. Ownership is never granted to a WeaponBase,
            // so today this is a backstop rather than a live fix - which is exactly why
            // it is worth keeping.)
            if (unit is WeaponBase) return false;
            if (!OwnershipRules.LockActive) return false;         // free-for-all
            if (OrderHandler.ApplyingFromNetwork) return false;   // somebody else's authorised order
            if (Authority.IsAllowed) return false;                // host-authoritative replay
            if (SessionManager.SceneLoading) return false;        // never filter a restore
            if (!NetworkManager.Instance.IsEstablished) return false;

            byte o = OwnerOf(unit.UniqueID);
            return o != Unowned && o != PlayerRegistry.LocalSlot;
        }

        /// <summary>Host: was this slot entitled to send that order? What makes ownership
        /// authoritative rather than a politely-observed convention.</summary>
        public static bool MaySend(byte slot, int unitId)
        {
            if (!OwnershipRules.LockActive) return true;
            if (slot == PlayerRegistry.NoSender) return true;   // host's own code
            byte o = OwnerOf(unitId);
            return o == Unowned || o == slot;
        }

        // ── Host mutations ────────────────────────────────────────────────────

        /// <summary>
        /// The first player to take a side receives everything on it that nobody else
        /// holds.
        ///
        /// "Unowned", not "all", deliberately: a player who drops and rejoins must not
        /// take back formations that were already redistributed to the teammate who
        /// stayed.
        /// </summary>
        public static void HostGrantTeamTo(Team team, byte slot)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (!OwnershipRules.LockActive) return;

            var tf = Teams.TaskforceFor(team);
            if (tf == null) return;

            _pendingDelta.Clear();
            foreach (var unit in tf.TaskforceObjects)
            {
                if (unit == null || unit.UniqueID == 0) continue;
                if (unit is WeaponBase) continue;
                if (OwnerOf(unit.UniqueID) != Unowned) continue;
                _owner[unit.UniqueID] = slot;
                _pendingDelta.Add((unit.UniqueID, slot));
            }

            if (_pendingDelta.Count == 0)
            {
                Plugin.Log.LogInfo($"[Ownership] {PlayerRegistry.DisplayName(slot)} took nothing on " +
                                   $"{Teams.Name(team)} - {tf.TaskforceObjects.Count} object(s) there, " +
                                   $"{_owner.Count} already owned, the rest not ownable (weapons, or no id yet). " +
                                   "(Expected for anyone but the first player on a side.)");
                return;
            }
            Epoch++;
            Plugin.Log.LogInfo($"[Ownership] {PlayerRegistry.DisplayName(slot)} took {_pendingDelta.Count} unit(s) on {Teams.Name(team)}.");
            HostBroadcastDelta();
            NotifyChanged();
        }

        /// <summary>Transfer a whole formation (or a lone unit) to another player.</summary>
        public static void HostAssignFormationOf(ObjectBase? anchor, byte slot)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (anchor == null) return;

            _pendingDelta.Clear();
            foreach (int id in MembersOf(anchor))
            {
                if (OwnerOf(id) == slot) continue;
                _owner[id] = slot;
                _pendingDelta.Add((id, slot));
            }

            if (_pendingDelta.Count == 0) return;
            Epoch++;
            Plugin.Log.LogInfo($"[Ownership] {_pendingDelta.Count} unit(s) → {PlayerRegistry.DisplayName(slot)}.");
            HostBroadcastDelta();
            NotifyChanged();
        }

        /// <summary>
        /// A player left: hand their units to the longest-present remaining teammate, or
        /// release them if there is nobody left on that side.
        /// </summary>
        public static void HostReleaseSlot(byte slot)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (slot == PlayerRegistry.NoSender) return;

            var heir = PlayerRegistry.FirstRemainingTeammate(slot);
            byte to = heir?.Slot ?? Unowned;

            _pendingDelta.Clear();
            foreach (var kv in _owner)
                if (kv.Value == slot) _pendingDelta.Add((kv.Key, to));

            if (_pendingDelta.Count == 0) return;

            foreach (var (id, newOwner) in _pendingDelta)
            {
                if (newOwner == Unowned) _owner.Remove(id);
                else _owner[id] = newOwner;
            }

            Epoch++;
            Plugin.Log.LogInfo(to == Unowned
                ? $"[Ownership] Slot {slot} left; {_pendingDelta.Count} unit(s) released."
                : $"[Ownership] Slot {slot} left; {_pendingDelta.Count} unit(s) → {PlayerRegistry.DisplayName(to)}.");
            HostBroadcastDelta();
            NotifyChanged();
        }

        /// <summary>
        /// A unit joined a formation: give it the LEADER's owner.
        ///
        /// Keeps the one-owner-per-formation invariant that the whole gesture depends on -
        /// "send this formation to Bob" has no meaning if half of it belongs to someone
        /// else.
        /// </summary>
        public static void HostNormaliseFormation(UnitFormation? formation)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (!OwnershipRules.LockActive) return;

            var leader = formation?.LeaderStation?.UnitObject;
            if (leader == null) return;
            byte owner = OwnerOf(leader.UniqueID);
            if (owner == Unowned) return;

            HostAssignFormationOf(leader, owner);
        }

        /// <summary>A unit spawned mid-mission (an aircraft off a deck, a launched boat):
        /// it belongs to whoever owns the thing that launched it.</summary>
        public static void HostOnUnitSpawned(ObjectBase? child, ObjectBase? parent)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (!OwnershipRules.LockActive) return;
            if (child == null || child.UniqueID == 0) return;

            byte owner = OwnerOf(parent);
            if (owner == Unowned) return;               // unresolvable parent: leave it free
            if (OwnerOf(child.UniqueID) == owner) return;

            _owner[child.UniqueID] = owner;
            Epoch++;
            _pendingDelta.Clear();
            _pendingDelta.Add((child.UniqueID, owner));
            HostBroadcastDelta();
            NotifyChanged();
        }

        // ── Replication ───────────────────────────────────────────────────────

        private static void HostBroadcastDelta()
        {
            if (_pendingDelta.Count == 0) return;
            var msg = new UnitOwnershipMessage { Full = false, LockActive = OwnershipRules.LockActive, Epoch = Epoch };
            msg.Entries.AddRange(_pendingDelta);
            NetworkManager.Instance.BroadcastToClients(msg);
        }

        /// <summary>
        /// Host: send the whole table.
        ///
        /// TIMING MATTERS. Unit ids mean nothing to a client until its scene exists, and
        /// an empty table does not read as "not yet" - it reads as "nothing is owned",
        /// i.e. everything is commandable. So this goes out only once a client is
        /// Synchronized, and again after any hard resync.
        /// </summary>
        public static void HostSendFull(byte toSlot = PlayerRegistry.NoSender)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;

            var msg = new UnitOwnershipMessage { Full = true, LockActive = OwnershipRules.LockActive, Epoch = Epoch };
            foreach (var kv in _owner) msg.Entries.Add((kv.Key, kv.Value));

            if (toSlot == PlayerRegistry.NoSender) NetworkManager.Instance.BroadcastToClients(msg);
            else NetworkManager.Instance.SendToSlot(toSlot, msg);

            Plugin.Log.LogInfo($"[Ownership] Sent full table ({msg.Entries.Count} entries, lock={msg.LockActive}) " +
                               $"to {(toSlot == PlayerRegistry.NoSender ? "everyone" : $"slot {toSlot}")}.");
        }

        /// <summary>Client: fold a host packet into the table.</summary>
        public static void ApplySnapshot(UnitOwnershipMessage msg)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            OwnershipRules.LockActive = msg.LockActive;
            Epoch = msg.Epoch;

            if (msg.Full) _owner.Clear();

            foreach (var (id, slot) in msg.Entries)
            {
                if (slot == Unowned) _owner.Remove(id);
                else _owner[id] = slot;
            }

            // A full table can touch every unit on the map, so re-label the lot rather
            // than trying to work out which ones moved.
            Plugin.Log.LogInfo($"[Ownership] Applied {(msg.Full ? "FULL" : "delta")} table: " +
                               $"{msg.Entries.Count} entries, lock={msg.LockActive}, epoch={msg.Epoch}; " +
                               $"now holding {_owner.Count} owned unit(s), my slot={PlayerRegistry.LocalSlot}.");

            if (msg.Full) MapUnitViewModelRegistry.NotifyAll();
            else foreach (var (id, _) in msg.Entries) MapUnitViewModelRegistry.NotifyOwnershipChanged(id);
        }

        private static void NotifyChanged()
        {
            foreach (var (id, _) in _pendingDelta)
                MapUnitViewModelRegistry.NotifyOwnershipChanged(id);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Every unit the gesture acts on: a whole formation when the anchor is
        /// in one, otherwise just the anchor.</summary>
        private static IEnumerable<int> MembersOf(ObjectBase anchor)
        {
            var formation = anchor.Formation;
            if (formation?.Stations == null)
            {
                if (anchor.UniqueID != 0) yield return anchor.UniqueID;
                yield break;
            }

            bool any = false;
            foreach (var station in formation.Stations)
            {
                var u = station?.UnitObject;
                if (u == null || u.UniqueID == 0) continue;
                any = true;
                yield return u.UniqueID;
            }
            if (!any && anchor.UniqueID != 0) yield return anchor.UniqueID;
        }

        public static void Reset()
        {
            _owner.Clear();
            _pendingDelta.Clear();
            Epoch = 0;
            OwnershipRules.LockActive = false;
        }
    }
}
