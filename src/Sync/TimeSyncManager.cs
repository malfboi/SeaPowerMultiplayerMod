using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Manages time compression synchronization between host and client.
    ///
    /// Default mode (host-authoritative):
    ///   Host applies time changes locally, broadcasts GameEvent.TimeChanged to client.
    ///   Client UI buttons send requests to host; local changes suppressed until host confirms.
    ///
    /// Vote mode (CfgTimeVote):
    ///   Either side can propose a time change. The other side sees a popup and must
    ///   Agree or Decline. Time only changes when both sides agree.
    /// </summary>
    public static class TimeSyncManager
    {
        /// <summary>True when client has sent a time-change request and is waiting for host (default mode).</summary>
        public static bool PendingRequest { get; private set; }

        /// <summary>
        /// Set true while applying a network-confirmed time change locally.
        /// Prevents Harmony Prefixes from intercepting and re-forwarding the call.
        /// </summary>
        private static bool _applyingFromNetwork = false;

        // ── Vote mode state ─────────────────────────────────────────────────

        /// <summary>True when we've received a proposal from the other side awaiting our response.</summary>
        public static bool HasPendingProposal { get; private set; }

        /// <summary>The raw param of the pending proposal (for applying when accepted).</summary>
        private static float _proposalParam;

        /// <summary>Human-readable description of the proposed time change.</summary>
        public static string ProposalDescription { get; private set; } = "";

        /// <summary>True when we've sent a proposal and are waiting for the other side to respond.</summary>
        public static bool WaitingForVoteResponse { get; private set; }

        /// <summary>Host's CfgTimeVote, synced to client via SessionSync. Only read on client.</summary>
        private static bool _hostVoteModeEnabled;

        /// <summary>Called on client when SessionSync arrives with the host's vote-mode setting.</summary>
        public static void SetHostVoteMode(bool enabled)
        {
            _hostVoteModeEnabled = enabled;
            Plugin.Log.LogInfo($"[TimeSync] Host vote mode = {enabled}");
        }

        /// <summary>
        /// Effective vote-mode setting: the host's config is authoritative for both sides.
        /// Host reads its own config; client reads the value synced from the host.
        /// </summary>
        private static bool VoteModeActive =>
            Plugin.Instance.CfgIsHost.Value ? Plugin.Instance.CfgTimeVote.Value : _hostVoteModeEnabled;

        private static bool IsVoteMode =>
            VoteModeActive && NetworkManager.Instance.IsConnected;

        // ── UI-facing request methods (called by MultiplayerUI buttons) ───────

        public static void RequestPause()
        {
            // Pause is always allowed immediately (exempt from vote mode)
            if (Plugin.Instance.CfgIsHost.Value)
                GameTime.Pause();
            else
                SendTimeRequest(0f);
        }

        public static void RequestUnpause()
        {
            // Unpause is always allowed immediately (exempt from vote mode)
            if (Plugin.Instance.CfgIsHost.Value)
                GameTime.StopTimeCompression();
            else
                SendTimeRequest(1f);
        }

        public static void RequestIncrease()
        {
            if (IsVoteMode)
            {
                SendProposal(-1f);
                return;
            }
            if (Plugin.Instance.CfgIsHost.Value)
                GameTime.IncreaseTimeCompression();
            else
                SendTimeRequest(-1f);
        }

        public static void RequestDecrease()
        {
            if (IsVoteMode)
            {
                SendProposal(-2f);
                return;
            }
            if (Plugin.Instance.CfgIsHost.Value)
                GameTime.DecreaseTimeCompression();
            else
                SendTimeRequest(-2f);
        }

        internal static void SendTimeRequest(float param)
        {
            PendingRequest = true;
            NetworkManager.Instance.SendToServer(new GameEventMessage
            {
                EventType = GameEventType.TimeChanged,
                Param     = param,
            });
        }

        // ── Vote mode methods ───────────────────────────────────────────────

        private static void SendProposal(float param)
        {
            if (WaitingForVoteResponse) return; // already waiting
            WaitingForVoteResponse = true;
            _lastProposedParam = param;
            Plugin.Log.LogInfo($"[TimeSync] Sending time proposal: param={param}");
            NetworkManager.Instance.SendToOther(new GameEventMessage
            {
                EventType = GameEventType.TimeProposal,
                Param     = param,
            });
        }

        /// <summary>Called when we receive a TimeProposal from the other side.</summary>
        public static void OnProposalReceived(float param, bool fromHost)
        {
            _proposalParam = param;
            ProposalDescription = DescribeTimeParam(param);
            HasPendingProposal = true;
            string who = fromHost ? "Host" : "Client";
            Plugin.Log.LogInfo($"[TimeSync] Received time proposal from {who}: {ProposalDescription} (param={param})");
        }

        /// <summary>Called by UI when the player clicks Agree.</summary>
        public static void AcceptProposal()
        {
            if (!HasPendingProposal) return;
            HasPendingProposal = false;
            Plugin.Log.LogInfo($"[TimeSync] Accepting proposal: param={_proposalParam}");

            // Tell the other side we accepted
            NetworkManager.Instance.SendToOther(new GameEventMessage
            {
                EventType = GameEventType.TimeProposalResponse,
                Param     = 1f, // accept
            });

            // Apply locally
            ApplyTimeParam(_proposalParam);
        }

        /// <summary>Called by UI when the player clicks Decline.</summary>
        public static void DeclineProposal()
        {
            if (!HasPendingProposal) return;
            HasPendingProposal = false;
            Plugin.Log.LogInfo("[TimeSync] Declining proposal");

            NetworkManager.Instance.SendToOther(new GameEventMessage
            {
                EventType = GameEventType.TimeProposalResponse,
                Param     = 0f, // decline
            });
        }

        /// <summary>The param we last proposed (so we can apply it when accepted).</summary>
        private static float _lastProposedParam;

        /// <summary>Called when we receive a TimeProposalResponse from the other side.</summary>
        public static void OnProposalResponseReceived(float param)
        {
            if (!WaitingForVoteResponse) return;
            WaitingForVoteResponse = false;

            if (param == 1f)
            {
                Plugin.Log.LogInfo("[TimeSync] Proposal accepted by other side — applying time change");
                ApplyTimeParam(_lastProposedParam);
            }
            else
            {
                Plugin.Log.LogInfo("[TimeSync] Proposal declined by other side");
            }
        }

        private static void ApplyTimeParam(float param)
        {
            _applyingFromNetwork = true;
            try
            {
                if (param == 0f)        GameTime.Pause(false);
                else if (param == 1f)   GameTime.StopTimeCompression();
                else if (param == -1f)  GameTime.IncreaseTimeCompression();
                else if (param == -2f)  GameTime.DecreaseTimeCompression();
                else                    GameTime.SetTimeCompression(param);
            }
            finally
            {
                _applyingFromNetwork = false;
            }
        }

        private static string DescribeTimeParam(float param)
        {
            if (param == 0f)  return "Pause";
            if (param == 1f)  return "1x speed";
            if (param == -1f)
            {
                // "increase" from current — estimate the next step
                float current = GameTime.TimeCompression;
                return $"faster than {current:0.#}x";
            }
            if (param == -2f)
            {
                float current = GameTime.TimeCompression;
                return $"slower than {current:0.#}x";
            }
            return $"{param:0.#}x speed";
        }

        // ── Called by GameEventHandler when a TimeChanged event arrives ─────

        /// <summary>Host receives a time-change request from a client.</summary>
        public static void OnHostReceivedRequest(float param)
        {
            Plugin.Log.LogInfo($"[TimeSync] Host received time request: param={param}");
            if (param == 0f)        GameTime.Pause();
            else if (param == 1f)   GameTime.StopTimeCompression();
            else if (param == -1f)  GameTime.IncreaseTimeCompression();
            else if (param == -2f)  GameTime.DecreaseTimeCompression();
            else                    GameTime.SetTimeCompression(param);
            Plugin.Log.LogInfo($"[TimeSync] Host after applying: paused={GameTime.IsPaused()}, TC={GameTime.TimeCompression}");
            // Broadcast is handled by the Harmony Postfix below
        }

        /// <summary>Client receives confirmed time-change broadcast from host.</summary>
        public static void OnClientReceivedConfirm(float newTimeScale, float hostGameSeconds = 0f)
        {
            Plugin.Log.LogInfo($"[TimeSync] Client received time confirm: newTimeScale={newTimeScale}, currently paused={GameTime.IsPaused()}, TC={GameTime.TimeCompression}");
            PendingRequest = false;
            _applyingFromNetwork = true;
            try
            {
                if (newTimeScale == 0f)
                {
                    Plugin.Log.LogInfo("[TimeSync] Client applying: Pause");
                    GameTime.Pause(false); // don't mute sounds — client-side cosmetic only
                }
                else
                {
                    Plugin.Log.LogInfo($"[TimeSync] Client applying: SetTimeCompression({newTimeScale})");
                    GameTime.SetTimeCompression(newTimeScale);
                }

                // Snap game time to host's on time change events
                if (hostGameSeconds > 0f)
                {
                    float rttSec = NetworkManager.Instance.LastRttMs / 2000f;
                    float tc = newTimeScale;
                    if (tc <= 0f) tc = 0f; // pausing = no time advancement during RTT
                    float estimated = hostGameSeconds + rttSec * tc;
                    StateApplier.SetGameTime(Singleton<SeaPower.Environment>.Instance, estimated);
                    Plugin.Log.LogInfo($"[TimeSync] Snapped game time to {estimated:F1}s (host={hostGameSeconds:F1}, rtt_adj={rttSec * tc:F2})");
                }

                Plugin.Log.LogInfo($"[TimeSync] Client after applying: paused={GameTime.IsPaused()}, TC={GameTime.TimeCompression}");
            }
            finally
            {
                _applyingFromNetwork = false;
            }
        }

        // ── Harmony patches ───────────────────────────────────────────────────

        /// <summary>Shared prefix logic: in vote mode, suppress all time changes unless applying from network.</summary>
        private static bool ShouldSuppressForVoteMode()
        {
            if (!VoteModeActive) return false;
            if (!NetworkManager.Instance.IsConnected) return false;
            if (_applyingFromNetwork) return false;
            return true;
        }

        [HarmonyPatch(typeof(GameTime), nameof(GameTime.SetTimeCompression))]
        public static class Patch_GameTime_SetTimeCompression
        {
            static bool Prefix(float timeScale)
            {
                if (!NetworkManager.Instance.IsConnected) return true;
                if (_applyingFromNetwork) return true;
                if (ShouldSuppressForVoteMode())
                {
                    SendProposal(timeScale);
                    return false;
                }
                if (Plugin.Instance.CfgIsHost.Value) return true;
                Plugin.Log.LogInfo($"[TimeSync] CLIENT SetTimeCompression intercepted (tc={timeScale}) → sending request");
                SendTimeRequest(timeScale);
                return false;
            }

            static void Postfix(float timeScale)
            {
                if (Plugin.Instance.CfgTimeVote.Value) return; // vote mode handles its own sync
                if (!Plugin.Instance.CfgIsHost.Value) return;
                if (!NetworkManager.Instance.IsConnected) return;

                Plugin.Log.LogInfo($"[TimeSync] HOST SetTimeCompression Postfix: broadcasting TC={timeScale}");
                NetworkManager.Instance.BroadcastToClients(new GameEventMessage
                {
                    EventType      = GameEventType.TimeChanged,
                    Param          = timeScale,
                    SourceEntityId = System.BitConverter.ToInt32(
                        System.BitConverter.GetBytes(Singleton<SeaPower.Environment>.Instance.Hour * 3600f
                        + Singleton<SeaPower.Environment>.Instance.Minutes * 60f
                        + Singleton<SeaPower.Environment>.Instance.Seconds), 0),
                });
            }
        }

        [HarmonyPatch(typeof(GameTime), nameof(GameTime.IncreaseTimeCompression))]
        public static class Patch_GameTime_IncreaseTimeCompression
        {
            static bool Prefix()
            {
                if (!NetworkManager.Instance.IsConnected) return true;
                if (_applyingFromNetwork) return true;
                if (ShouldSuppressForVoteMode())
                {
                    SendProposal(-1f);
                    return false;
                }
                if (Plugin.Instance.CfgIsHost.Value) return true;
                Plugin.Log.LogInfo("[TimeSync] CLIENT IncreaseTimeCompression intercepted → sending request");
                SendTimeRequest(-1f);
                return false;
            }

            static void Postfix()
            {
                if (Plugin.Instance.CfgTimeVote.Value) return;
                if (!Plugin.Instance.CfgIsHost.Value) return;
                if (!NetworkManager.Instance.IsConnected) return;
                float tc = GameTime.TimeCompression;
                Plugin.Log.LogInfo($"[TimeSync] HOST IncreaseTimeCompression Postfix: broadcasting TC={tc}");
                NetworkManager.Instance.BroadcastToClients(new GameEventMessage
                {
                    EventType      = GameEventType.TimeChanged,
                    Param          = tc,
                    SourceEntityId = System.BitConverter.ToInt32(
                        System.BitConverter.GetBytes(Singleton<SeaPower.Environment>.Instance.Hour * 3600f
                        + Singleton<SeaPower.Environment>.Instance.Minutes * 60f
                        + Singleton<SeaPower.Environment>.Instance.Seconds), 0),
                });
            }
        }

        [HarmonyPatch(typeof(GameTime), nameof(GameTime.DecreaseTimeCompression))]
        public static class Patch_GameTime_DecreaseTimeCompression
        {
            static bool Prefix()
            {
                if (!NetworkManager.Instance.IsConnected) return true;
                if (_applyingFromNetwork) return true;
                if (ShouldSuppressForVoteMode())
                {
                    SendProposal(-2f);
                    return false;
                }
                if (Plugin.Instance.CfgIsHost.Value) return true;
                Plugin.Log.LogInfo("[TimeSync] CLIENT DecreaseTimeCompression intercepted → sending request");
                SendTimeRequest(-2f);
                return false;
            }

            static void Postfix()
            {
                if (Plugin.Instance.CfgTimeVote.Value) return;
                if (!Plugin.Instance.CfgIsHost.Value) return;
                if (!NetworkManager.Instance.IsConnected) return;
                float tc = GameTime.TimeCompression;
                Plugin.Log.LogInfo($"[TimeSync] HOST DecreaseTimeCompression Postfix: broadcasting TC={tc}");
                NetworkManager.Instance.BroadcastToClients(new GameEventMessage
                {
                    EventType      = GameEventType.TimeChanged,
                    Param          = tc,
                    SourceEntityId = System.BitConverter.ToInt32(
                        System.BitConverter.GetBytes(Singleton<SeaPower.Environment>.Instance.Hour * 3600f
                        + Singleton<SeaPower.Environment>.Instance.Minutes * 60f
                        + Singleton<SeaPower.Environment>.Instance.Seconds), 0),
                });
            }
        }

        [HarmonyPatch(typeof(GameTime), nameof(GameTime.Pause))]
        public static class Patch_GameTime_Pause
        {
            static bool Prefix()
            {
                // Pause is exempt from vote mode — always allowed
                bool isHost = Plugin.Instance.CfgIsHost.Value;
                bool connected = NetworkManager.Instance.IsConnected;
                Plugin.Log.LogInfo($"[TimeSync] Pause Prefix: isHost={isHost}, connected={connected}, applyingFromNet={_applyingFromNetwork}");

                if (isHost) return true;
                if (!connected) return true;
                if (_applyingFromNetwork) return true;
                Plugin.Log.LogInfo("[TimeSync] CLIENT Pause intercepted → sending request to host");
                TimeSyncManager.SendTimeRequest(0f);
                return false; // suppress local pause
            }

            // Host: broadcast the pause to all clients
            static void Postfix()
            {
                if (!Plugin.Instance.CfgIsHost.Value) return;
                if (!NetworkManager.Instance.IsConnected) return;

                Plugin.Log.LogInfo("[TimeSync] HOST Pause Postfix: broadcasting pause (param=0)");
                NetworkManager.Instance.BroadcastToClients(new GameEventMessage
                {
                    EventType      = GameEventType.TimeChanged,
                    Param          = 0f,
                    SourceEntityId = System.BitConverter.ToInt32(
                        System.BitConverter.GetBytes(Singleton<SeaPower.Environment>.Instance.Hour * 3600f
                        + Singleton<SeaPower.Environment>.Instance.Minutes * 60f
                        + Singleton<SeaPower.Environment>.Instance.Seconds), 0),
                });
            }
        }

        // Intercept spacebar-unpause on client (same pattern as Pause above)
        [HarmonyPatch(typeof(GameTime), nameof(GameTime.StopTimeCompression))]
        public static class Patch_GameTime_StopTimeCompression
        {
            static bool Prefix()
            {
                // Unpause is exempt from vote mode — always allowed
                bool isHost = Plugin.Instance.CfgIsHost.Value;
                bool connected = NetworkManager.Instance.IsConnected;
                Plugin.Log.LogInfo($"[TimeSync] StopTimeCompression Prefix: isHost={isHost}, connected={connected}, applyingFromNet={_applyingFromNetwork}");

                if (isHost) return true;
                if (!connected) return true;
                if (_applyingFromNetwork) return true;
                Plugin.Log.LogInfo("[TimeSync] CLIENT StopTimeCompression intercepted → sending request to host");
                TimeSyncManager.SendTimeRequest(1f);
                return false;
            }

            // Host: broadcast the unpause to all clients
            static void Postfix()
            {
                if (!Plugin.Instance.CfgIsHost.Value) return;
                if (!NetworkManager.Instance.IsConnected) return;

                float tc = GameTime.TimeCompression;
                Plugin.Log.LogInfo($"[TimeSync] HOST StopTimeCompression Postfix: broadcasting TC={tc}, paused={GameTime.IsPaused()}");
                NetworkManager.Instance.BroadcastToClients(new GameEventMessage
                {
                    EventType      = GameEventType.TimeChanged,
                    Param          = tc,
                    SourceEntityId = System.BitConverter.ToInt32(
                        System.BitConverter.GetBytes(Singleton<SeaPower.Environment>.Instance.Hour * 3600f
                        + Singleton<SeaPower.Environment>.Instance.Minutes * 60f
                        + Singleton<SeaPower.Environment>.Instance.Seconds), 0),
                });
            }
        }
    }
}
