using BepInEx.Logging;
using SeaPower;
using SeapowerMultiplayer.Messages;

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

            Log.LogInfo($"[Combat] Found target {msg.TargetEntityId}: type={target.GetType().Name} name={target.Name?.Value ?? "?"} IsDestroyed={target.IsDestroyed}");

            if (target.IsDestroyed)
            {
                Log.LogInfo($"[Combat] Target {msg.TargetEntityId} already destroyed locally — no action needed");
                return;
            }

            Log.LogInfo($"[Combat] Applying {msg.EventType} to {msg.TargetEntityId} — calling notifyOfExternalDestruction");

            ApplyingFromNetwork = true;
            try
            {
                switch (msg.EventType)
                {
                    case CombatEventType.ProjectileIntercepted:
                    case CombatEventType.ProjectileDestroyed:
                    case CombatEventType.UnitDestroyed:
                        DestroyFromNetwork(target);
                        Log.LogInfo($"[Combat] DestroyFromNetwork called — IsDestroyed={target.IsDestroyed}");
                        break;

                    case CombatEventType.MissileImpact:
                        // Defender says "your missile X hit my unit, I resolved damage, destroy it"
                        DestroyFromNetwork(target);
                        Log.LogInfo($"[Combat] MissileImpact — destroyed our missile {msg.TargetEntityId}");
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
