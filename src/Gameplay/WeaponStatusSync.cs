using System.Collections.Generic;
using SeaPower;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Weapon status (doctrine) relay for the paths that do not go through
    /// <see cref="ObjectBase.SetWeaponStatus"/> in a form the order patch can see.
    ///
    /// The patch on SetWeaponStatus catches a single unit's change and nothing else,
    /// because every group-level path writes the field directly instead:
    ///
    ///  - SetWeaponStatus(status, formationCheck: true) on a Synchronized formation (or a
    ///    FollowLeader leader) writes station.UnitObject._weaponStatus for every member and
    ///    returns, so one click on six ships produced ONE message - and the apply side
    ///    re-issues with formationCheck:false, so even that one reached the leader alone.
    ///  - The formation context menu's Weapons Free/Tight/Hold entries loop the stations
    ///    writing the field, never calling SetWeaponStatus at all.
    ///  - Taskforce.SetWeaponStatus does the same across the whole taskforce.
    ///
    /// A sweep rather than three patches: the context-menu case is a compiler-generated
    /// lambda with no stable method to hook, so it needs one regardless - and once it
    /// exists it covers the other two, plus whatever a future game update adds.
    ///
    /// Re-issuing through SetWeaponStatus(value, false) rather than sending a message
    /// directly: the value is already set, so the call is a local no-op, but it rides the
    /// existing relay with its ownership gates and direction handling intact - Prefix
    /// carries a client's change upstream, Postfix broadcasts a host's.
    ///
    /// Runs on BOTH machines, over the local player's own fleet only.
    /// </summary>
    internal static class WeaponStatusSync
    {
        private static readonly Dictionary<int, ObjectBase.WeaponStatus> _lastSeen = new(128);

        internal static void Reset() => _lastSeen.Clear();

        /// <summary>Record a value without relaying it - used when the change arrived over
        /// the network, so the sweep does not read it back as a local action and bounce it
        /// to the machine it came from.</summary>
        internal static void NoteApplied(ObjectBase unit)
        {
            if (unit != null && unit.UniqueID != 0)
                _lastSeen[unit.UniqueID] = unit._weaponStatus;
        }

        internal static void Sweep()
        {
            var playerTf = Globals._playerTaskforce;
            if (playerTf == null) return;

            var units = UnitRegistry.All;
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null || unit.UniqueID == 0 || unit.IsDestroyed) continue;
                if (!unit.isUnit()) continue;
                if (unit._taskforce != playerTf) continue;

                var status = unit._weaponStatus;
                if (_lastSeen.TryGetValue(unit.UniqueID, out var prev) && prev == status) continue;
                _lastSeen[unit.UniqueID] = status;

                // Local no-op that carries the value onto the wire. First sight sends too:
                // that is what puts each side's real doctrine on the other machine once the
                // session is up, rather than leaving it at whatever the save's spawn stamp
                // decided.
                unit.SetWeaponStatus(status, false);
            }
        }
    }
}
