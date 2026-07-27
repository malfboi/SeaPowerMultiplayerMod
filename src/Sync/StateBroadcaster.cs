using System.Collections;
using LiteNetLib;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// MonoBehaviour attached to the Plugin GameObject.
    /// Periodic host-side sync loops that ride Unity coroutines:
    /// damage-state corrections and waypoint drag flushing.
    /// </summary>
    public class StateBroadcaster : MonoBehaviour
    {
        private void Start()
        {
            StartCoroutine(DamageCorrectionLoop());
            StartCoroutine(WaypointFlushLoop());
            StartCoroutine(SensorStateLoop());
            StartCoroutine(UnitStatusLoop());
        }

        // ── Bottom-row unit status (host → client, both modes) ──────────────
        //
        // Same shape and reasoning as the sensor loop: the status line and the
        // per-mount engagement state are host-decided, so without this a client
        // looking at a ship it had ordered to engage saw a blank line and "Ready"
        // on every mount. Not configurable for the same reason - it is the UI
        // being right, not a choice about what to share. PvP scoping is handled
        // inside the manager (remote player's own taskforce only).
        private IEnumerator UnitStatusLoop()
        {
            var wait = new WaitForSeconds(0.5f);
            while (true)
            {
                yield return wait;
                if (!NetworkManager.Instance.IsEstablished) continue;
                if (SimSyncManager.CurrentState != SimState.Synchronized) continue;
                if (SessionManager.SceneLoading) continue;

                try
                {
                    if (Plugin.Instance.CfgIsHost.Value) UnitStatusManager.HostBroadcast();
                    else                                 UnitStatusManager.ClientReassert();
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[UnitStatus] Sync failed: {ex.Message}");
                }
            }
        }

        // ── Sensor emitter state (host → client, both modes) ────────────────
        //
        // Lives here rather than with the contact/drawing loops because those are
        // co-op only. Emission is world state: the client needs it in PvP too, or
        // its ESM cannot hear a radar that is genuinely radiating.
        //
        // Not configurable, unlike ContactSync/DrawingSync. Those are preferences
        // about what to share with the other player; this is the simulation being
        // right, and switching it off only restores the bug. It would also be a
        // per-machine setting capable of desyncing the two players silently.
        private IEnumerator SensorStateLoop()
        {
            var wait = new WaitForSeconds(0.5f);
            while (true)
            {
                yield return wait;
                if (!NetworkManager.Instance.IsEstablished) continue;
                if (SimSyncManager.CurrentState != SimState.Synchronized) continue;
                if (SessionManager.SceneLoading) continue;

                try
                {
                    if (Plugin.Instance.CfgIsHost.Value) SensorStateManager.HostBroadcast();
                    else                                 SensorStateManager.ClientReassert();
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[Sensors] Sync failed: {ex.Message}");
                }
            }
        }

        // ── Waypoint drag flush (catches throttled final positions) ────────
        private IEnumerator WaypointFlushLoop()
        {
            var wait = new WaitForSeconds(0.15f);
            while (true)
            {
                yield return wait;
                if (!NetworkManager.Instance.IsConnected) continue;

                foreach (var kvp in Patch_UserRootNode_UpdateSimulation._pending)
                {
                    var (unit, index) = kvp.Value;
                    var root = unit._userRoot;
                    if (root == null || index >= root.TaskViewModels.Count) continue;
                    if (root.TaskViewModels[index].Task is GoToWaypointTask wp)
                        Patch_UserRootNode_UpdateSimulation.SendEditWaypoint(unit, index, wp);
                }
                Patch_UserRootNode_UpdateSimulation._pending.Clear();
            }
        }

        // ── Periodic damage correction (catches drift / packet loss) ────────
        private IEnumerator DamageCorrectionLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(Plugin.Instance.CfgDamageSyncInterval.Value);
                if (!NetworkManager.Instance.IsConnected) continue;

                // v2: damage is host-authoritative in both modes
                if (!Plugin.Instance.CfgIsHost.Value) continue;

                BroadcastDamageCorrections();
            }
        }

        private static void BroadcastDamageCorrections()
        {
            SendCorrections(UnitRegistry.Vessels);
            SendCorrections(UnitRegistry.Submarines);
        }

        private static void SendCorrections<T>(System.Collections.Generic.IReadOnlyList<T> units) where T : ObjectBase
        {
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null || unit.IsDestroyed) continue;
                var comps = unit.Compartments;
                if (comps == null) continue;

                // Only send for units that have taken damage or are sinking
                if (!comps._isSinking && comps.IntegrityPercentage > 99f) continue;

                var msg = DamageStateSerializer.Capture(unit);
                if (msg != null)
                    NetworkManager.Instance.SendToOther(msg, DeliveryMethod.Unreliable);
            }
        }
    }
}
