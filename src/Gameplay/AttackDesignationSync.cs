using System.Collections.Generic;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// "Attack that contact" - the plain designation a player makes by clicking a track,
    /// as opposed to the "Engage with «weapon»" menu (which builds an explicit EngageTask
    /// and already syncs as OrderType.FireWeapon).
    ///
    /// The designation is not a method call. Every path that makes one - the click
    /// handler, the ship and formation target-distribution paths - writes
    /// <c>_ai._objectToDestroy</c> and <c>_ai._objectsToDestroyList</c> as raw fields, so
    /// there is nothing to patch and nothing was carrying it. On the client the write
    /// landed in an AI this plugin has already switched off (AI.OnFixedUpdate is
    /// suppressed for ClientActive), and the host - which runs that unit - was never
    /// told. The order simply evaporated between the two machines.
    ///
    /// A SWEEP rather than patches on the write sites: there are a dozen writers spread
    /// across the UI and the AI, only some of them player-driven, and the ones that
    /// matter are field writes buried mid-method. Diffing the field itself catches every
    /// path including ones added by a future game update, and - just as important - it
    /// catches the CLEAR, which a player uses as much as the set.
    ///
    /// Polling is cheap and quiet here precisely BECAUSE the client's AI is suppressed:
    /// nothing but the player's own UI moves this field client-side, so a change means a
    /// player action every time.
    /// </summary>
    internal static class AttackDesignationSync
    {
        /// <summary>Client: last target id relayed per unit. 0 means "no designation",
        /// which is a value like any other - clearing has to travel too.</summary>
        private static readonly Dictionary<int, int> _lastSent = new(128);

        /// <summary>Host: the target THIS path put on each unit, so a later change can
        /// take the old one back out of _objectsToDestroyList without clearing entries
        /// the host's own taskforce automation put there.</summary>
        private static readonly Dictionary<int, ObjectBase> _applied = new(128);

        internal static void Reset()
        {
            _lastSent.Clear();
            _applied.Clear();
        }

        // ── Client: detect and relay ────────────────────────────────────────────

        internal static void ClientSweep()
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            var units = UnitRegistry.All;
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null || unit.UniqueID == 0 || unit.IsDestroyed) continue;
                if (!unit.isUnit()) continue;
                // Units we do not own are host-driven replicas; a designation on one is
                // our own local sim talking, not the player.
                if (Suppression.ClientForeignUnit(unit)) continue;

                var ai = unit._ai;
                if (ai == null) continue;

                var target = ai._objectToDestroy;
                int targetId = (target != null && !target.IsDestroyed && target.UniqueID != 0)
                    ? target.UniqueID : 0;

                if (_lastSent.TryGetValue(unit.UniqueID, out int prev) && prev == targetId) continue;
                _lastSent[unit.UniqueID] = targetId;

                NetworkManager.Instance.SendToServer(new PlayerOrderMessage
                {
                    SourceEntityId = unit.UniqueID,
                    Order          = OrderType.AttackTarget,
                    TargetEntityId = targetId,
                });

                Plugin.Log.LogInfo($"[Attack] Designation relayed: unit={unit.UniqueID} target={targetId}");
            }
        }

        // ── Host: apply ─────────────────────────────────────────────────────────

        /// <summary>Called from OrderHandler.Apply under ApplyingFromNetwork.</summary>
        internal static void Apply(ObjectBase unit, PlayerOrderMessage msg)
        {
            var ai = unit._ai;
            if (ai == null) return;

            // Take out whatever this path put there last, and only that - the host's own
            // taskforce automation writes into the same list.
            if (_applied.TryGetValue(unit.UniqueID, out var previous) && previous != null)
                ai._objectsToDestroyList.Remove(previous);
            _applied.Remove(unit.UniqueID);

            if (msg.TargetEntityId == 0)
            {
                ai._objectToDestroy = null;
                Plugin.Log.LogInfo($"[Attack] Designation cleared: unit={unit.UniqueID}");
                return;
            }

            var target = StateSerializer.FindById(msg.TargetEntityId);
            if (target == null || target.IsDestroyed)
            {
                Plugin.Log.LogWarning($"[Attack] Designation dropped: unit={unit.UniqueID} " +
                                      $"target={msg.TargetEntityId} not found");
                return;
            }

            // Same reason the FireWeapon case marks the pair: AI.IsProcessed drops a
            // target the shooter's own crew never worked up, so without this a ship or
            // submarine sits on the designation forever. Air units short-circuit that
            // check, which is why an aircraft order survives without it.
            Patch_ObjectBase_HandleEngageTasks.MarkNetworkOrderedTarget(unit.UniqueID, target.UniqueID);

            ai._objectToDestroy = target;
            if (!ai._objectsToDestroyList.Contains(target))
                ai._objectsToDestroyList.Add(target);
            _applied[unit.UniqueID] = target;

            Plugin.Log.LogInfo($"[Attack] Designation applied: unit={unit.UniqueID} target={target.UniqueID}");
        }
    }
}
