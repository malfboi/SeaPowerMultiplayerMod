using SeaPower;
using SeapowerMultiplayer.Transport;
using Steamworks;
using UnityEngine;

namespace SeapowerMultiplayer
{
    public enum LinkState
    {
        /// <summary>Normal play, or no session at all.</summary>
        Idle,
        /// <summary>Peer lost mid-session. Both sides frozen; client retries.</summary>
        Interrupted,
        /// <summary>Client is re-dialling and re-handshaking.</summary>
        Reconnecting,
        /// <summary>Link is back; host is pushing a fresh snapshot.</summary>
        Resyncing,
    }

    /// <summary>
    /// Owns the mid-session disconnect lifecycle.
    ///
    /// Before this existed, a dropped peer only reset the replication subsystems:
    /// neither side paused and nothing was shown, so on the client
    /// Suppression.EnforceDefenseFlag() handed local AI back and the session
    /// quietly became two separate single-player games. The rule here is that a
    /// broken link always stops the clock on both sides, so an unnoticed
    /// disconnect is impossible.
    ///
    /// Reconnect deliberately reuses the join path rather than inventing a
    /// delta: host re-runs SessionManager.CaptureAndSend(), client loads it into
    /// the still-loaded scene, and the host unpauses on SessionReady, restoring
    /// the time compression that was running when the link dropped.
    /// </summary>
    public static class ReconnectManager
    {
        /// <summary>Gap between automatic client retries.</summary>
        private const float RetryIntervalSec = 10f;

        /// <summary>How long one attempt may sit in Reconnecting before it counts as failed.
        /// Covers the case where the host is gone entirely, so no disconnect event ever
        /// arrives to fail the attempt for us.</summary>
        private const float AttemptTimeoutSec = 20f;

        public static LinkState State { get; private set; } = LinkState.Idle;

        /// <summary>True whenever the simulation must stay stopped.</summary>
        public static bool IsFrozen => State != LinkState.Idle;

        public static int Attempts { get; private set; }

        /// <summary>Seconds until the next automatic retry, or 0 when not waiting.</summary>
        public static float RetryCountdown =>
            State == LinkState.Interrupted && !NetworkManager.Instance.IsHost
                ? Mathf.Max(0f, _nextRetryRealtime - Time.realtimeSinceStartup)
                : 0f;

        /// <summary>Time compression to restore once both sides are synced again.
        /// 0 means the session was already paused when the link dropped.</summary>
        private static float _frozenTimeCompression;

        private static float _nextRetryRealtime;
        private static float _attemptDeadline;
        private static bool  _suppressNextLoss;

        /// <summary>Steam lobby to rejoin if the client fell out of it completely.</summary>
        private static CSteamID _lastLobby = CSteamID.Nil;

        private static bool IsSteam => Plugin.Instance.CfgTransport.Value == "Steam";

        /// <summary>The player deliberately ended the session - the disconnect that
        /// follows is expected and must not trigger the freeze.</summary>
        public static void NotifyIntentionalDisconnect() => _suppressNextLoss = true;

        // ── Lifecycle hooks ──────────────────────────────────────────────────

        /// <summary>Peer dropped. <paramref name="wasEstablished"/> is the handshake
        /// state from before NetworkManager reset it.</summary>
        public static void OnPeerLost(bool wasEstablished)
        {
            // Already handling an interruption: a failed reconnect attempt just
            // came back. Keep the freeze and go back to waiting.
            if (IsFrozen)
            {
                State = LinkState.Interrupted;
                ScheduleRetry();
                Plugin.Log.LogInfo($"[Reconnect] Attempt {Attempts} failed - retrying in {RetryIntervalSec:F0}s");
                return;
            }

            if (_suppressNextLoss)
            {
                _suppressNextLoss = false;
                return;
            }

            // A drop before the handshake completed, or outside a mission, is an
            // ordinary failed connection - the existing UI already covers it.
            if (!wasEstablished || !InScenario()) return;

            Freeze();
        }

        /// <summary>Handshake reached Established on either side.</summary>
        public static void OnPeerEstablished()
        {
            // StartHost/StartClient call Stop() to restart cleanly, so an ordinary
            // connect arms the suppress flag too. Clearing it here stops it going
            // stale and swallowing the first genuine drop of the session.
            _suppressNextLoss = false;

            if (!IsFrozen) return; // ordinary first join - existing manual flow untouched

            State = LinkState.Resyncing;
            Plugin.Log.LogInfo("[Reconnect] Link re-established");

            if (NetworkManager.Instance.IsHost)
            {
                Plugin.Log.LogInfo("[Reconnect] Host pushing a fresh session snapshot");
                SessionManager.CaptureAndSend();
            }
        }

        /// <summary>Host: the returning client reported SessionReady.</summary>
        public static void OnClientResynced()
        {
            if (!IsFrozen || !NetworkManager.Instance.IsHost) return;
            Thaw();
        }

        /// <summary>Client: our own resync finished and SessionReady has been sent.
        /// We stay paused - the host's unpause broadcast is what starts us again.</summary>
        public static void OnLocalResyncComplete()
        {
            if (!IsFrozen || NetworkManager.Instance.IsHost) return;

            Plugin.Log.LogInfo("[Reconnect] Client resynced - waiting for host to unpause");
            State = LinkState.Idle;
            Attempts = 0;
            SimSyncManager.ClearIssue();
        }

        // ── Player actions ───────────────────────────────────────────────────

        /// <summary>Client: dial the host again. Also called by the auto-retry.</summary>
        public static void BeginReconnect()
        {
            if (NetworkManager.Instance.IsHost) return;
            if (State != LinkState.Interrupted) return;

            State = LinkState.Reconnecting;
            Attempts++;
            _attemptDeadline = Time.realtimeSinceStartup + AttemptTimeoutSec;
            Plugin.Log.LogInfo($"[Reconnect] Attempt {Attempts} starting");

            if (IsSteam)
            {
                // Losing the P2P connection does not necessarily mean losing lobby
                // membership, and restarting the transport is much cheaper than a
                // rejoin, so only fall back to JoinLobby when we really left.
                if (SteamLobbyManager.InLobby)
                    NetworkManager.Instance.StartTransport(asHost: false);
                else if (_lastLobby != CSteamID.Nil)
                    SteamLobbyManager.JoinLobby(_lastLobby);
                else
                    Plugin.Log.LogWarning("[Reconnect] No lobby to rejoin - the host must re-invite.");
            }
            else
            {
                NetworkManager.Instance.StartClient(
                    Plugin.Instance.CfgHostIP.Value, Plugin.Instance.CfgPort.Value);
            }
        }

        /// <summary>Give up: host continues solo, or client drops back to single player.
        /// The game stays paused so nobody resumes without meaning to.</summary>
        public static void AbandonSession()
        {
            Plugin.Log.LogInfo("[Reconnect] Session abandoned by player");
            State = LinkState.Idle;
            Attempts = 0;
            NetworkManager.Instance.Stop();
        }

        // ── Per-frame ────────────────────────────────────────────────────────

        public static void Tick()
        {
            if (!IsFrozen) return;

            // Backstop for anything that unpauses behind our back. Skipped while a
            // snapshot is loading so we never fight the save loader.
            if (!SessionManager.IsReceiving && !GameTime.IsPaused())
                GameTime.Pause();

            if (NetworkManager.Instance.IsHost) return; // host waits, it never dials

            float now = Time.realtimeSinceStartup;

            if (State == LinkState.Reconnecting && now > _attemptDeadline)
            {
                Plugin.Log.LogInfo($"[Reconnect] Attempt {Attempts} timed out");
                State = LinkState.Interrupted;
                ScheduleRetry();
            }

            if (State == LinkState.Interrupted && now >= _nextRetryRealtime)
                BeginReconnect();
        }

        // ── Internals ────────────────────────────────────────────────────────

        private static void Freeze()
        {
            _frozenTimeCompression = GameTime.IsPaused() ? 0f : GameTime.TimeCompression;
            _lastLobby = SteamLobbyManager.LobbyId;
            Attempts = 0;
            State = LinkState.Interrupted;   // set before Pause so the freeze gate sees it

            GameTime.Pause();
            ScheduleRetry();

            Plugin.Log.LogWarning(
                $"[Reconnect] Connection lost mid-session - simulation frozen " +
                $"(restore TC={_frozenTimeCompression}).");
            Telemetry.Count("reconnect.linkLost");
        }

        private static void Thaw()
        {
            float tc = _frozenTimeCompression;
            State = LinkState.Idle;
            Attempts = 0;
            SimSyncManager.ClearIssue();

            // Host only: ForceResume applies locally and makes sure the client is
            // told, in vote mode as well as normal mode, so both sides resume
            // together. A session that was already paused stays paused.
            if (tc > 0f)
            {
                Plugin.Log.LogInfo($"[Reconnect] Both sides synced - resuming at TC={tc}");
                TimeSyncManager.ForceResume(tc);
            }
            else
            {
                Plugin.Log.LogInfo("[Reconnect] Both sides synced - staying paused (was paused before the drop)");
            }

            Telemetry.Count("reconnect.recovered");
        }

        private static void ScheduleRetry() =>
            _nextRetryRealtime = Time.realtimeSinceStartup + RetryIntervalSec;

        /// <summary>True once a mission is loaded - the same signal SessionManager's
        /// scene-ready path waits on.</summary>
        private static bool InScenario()
            => Singleton<SceneCreator>.InstanceExists(false)
               && Singleton<SceneCreator>.Instance.IsLoadingDone;
    }
}
