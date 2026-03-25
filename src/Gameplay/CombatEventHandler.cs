using System.Collections.Generic;
using BepInEx.Logging;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Applies host-authoritative combat events on the client.
    /// Host determines all interception, damage, and destruction outcomes;
    /// client suppresses local combat RNG and applies these events instead.
    /// </summary>
    public static class CombatEventHandler
    {
        /// <summary>True while applying a combat event from network. Guards Harmony patches.</summary>
        internal static bool ApplyingFromNetwork;

        // ── Incremental dead-unit tracking (D2) ─────────────────────────────
        // Instead of scanning all scene objects each kill-resync tick, we maintain
        // a set of own-side unit IDs confirmed dead and iterate that instead.
        private static readonly HashSet<int> _confirmedDeadOwnUnits = new();

        /// <summary>Record a dead own-side unit for periodic kill resync.</summary>
        internal static void TrackDeadUnit(int unitId) => _confirmedDeadOwnUnits.Add(unitId);

        /// <summary>Clear the dead-unit tracking set (call on scene load/reset).</summary>
        internal static void ClearDeadUnitTracking() => _confirmedDeadOwnUnits.Clear();

        // ── Stats (read by UI) ──────────────────────────────────────────────

        public static int EventsReceived { get; private set; }
        public static int EventsNotFound { get; private set; }
        public static void ResetCounters() { EventsReceived = 0; EventsNotFound = 0; }

        // ── PvP: delayed projectile destruction ─────────────────────────────
        // The attacker's "missile died" arrives before the missile reaches us.
        // Delay destruction so the local sim has time to resolve impact/damage.
        private const float PvpDestroyDelay = 3f;
        private static readonly Queue<(int id, float destroyAt)> _delayedDestroys = new();

        // ── PvP: deferred unit-death watch ───────────────────────────────────
        // When an enemy weapon hits our unit (OnHitUnit) without immediately killing it,
        // we send MissileImpact but still need to notify the host when the unit
        // eventually dies (e.g. via Compartments.DestroyByExplosion on next few frames).
        // notifyOfExternalDestruction is not reliable for compartment-based units, so
        // we poll IsDestroyed / _externalDestructionNotified each frame instead.
        private const float DeathWatchTimeout = 60f;
        private static readonly Dictionary<int, float> _deathWatch = new(); // unitId → watchStartTime
        private static readonly List<int> _deathWatchRemove = new();

        // ── PvP: periodic kill resync ────────────────────────────────────────
        // Re-broadcasts UnitDestroyed for all dead own-side units every N seconds.
        // Ensures the remote machine catches any kills it missed (e.g. radar dying
        // between hard syncs, or a delayed delivery that was never processed).
        private const float KillResyncInterval = 5f;
        private static float _lastKillResync = 0f;

        internal static void WatchForDeath(int unitId)
        {
            if (!_deathWatch.ContainsKey(unitId))
                _deathWatch[unitId] = Time.unscaledTime;
        }

        internal static void ClearDeathWatch()
        {
            _deathWatch.Clear();
            _lastKillResync = 0f;
            _confirmedDeadOwnUnits.Clear();
        }

        /// <summary>Called from Plugin.Update() each frame.</summary>
        internal static void Tick()
        {
            float now = Time.unscaledTime;

            // Delayed projectile destroys
            while (_delayedDestroys.Count > 0)
            {
                var (id, destroyAt) = _delayedDestroys.Peek();
                if (now < destroyAt) break;
                _delayedDestroys.Dequeue();
                var obj = StateSerializer.FindById(id);
                if (obj == null || obj.IsDestroyed) continue;

                RunAsNetworkEvent(() => obj.notifyOfExternalDestruction());
                Log.LogDebug($"[Combat] PvP delayed destroy fired for {id}");
            }

            if (!Plugin.Instance.CfgPvP.Value || !NetworkManager.Instance.IsConnected || SessionManager.SceneLoading)
                return;

            // Death watch: send UnitDestroyed when a watched unit finally dies
            if (_deathWatch.Count > 0)
            {
                _deathWatchRemove.Clear();
                foreach (var kvp in _deathWatch)
                {
                    if (now - kvp.Value > DeathWatchTimeout)
                    {
                        _deathWatchRemove.Add(kvp.Key);
                        continue;
                    }
                    var unit = StateSerializer.FindById(kvp.Key);
                    if (unit == null || unit.IsDestroyed || unit._externalDestructionNotified)
                    {
                        // Send UnitDestroyed regardless — unit==null means it was removed from the
                        // registry by Compartments.DestroyByExplosion after being hit by enemy fire.
                        CombatSyncHelper.Send(new CombatEventMessage
                        {
                            EventType      = CombatEventType.UnitDestroyed,
                            TargetEntityId = kvp.Key,
                            SourceEntityId = 0,
                        });
                        // Track for incremental kill resync
                        TrackDeadUnit(kvp.Key);
                        if (unit != null)
                        {
                            // Silence sensors so radar stops being a target immediately.
                            DisableSensors(unit);
                            Log.LogInfo($"[Combat] PvP WatchForDeath: unit {kvp.Key} ({unit.name}) died — sent UnitDestroyed");
                        }
                        else
                        {
                            Log.LogInfo($"[Combat] PvP WatchForDeath: unit {kvp.Key} missing from registry (destroyed via compartments) — sent UnitDestroyed");
                        }
                        _deathWatchRemove.Add(kvp.Key);
                    }
                }
                foreach (var id in _deathWatchRemove) _deathWatch.Remove(id);
            }

            // Periodic kill resync: re-broadcast all confirmed dead own-side units so
            // the remote machine catches any UnitDestroyed it may have missed.
            // Uses the incremental _confirmedDeadOwnUnits set instead of scanning all objects.
            if (now - _lastKillResync > KillResyncInterval)
            {
                _lastKillResync = now;
                foreach (var deadId in _confirmedDeadOwnUnits)
                {
                    CombatSyncHelper.Send(new CombatEventMessage
                    {
                        EventType      = CombatEventType.UnitDestroyed,
                        TargetEntityId = deadId,
                        SourceEntityId = 0,
                    });
                }
            }
        }

        internal static void ClearDelayed() => _delayedDestroys.Clear();

        /// <summary>Run an action with ApplyingFromNetwork set (for external callers like StateApplier).</summary>
        internal static void RunAsNetworkEvent(System.Action action)
        {
            ApplyingFromNetwork = true;
            try { action(); }
            finally { ApplyingFromNetwork = false; }
        }

        private static ManualLogSource Log => Plugin.Log;

        /// <summary>
        /// Destroy a unit from network authority. Aircraft/missiles/torpedoes check
        /// _externalDestructionNotified in their OnFixedUpdate and self-destruct.
        /// Vessels and submarines never check that flag, so we must call
        /// Compartments.DestroyByExplosion() directly for them.
        /// Also explicitly disables sensors so radar emissions stop immediately —
        /// without this, compartment destruction kills the health but leaves the
        /// emission component active, letting ARMs continue to home on a dead radar.
        /// </summary>
        internal static void DestroyFromNetwork(ObjectBase unit)
        {
            var comps = unit.Compartments;
            if (comps != null)
                comps.DestroyByExplosion();
            else
                unit.notifyOfExternalDestruction();

            // LandUnit has no Compartments and doesn't check _externalDestructionNotified
            // in its update loop, so neither path above actually kills it.
            // Replicate the game's natural LandUnit death: make all systems inoperable
            // (triggers fire effects) then set the destroyed flag.
            if (unit is LandUnit && !unit.IsDestroyed)
            {
                foreach (var sys in unit._obp._systems)
                    sys.MakeInoperable();
                unit.setDestroyedFlag(false, TacView.TCEvent.Destroyed);
                Log.LogInfo($"[Combat] Destroyed LandUnit {unit.UniqueID} ({unit.name}) via setDestroyedFlag");
            }

            // Track own-side dead units for incremental kill resync (D2)
            if (unit._taskforce == Globals._playerTaskforce)
                TrackDeadUnit(unit.UniqueID);

            DisableSensors(unit);
        }

        /// <summary>
        /// Disable all active sensors on a unit, bypassing the PvP AI sensor guard
        /// patches (which would otherwise block the call for enemy-taskforce units).
        /// </summary>
        private static void DisableSensors(ObjectBase unit)
        {
            bool prev = OrderHandler.ApplyingFromNetwork;
            OrderHandler.ApplyingFromNetwork = true;
            try { unit.DisableAllActiveSensors(); }
            catch { /* sensor disable is best-effort */ }
            finally { OrderHandler.ApplyingFromNetwork = prev; }
        }

        public static void Apply(CombatEventMessage msg)
        {
            EventsReceived++;
            Log.LogInfo($"[Combat] Received {msg.EventType}: target={msg.TargetEntityId} source={msg.SourceEntityId}");

            if (SessionManager.SceneLoading || SimSyncManager.CurrentState != SimState.Synchronized)
            {
                Log.LogWarning("[Combat] Ignored — not in game");
                return;
            }

            // Use mapper — it handles both projectiles (divergent IDs) and units (falls back to FindById)
            var target = ProjectileIdMapper.FindByHostId(msg.TargetEntityId);

            if (target == null)
            {
                EventsNotFound++;
                Log.LogWarning($"[Combat] {msg.EventType} target={msg.TargetEntityId} NOT FOUND");
                return;
            }

            Log.LogDebug($"[Combat] Found target {msg.TargetEntityId}: type={target.GetType().Name} name={target.Name?.Value ?? "?"} IsDestroyed={target.IsDestroyed}");

            if (target.IsDestroyed || target._externalDestructionNotified)
            {
                Log.LogDebug($"[Combat] Target {msg.TargetEntityId} already destroyed/notified locally — no action needed");
                return;
            }

            Log.LogDebug($"[Combat] Applying {msg.EventType} to {msg.TargetEntityId}");

            ApplyingFromNetwork = true;
            try
            {
                switch (msg.EventType)
                {
                    case CombatEventType.ProjectileDestroyed:
                        // PvP: the attacker is slightly ahead — the missile may not
                        // have reached us yet. Delay destruction so local sim can
                        // resolve impact and damage first. If the missile hits our
                        // ship in the meantime, the game destroys it naturally and
                        // the delayed action becomes a no-op (IsDestroyed check).
                        if (Plugin.Instance.CfgPvP.Value)
                        {
                            _delayedDestroys.Enqueue((msg.TargetEntityId, Time.unscaledTime + PvpDestroyDelay));
                            Log.LogDebug($"[Combat] PvP: Delaying ProjectileDestroyed for {msg.TargetEntityId} by {PvpDestroyDelay}s");
                            return;
                        }
                        DestroyFromNetwork(target);
                        Log.LogDebug($"[Combat] DestroyFromNetwork called — IsDestroyed={target.IsDestroyed}");
                        break;

                    case CombatEventType.ProjectileIntercepted:
                    case CombatEventType.UnitDestroyed:
                        DestroyFromNetwork(target);
                        Log.LogDebug($"[Combat] DestroyFromNetwork called — IsDestroyed={target.IsDestroyed}");
                        break;

                    case CombatEventType.MissileImpact:
                        // Defender says "your missile X hit my unit, I resolved damage, destroy it"
                        DestroyFromNetwork(target);
                        Log.LogDebug($"[Combat] MissileImpact — destroyed our missile {msg.TargetEntityId}");
                        break;
                }
            }
            finally
            {
                ApplyingFromNetwork = false;
            }
        }
    }
}
