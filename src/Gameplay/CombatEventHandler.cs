using BepInEx.Logging;
using SeaPower;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Applies host-authoritative destruction on the client (v2: DestroyEvent /
    /// state-stream flags / census all funnel through DestroyFromNetwork).
    /// </summary>
    public static class CombatEventHandler
    {
        /// <summary>True while applying a combat event from network. Guards Harmony patches.</summary>
        internal static bool ApplyingFromNetwork;

        /// <summary>Run an action with ApplyingFromNetwork set (for external callers).</summary>
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
        /// Also explicitly disables sensors so radar emissions stop immediately -
        /// without this, compartment destruction kills the health but leaves the
        /// emission component active, letting ARMs continue to home on a dead radar.
        /// </summary>
        internal static void DestroyFromNetwork(ObjectBase unit)
        {
            var comps = unit.Compartments;
            if (comps != null)
                using (Authority.Allowed())   // client-side DestroyByExplosion is host-only
                    comps.DestroyByExplosion();
            else
                unit.notifyOfExternalDestruction();

            // LandUnit has no Compartments and doesn't check _externalDestructionNotified
            // in its update loop, so neither path above actually kills it.
            // Zero every system's integrity (IsDestroyed derives from it) and let
            // LandUnit.OnUpdateEveryFrame run the native death: a Fire per destroyed
            // system, MakeInoperable, setDestroyedFlag, ambient audio stop. Setting
            // the flag here directly skipped that loop - kills showed no fires.
            // The stream's FlagDestroyed re-invokes this until IsDestroyed, so if
            // the unit's own damage threshold never trips (ini-set DamagePoints),
            // a later call finds all systems down and finishes with the flag.
            if (unit is LandUnit && !unit.IsDestroyed)
            {
                var systems = unit._obp?._systems;
                bool anyAlive = false;
                if (systems != null)
                    foreach (var s in systems)
                        if (!s.IsDestroyed) { anyAlive = true; break; }

                if (anyAlive)
                {
                    foreach (var sys in systems!)
                        sys.CurrentIntegrity = 0f;
                    Log.LogInfo($"[Combat] Destroying LandUnit {unit.UniqueID} ({unit.name}) via system integrity zeroing");
                }
                else
                {
                    unit.setDestroyedFlag(false, TacView.TCEvent.Destroyed);
                    Log.LogInfo($"[Combat] Destroyed LandUnit {unit.UniqueID} ({unit.name}) via setDestroyedFlag fallback");
                }
            }

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
    }
}
