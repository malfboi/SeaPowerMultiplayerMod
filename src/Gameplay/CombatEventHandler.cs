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

        // ── Stats (read by UI) ──────────────────────────────────────────────

        public static int EventsReceived { get; private set; }
        public static int EventsNotFound { get; private set; }
        public static void ResetCounters() { EventsReceived = 0; EventsNotFound = 0; }

        // ── PvP: delayed projectile destruction ─────────────────────────────
        // The attacker's "missile died" arrives before the missile reaches us.
        // Delay destruction so the local sim has time to resolve impact/damage.
        private const float PvpDestroyDelay = 3f;
        private static readonly List<(int id, float destroyAt)> _delayedDestroys = new();

        /// <summary>Called from Plugin.Update() each frame.</summary>
        internal static void Tick()
        {
            if (_delayedDestroys.Count == 0) return;
            float now = Time.unscaledTime;
            for (int i = _delayedDestroys.Count - 1; i >= 0; i--)
            {
                var (id, destroyAt) = _delayedDestroys[i];
                if (now < destroyAt) continue;

                _delayedDestroys.RemoveAt(i);
                var obj = StateSerializer.FindById(id);
                if (obj == null || obj.IsDestroyed) continue;

                RunAsNetworkEvent(() => obj.notifyOfExternalDestruction());
                Log.LogDebug($"[Combat] PvP delayed destroy fired for {id}");
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
        /// </summary>
        internal static void DestroyFromNetwork(ObjectBase unit)
        {
            var comps = unit.Compartments;
            if (comps != null)
                comps.DestroyByExplosion();
            else
                unit.notifyOfExternalDestruction();
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
                Log.LogWarning($"[Combat] Target {msg.TargetEntityId} NOT FOUND (missile may have already been destroyed by impact)");
                return;
            }

            Log.LogDebug($"[Combat] Found target {msg.TargetEntityId}: type={target.GetType().Name} name={target.Name?.Value ?? "?"} IsDestroyed={target.IsDestroyed}");

            if (target.IsDestroyed)
            {
                Log.LogDebug($"[Combat] Target {msg.TargetEntityId} already destroyed locally — no action needed");
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
                            _delayedDestroys.Add((msg.TargetEntityId, Time.unscaledTime + PvpDestroyDelay));
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
