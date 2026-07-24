using System;
using System.Collections;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// MonoBehaviour on the Plugin GameObject: the two slow "shared picture" loops.
    /// Both run at human timescales - contacts change over seconds and markers are
    /// placed by hand - so neither shares the 10 Hz entity stream's timer.
    ///
    /// BOTH ARE CO-OP ONLY. They exist to give two players commanding one task force
    /// the same picture; in PvP the players are opponents whose pictures are supposed
    /// to differ, and sharing either one would give the game away.
    /// </summary>
    public class ContactSyncStreamer : MonoBehaviour
    {
        private const float ContactInterval = 0.5f;
        private const float DrawingInterval = 1f;

        private void Start()
        {
            StartCoroutine(ContactLoop());
            StartCoroutine(DrawingLoop());
        }

        private static bool SessionLive()
            => ContactSyncManager.CoopSessionActive
               && SimSyncManager.CurrentState == SimState.Synchronized
               && !SessionManager.SceneLoading;

        /// <summary>Host → client: track numbers and classification.</summary>
        private IEnumerator ContactLoop()
        {
            var wait = new WaitForSeconds(ContactInterval);
            while (true)
            {
                yield return wait;
                if (!Plugin.Instance.CfgContactSync.Value) continue;
                if (!Plugin.Instance.CfgIsHost.Value) continue;
                if (!SessionLive()) continue;

                try { ContactSyncManager.HostBroadcast(); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[Contacts] Broadcast failed: {ex.Message}"); }
            }
        }

        /// <summary>Bidirectional: map drawings, sent by whichever side edited them.</summary>
        private IEnumerator DrawingLoop()
        {
            var wait = new WaitForSeconds(DrawingInterval);
            while (true)
            {
                yield return wait;
                if (!Plugin.Instance.CfgDrawingSync.Value) continue;
                if (!SessionLive()) continue;

                try { DrawingSyncManager.PollAndSend(); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[Drawings] Poll failed: {ex.Message}"); }
            }
        }
    }
}
