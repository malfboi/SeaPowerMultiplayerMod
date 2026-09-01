using SeaPower;

namespace SeapowerMultiplayer
{
    public enum SimState
    {
        Idle,
        WaitingForClient,
        Synchronized,
    }

    /// <summary>
    /// Coordinates the synchronized simulation lifecycle.
    /// Tracks whether both sides have loaded and are ready to run.
    /// </summary>
    public static class SimSyncManager
    {
        private static SimState _currentState = SimState.Idle;
        public static SimState CurrentState
        {
            get => _currentState;
            set
            {
                if (_currentState != value)
                {
                    Plugin.Log.LogInfo($"[SimSync] State transition: {_currentState} → {value}");
                    _currentState = value;
                }
            }
        }

        // ── Readiness ─────────────────────────────────────────────────────────
        //
        // A SET of slots, not a bool. "BothSidesReady" could only ever describe two
        // players: the first SessionReady to arrive flipped it true, so with three
        // players the host would resume the moment the FASTEST loader reported in and
        // leave the others still on their loading screens.

        private static readonly System.Collections.Generic.HashSet<byte> _readySlots = new();

        public static bool IsReady(byte slot) => _readySlots.Contains(slot);

        /// <summary>Every established player has reported in. The host's cue to resume.</summary>
        public static bool AllReady
        {
            get
            {
                bool any = false;
                foreach (var p in PlayerRegistry.All)
                {
                    if (p.Slot == 0 || !p.Connected || !p.Established) continue;
                    any = true;
                    if (!_readySlots.Contains(p.Slot)) return false;
                }
                return any;
            }
        }

        /// <summary>At least one established player is still loading.</summary>
        public static bool AnyPending
        {
            get
            {
                foreach (var p in PlayerRegistry.All)
                {
                    if (p.Slot == 0 || !p.Connected || !p.Established) continue;
                    if (!_readySlots.Contains(p.Slot)) return true;
                }
                return false;
            }
        }

        public static void ClearAllReady() => _readySlots.Clear();

        /// <summary>A player is loading a fresh session - they are not ready until they
        /// say so.</summary>
        public static void OnPeerJoined(byte slot) => _readySlots.Remove(slot);

        /// <summary>A player left. Drop their readiness so <see cref="AllReady"/> is not
        /// waiting on somebody who will never answer.</summary>
        public static void OnPeerLeft(byte slot)
        {
            if (_readySlots.Remove(slot))
                Plugin.Log.LogInfo($"[SimSync] Slot {slot} left — readiness dropped.");
        }

        // ── Issue banner ──────────────────────────────────────────────────────
        // A failed session sync used to leave no trace outside the BepInEx log:
        // CurrentState fell back to Idle and the overlay simply stopped drawing a
        // sync line, so a broken sync looked identical to a healthy connection.
        // These fields survive Reset() so the overlay can keep showing what went
        // wrong until the next sync attempt starts.

        /// <summary>Short issue line for the overlay, or null when healthy.</summary>
        public static string? IssueMessage { get; private set; }

        /// <summary>Optional second line with detail or a suggested fix.</summary>
        public static string? IssueHint { get; private set; }

        /// <summary>True when the issue is transient (retry in progress) rather than fatal.</summary>
        public static bool IssueIsWarning { get; private set; }

        public static bool HasIssue => IssueMessage != null;

        public static void ReportIssue(string message, string hint = "", bool warning = false)
        {
            IssueMessage   = message;
            IssueHint      = string.IsNullOrEmpty(hint) ? null : hint;
            IssueIsWarning = warning;
            if (warning) Plugin.Log.LogWarning($"[SimSync] Issue: {message} {hint}");
            else         Plugin.Log.LogError($"[SimSync] Issue: {message} {hint}");
        }

        public static void ClearIssue()
        {
            if (IssueMessage == null) return;
            Plugin.Log.LogInfo("[SimSync] Issue cleared");
            IssueMessage   = null;
            IssueHint      = null;
            IssueIsWarning = false;
        }

        /// <summary>
        /// Resets the sync lifecycle. Deliberately leaves the issue banner alone -
        /// failure paths call Reset() right after reporting, and the player still
        /// needs to see why the sync failed.
        /// </summary>
        public static void Reset()
        {
            Plugin.Log.LogInfo("[SimSync] Reset()");
            CurrentState = SimState.Idle;
            _readySlots.Clear();
        }

        /// <summary>
        /// Host: a player finished loading.
        ///
        /// CurrentState still goes Synchronized on the FIRST report, and deliberately
        /// so - it means "this machine's own sim is live", which is what its fifteen
        /// read sites ask, and a later joiner must not switch the host's streaming off
        /// for the players already in. Whether to RESUME is the separate question, and
        /// that one asks <see cref="AllReady"/>.
        /// </summary>
        public static void OnClientReady(byte slot)
        {
            if (slot != PlayerRegistry.NoSender) _readySlots.Add(slot);
            CurrentState = SimState.Synchronized;
            ClearIssue();

            // Only now are unit ids meaningful on that machine. Sent earlier, the table
            // would resolve to nothing - and an EMPTY table does not read as "not yet",
            // it reads as "nobody owns anything", i.e. everything is commandable.
            if (Plugin.Instance.CfgIsHost.Value && slot != PlayerRegistry.NoSender)
            {
                if (PlayerRegistry.TryGet(slot, out var p))
                    FormationOwnership.HostGrantTeamTo(p.Team, slot);
                FormationOwnership.HostSendFull(slot);

                // Re-state the roster now that this player is definitely listening.
                // The one sent at handshake time can lose a race with the client's own
                // Welcome, and a roster is otherwise only re-sent when somebody joins or
                // leaves - so a guest that missed it stayed teammate-less for the whole
                // session. Four short entries; cheap insurance against a silent gap.
                PlayerRegistry.HostBroadcastRoster();
            }
            Plugin.Log.LogInfo($"[SimSync] Slot {slot} ready (allReady={AllReady}) — " +
                               $"paused={GameTime.IsPaused()}, TC={GameTime.TimeCompression}");
        }
    }
}
