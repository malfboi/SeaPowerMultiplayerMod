using System.Reflection;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Transport;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// In-game overlay. Toggle with Ctrl+F9.
    /// Shows connection status, ping, time compression controls, and sync health.
    /// </summary>
    public class MultiplayerUI : MonoBehaviour
    {
        private bool _visible = true;

        // Styles created once
        private GUIStyle? _boxStyle;
        private GUIStyle? _labelStyle;
        private GUIStyle? _headerStyle;
        private GUIStyle? _buttonStyle;
        private GUIStyle? _warningStyle;
        private GUIStyle? _successStyle;
        private GUIStyle? _criticalStyle;
        private GUIStyle? _elevatedStyle;
        private GUIStyle? _sectionHeaderStyle;
        private GUIStyle? _dimLabelStyle;
        private bool _stylesInit = false;

        // Cached unit counts (refreshed every 0.5s instead of every OnGUI frame)
        private float _unitCountTimer;
        private int _ownVessels, _ownSubs, _ownAir, _ownLand, _ownMissiles, _ownTorps;
        private int _enemyVessels, _enemySubs, _enemyAir, _enemyLand, _enemyMissiles, _enemyTorps;
        private int _remoteUnitCount;

        // Reflection for missile/torpedo ownership
        private static readonly FieldInfo _launchPlatformField =
            AccessTools.Field(typeof(WeaponBase), "_launchPlatform");

        private const int PanelWidth  = 280;
        private const int Margin     = 10;

        // Auto-sized panel: track content height from previous frame
        private float _contentHeight = 200f;
        private Vector2 _scrollPos;

        // Foldout state for sync health sections
        private bool _foldUnits = true;
        private bool _foldProjectiles, _foldMissileState, _foldFlightOps;
        private bool _foldFireAuth, _foldCombatEvents, _foldDrift;

        // TF dropdown state
        private byte? _tfDropdownOpenFor; // which player's dropdown is open, null = none

        // Overall sync status
        private enum SyncStatus { OK, Degraded, Issues }

        private void Update()
        {
            if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.F9))
                _visible = !_visible;

            _unitCountTimer -= Time.deltaTime;
            if (_unitCountTimer <= 0f)
            {
                _unitCountTimer = 0.5f;
                RefreshUnitCounts();
            }
        }

        private void RefreshUnitCounts()
        {
            bool isPvP = Plugin.Instance.CfgPvP.Value;
            var playerTf = Globals._playerTaskforce;

            _ownVessels = _ownSubs = _ownAir = _ownLand = _ownMissiles = _ownTorps = 0;
            _enemyVessels = _enemySubs = _enemyAir = _enemyLand = _enemyMissiles = _enemyTorps = 0;

            foreach (var v in Object.FindObjectsByType<Vessel>(FindObjectsSortMode.None))
            {
                if (isPvP && playerTf != null && v._taskforce == playerTf) _ownVessels++;
                else _enemyVessels++;
            }
            foreach (var s in Object.FindObjectsByType<Submarine>(FindObjectsSortMode.None))
            {
                if (isPvP && playerTf != null && s._taskforce == playerTf) _ownSubs++;
                else _enemySubs++;
            }
            foreach (var a in Object.FindObjectsByType<Aircraft>(FindObjectsSortMode.None))
            {
                if (isPvP && playerTf != null && a._taskforce == playerTf) _ownAir++;
                else _enemyAir++;
            }
            foreach (var h in Object.FindObjectsByType<Helicopter>(FindObjectsSortMode.None))
            {
                if (isPvP && playerTf != null && h._taskforce == playerTf) _ownAir++;
                else _enemyAir++;
            }
            foreach (var l in Object.FindObjectsByType<LandUnit>(FindObjectsSortMode.None))
            {
                if (isPvP && playerTf != null && l._taskforce == playerTf) _ownLand++;
                else _enemyLand++;
            }
            foreach (var m in Object.FindObjectsByType<Missile>(FindObjectsSortMode.None))
            {
                var launcher = _launchPlatformField?.GetValue(m) as ObjectBase;
                if (isPvP && playerTf != null && launcher != null && launcher._taskforce == playerTf) _ownMissiles++;
                else _enemyMissiles++;
            }
            foreach (var t in Object.FindObjectsByType<Torpedo>(FindObjectsSortMode.None))
            {
                var launcher = _launchPlatformField?.GetValue(t) as ObjectBase;
                if (isPvP && playerTf != null && launcher != null && launcher._taskforce == playerTf) _ownTorps++;
                else _enemyTorps++;
            }

            _remoteUnitCount = StateApplier.LastRemoteUnitCount;
        }

        private void InitStyles()
        {
            if (_stylesInit) return;

            _boxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 10, 10),
            };

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize  = 13,
                normal    = { textColor = new Color(0.9f, 0.9f, 0.9f) },
            };

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal   = { textColor = new Color(0.85f, 0.85f, 0.85f) },
            };

            _warningStyle  = MakeBoldLabel(new Color(1f, 0.7f, 0.2f));
            _successStyle  = MakeBoldLabel(new Color(0.3f, 1f, 0.4f));
            _elevatedStyle = MakeBoldLabel(new Color(1f, 1f, 0.3f));
            _criticalStyle = MakeBoldLabel(new Color(1f, 0.3f, 0.3f));

            _sectionHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize  = 11,
                normal    = { textColor = new Color(0.75f, 0.85f, 0.95f) },
            };

            _dimLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                normal   = { textColor = new Color(0.6f, 0.6f, 0.6f) },
            };

            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11,
            };

            _stylesInit = true;
        }

        private static GUIStyle MakeBoldLabel(Color color) => new GUIStyle(GUI.skin.label)
        {
            fontSize  = 11,
            fontStyle = FontStyle.Bold,
            normal    = { textColor = color },
        };

        private void OnGUI()
        {
            InitStyles();

            // Vote popup is always visible, even when the main panel is hidden
            DrawTimeVotePopup();

            if (!_visible) return;

            float x = Screen.width - PanelWidth - Margin;
            // Cap panel height to screen height minus margins, scroll if needed
            float maxHeight = Screen.height - Margin * 2;
            float panelHeight = Mathf.Min(_contentHeight + 20f, maxHeight); // +20 for box padding
            bool needsScroll = _contentHeight + 20f > maxHeight;

            GUILayout.BeginArea(new Rect(x, Margin, PanelWidth, panelHeight), _boxStyle);

            if (needsScroll)
                _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            DrawHeader();
            GUILayout.Space(4);
            DrawConnection();
            GUILayout.Space(6);
            if (NetworkManager.Instance.IsConnected)
            {
                DrawPlayerList();
                GUILayout.Space(6);
            }
            DrawTimeControls();
            GUILayout.Space(6);

            if (NetworkManager.Instance.IsConnected)
                DrawSyncHealth();

            if (needsScroll)
                GUILayout.EndScrollView();

            // Measure actual content height for next frame
            if (Event.current.type == EventType.Repaint)
                _contentHeight = GUILayoutUtility.GetLastRect().yMax;

            GUILayout.EndArea();
        }

        // ── Time Vote Popup ──────────────────────────────────────────────────

        private void DrawTimeVotePopup()
        {
            if (!TimeSyncManager.HasPendingProposal) return;

            const float popupWidth = 300f;
            const float popupHeight = 100f;
            float px = (Screen.width - popupWidth) / 2f;
            float py = (Screen.height - popupHeight) / 2f;

            GUILayout.BeginArea(new Rect(px, py, popupWidth, popupHeight), _boxStyle);

            bool isHost = Plugin.Instance.CfgIsHost.Value;
            string who = isHost ? "Client" : "Host";
            GUILayout.Label($"{who} wants: {TimeSyncManager.ProposalDescription}", _warningStyle);
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Agree", _buttonStyle))
                TimeSyncManager.AcceptProposal();
            if (GUILayout.Button("Decline", _buttonStyle))
                TimeSyncManager.DeclineProposal();
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }

        // ── Header ────────────────────────────────────────────────────────────

        private void DrawHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"SeaPower MP  v{PluginInfo.PLUGIN_VERSION}", _headerStyle);
            GUILayout.FlexibleSpace();
            if (Plugin.Instance.CfgPvP.Value)
                GUILayout.Label("PvP", _warningStyle);
            GUILayout.Label("[Ctrl+F9]", _labelStyle);
            GUILayout.EndHorizontal();
        }

        // ── Connection section ────────────────────────────────────────────────

        private void DrawConnection()
        {
            bool isSteam = Plugin.Instance.CfgTransport.Value == "Steam";

            if (isSteam)
                DrawConnectionSteam();
            else
                DrawConnectionLiteNet();
        }

        private void DrawConnectionLiteNet()
        {
            bool isHost      = Plugin.Instance.CfgIsHost.Value;
            bool isConnected = NetworkManager.Instance.IsConnected;

            GUILayout.Label("\u2500\u2500 Network \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500", _labelStyle);

            // Mode + status
            string modeStr   = isHost ? "HOST" : "CLIENT";
            Color  statusCol = isConnected ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.4f, 0.4f);
            string statusStr = isConnected ? "Connected" : "Disconnected";

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Mode: {modeStr}", _labelStyle, GUILayout.Width(100));
            var prevColor = GUI.color;
            GUI.color = statusCol;
            GUILayout.Label(statusStr, _labelStyle);
            GUI.color = prevColor;
            GUILayout.EndHorizontal();

            // Ping
            if (isConnected)
            {
                GUILayout.Label($"Ping: {NetworkManager.Instance.LastRttMs} ms", _labelStyle);
            }
            else
            {
                // Port / IP info
                if (isHost)
                    GUILayout.Label($"Port: {Plugin.Instance.CfgPort.Value}", _labelStyle);
                else
                    GUILayout.Label($"Host: {Plugin.Instance.CfgHostIP.Value}:{Plugin.Instance.CfgPort.Value}", _labelStyle);
            }

            GUILayout.Space(4);

            // Connect / Disconnect button
            if (!isConnected)
            {
                string btnLabel = isHost ? "Start Hosting" : "Connect";
                if (GUILayout.Button(btnLabel, _buttonStyle))
                {
                    if (isHost)
                        NetworkManager.Instance.StartHost(Plugin.Instance.CfgPort.Value);
                    else
                        NetworkManager.Instance.StartClient(Plugin.Instance.CfgHostIP.Value, Plugin.Instance.CfgPort.Value);
                }
            }
            else
            {
                if (GUILayout.Button("Disconnect", _buttonStyle))
                    NetworkManager.Instance.Stop();

                if (isHost)
                {
                    GUILayout.Space(4);
                    if (GUILayout.Button("Send State & Wait", _buttonStyle))
                        SessionManager.CaptureAndSend();
                }
            }

            DrawSyncState(isHost);
        }

        private void DrawConnectionSteam()
        {
            bool isConnected = NetworkManager.Instance.IsConnected;
            bool inLobby     = SteamLobbyManager.InLobby;

            GUILayout.Label("\u2500\u2500 Network \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500", _labelStyle);

            // Mode + status
            string modeStr;
            Color statusCol;
            string statusStr;

            if (isConnected)
            {
                modeStr = NetworkManager.Instance.IsHost ? "STEAM (HOST)" : "STEAM (CLIENT)";
                statusCol = new Color(0.3f, 1f, 0.4f);
                statusStr = "Connected";
            }
            else if (inLobby)
            {
                modeStr = "STEAM (HOST)";
                statusCol = new Color(1f, 1f, 0.3f);
                statusStr = "In Lobby";
            }
            else
            {
                modeStr = "STEAM";
                statusCol = new Color(1f, 0.4f, 0.4f);
                statusStr = "Not in lobby";
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Mode: {modeStr}", _labelStyle, GUILayout.Width(160));
            var prevColor = GUI.color;
            GUI.color = statusCol;
            GUILayout.Label(statusStr, _labelStyle);
            GUI.color = prevColor;
            GUILayout.EndHorizontal();

            if (isConnected)
            {
                GUILayout.Label($"Ping: {NetworkManager.Instance.LastRttMs} ms", _labelStyle);
            }
            else if (inLobby)
            {
                GUILayout.Label($"Lobby: {SteamLobbyManager.MemberCount}/{Plugin.Instance.CfgMaxPlayers.Value} players", _labelStyle);
            }

            GUILayout.Space(4);

            if (isConnected)
            {
                // Connected state
                if (GUILayout.Button("Disconnect", _buttonStyle))
                    SteamLobbyManager.LeaveLobby();

                if (NetworkManager.Instance.IsHost)
                {
                    GUILayout.Space(4);
                    if (GUILayout.Button("Send State & Wait", _buttonStyle))
                        SessionManager.CaptureAndSend();
                }
            }
            else if (inLobby)
            {
                // In lobby — invite buttons are in the player list section
                if (GUILayout.Button("Leave Lobby", _buttonStyle))
                    SteamLobbyManager.LeaveLobby();
            }
            else
            {
                // Not in lobby — create one
                if (GUILayout.Button("Create Lobby", _buttonStyle))
                    SteamLobbyManager.CreateLobby();
            }

            DrawSyncState(NetworkManager.Instance.IsHost);
        }

        private void DrawSyncState(bool isHost)
        {
            var state = SimSyncManager.CurrentState;
            if (state == SimState.Idle) return;

            GUILayout.Space(2);

            switch (state)
            {
                case SimState.WaitingForClient:
                    GUILayout.Label($"Waiting for players ({SimSyncManager.ReadyCount}/{SimSyncManager.ExpectedCount} ready)", _warningStyle);
                    break;
                case SimState.Synchronized when GameTime.IsPaused():
                    GUILayout.Label("All players ready \u2014 unpause to begin", _successStyle);
                    break;
                case SimState.Synchronized:
                    GUILayout.Label("Sim synced", _successStyle);
                    break;
            }

            if (!isHost && SessionManager.IsReceiving)
            {
                GUILayout.Label("Receiving scene...", _warningStyle);
            }
        }

        // ── Player list section ──────────────────────────────────────────────

        private void DrawPlayerList()
        {
            GUILayout.Label("\u2500\u2500 Players \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500", _labelStyle);

            bool isHost = NetworkManager.Instance.IsHost;
            bool isSteam = Plugin.Instance.CfgTransport.Value == "Steam";

            // Invite buttons (host only, Steam only)
            if (isHost && isSteam && SteamLobbyManager.InLobby)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Invite to Blue", _buttonStyle))
                {
                    PlayerRegistry.PendingInviteTeam = 0;
                    SteamLobbyManager.InviteFriend();
                }
                if (GUILayout.Button("Invite to Red", _buttonStyle))
                {
                    PlayerRegistry.PendingInviteTeam = 1;
                    SteamLobbyManager.InviteFriend();
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
            }

            // Collect players by team
            var bluePlayers = new System.Collections.Generic.List<PlayerInfo>();
            var redPlayers = new System.Collections.Generic.List<PlayerInfo>();
            foreach (var kvp in PlayerRegistry.Players)
            {
                if (kvp.Value.TeamSide == 0)
                    bluePlayers.Add(kvp.Value);
                else
                    redPlayers.Add(kvp.Value);
            }

            // Blue team
            GUILayout.Label("BLUE TEAM", _sectionHeaderStyle);
            foreach (var p in bluePlayers)
                DrawPlayerEntry(p, isHost);

            GUILayout.Space(4);

            // Red team
            GUILayout.Label("RED TEAM", _sectionHeaderStyle);
            foreach (var p in redPlayers)
                DrawPlayerEntry(p, isHost);
        }

        private void DrawPlayerEntry(PlayerInfo p, bool isHost)
        {
            string tfLabel = p.AssignedTfNames.Count == 0
                ? "All"
                : $"{p.AssignedTfNames.Count} TF{(p.AssignedTfNames.Count > 1 ? "s" : "")}";
            string readyStr = p.IsReady ? " \u2713" : "";
            string localStr = p.PlayerId == PlayerRegistry.LocalPlayerId ? " (you)" : "";

            GUILayout.BeginHorizontal();
            GUILayout.Label($"  {p.DisplayName}{localStr}{readyStr}", _labelStyle, GUILayout.Width(100));

            if (isHost)
            {
                // TF dropdown button
                string btnText = $"TF: {tfLabel}";
                if (GUILayout.Button(btnText, _buttonStyle, GUILayout.Width(100)))
                {
                    if (_tfDropdownOpenFor == p.PlayerId)
                        _tfDropdownOpenFor = null;
                    else
                        _tfDropdownOpenFor = p.PlayerId;
                }

                // Team swap button
                string teamIcon = p.TeamSide == 0 ? "\u2192R" : "\u2192B";
                if (GUILayout.Button(teamIcon, _buttonStyle, GUILayout.Width(30)))
                {
                    PlayerRegistry.HostAssignTeam(p.PlayerId, (byte)(p.TeamSide == 0 ? 1 : 0));
                    _tfDropdownOpenFor = null;
                }
            }
            else
            {
                GUILayout.Label($"TF: {tfLabel}", _dimLabelStyle, GUILayout.Width(100));
            }

            GUILayout.EndHorizontal();

            // Draw dropdown list if open for this player
            if (isHost && _tfDropdownOpenFor == p.PlayerId)
                DrawTfDropdown(p);
        }

        private void DrawTfDropdown(PlayerInfo p)
        {
            var options = PlayerRegistry.GetTeamGroups(p.TeamSide);

            GUILayout.BeginVertical(GUI.skin.box, GUILayout.MaxWidth(PanelWidth - Margin * 2));

            // "All" option (clears selections)
            bool isAll = p.AssignedTfNames.Count == 0;
            if (GUILayout.Button(isAll ? "> All" : "  All", _buttonStyle))
                PlayerRegistry.HostAssignAll(p.PlayerId);

            // Individual formation/unit group options (toggle checkboxes)
            foreach (var (groupKey, displayName, unitCount) in options)
            {
                bool selected = p.AssignedTfNames.Contains(groupKey);
                string prefix = selected ? "[x] " : "[ ] ";
                string countStr = unitCount > 1 ? $" ({unitCount})" : "";
                string label = displayName + countStr;
                if (label.Length > 30) label = label.Substring(0, 27) + "...";
                if (GUILayout.Button($"{prefix}{label}", _buttonStyle))
                    PlayerRegistry.HostToggleTaskforce(p.PlayerId, groupKey);
            }

            // Done button to close
            if (GUILayout.Button("Done", _buttonStyle))
                _tfDropdownOpenFor = null;

            GUILayout.EndVertical();
        }

        // ── Time compression section ──────────────────────────────────────────

        private void DrawTimeControls()
        {
            GUILayout.Label("\u2500\u2500 Time Control \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500", _labelStyle);

            float tc      = GameTime.TimeCompression;
            bool  paused  = GameTime.IsPaused();
            bool  isHost  = Plugin.Instance.CfgIsHost.Value;
            bool  connected = NetworkManager.Instance.IsConnected;

            // Current state display
            string timeStr = paused ? "PAUSED" : $"{tc:0.#}x";
            GUILayout.Label($"Time: {timeStr}", _labelStyle);

            // Time buttons — always show, but client sends request to host
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("<<", _buttonStyle, GUILayout.Width(40)))
                TimeSyncManager.RequestDecrease();

            if (paused)
            {
                if (GUILayout.Button("\u25b6 Play", _buttonStyle))
                    TimeSyncManager.RequestUnpause();
            }
            else
            {
                if (GUILayout.Button("\u23f8 Pause", _buttonStyle))
                    TimeSyncManager.RequestPause();
            }

            if (GUILayout.Button(">>", _buttonStyle, GUILayout.Width(40)))
                TimeSyncManager.RequestIncrease();

            GUILayout.EndHorizontal();

            // Pending request indicator (default mode)
            if (TimeSyncManager.PendingRequest && !isHost)
                GUILayout.Label("\u27f3 Waiting for host...", _warningStyle);

            // Vote mode: waiting for other player to respond to our proposal
            if (TimeSyncManager.WaitingForVoteResponse)
                GUILayout.Label("\u27f3 Waiting for other player...", _warningStyle);
        }

        // ── Sync Health ─────────────────────────────────────────────────────

        private SyncStatus ComputeOverallStatus()
        {
            bool isPvP = Plugin.Instance.CfgPvP.Value;

            // Issues (red)
            if (!isPvP && DriftDetector.DriftLevel == DriftTier.Critical) return SyncStatus.Issues;
            if (!isPvP && DriftDetector.HardSyncRequested) return SyncStatus.Issues;
            if (StateApplier.OrphanCandidateCount > 3) return SyncStatus.Issues;
            if (ProjectileIdMapper.PendingHostCount + ProjectileIdMapper.PendingLocalCount > 5) return SyncStatus.Issues;
            if (isPvP && PvPFireAuth.SuppressedCount > 10) return SyncStatus.Issues;

            // Degraded (yellow)
            if (!isPvP && (DriftDetector.DriftLevel == DriftTier.Elevated || DriftDetector.DriftLevel == DriftTier.High)) return SyncStatus.Degraded;
            if (StateApplier.OrphanCandidateCount > 0) return SyncStatus.Degraded;
            if (ProjectileIdMapper.PendingHostCount + ProjectileIdMapper.PendingLocalCount > 0) return SyncStatus.Degraded;
            if (FlightOpsHandler.DeferredSpawnCount > 0) return SyncStatus.Degraded;
            if (isPvP && PvPFireAuth.SuppressedCount > 0) return SyncStatus.Degraded;
            if (!isPvP && CombatEventHandler.EventsNotFound > 0) return SyncStatus.Degraded;
            if (OrderDelayQueue.PendingCount > 3) return SyncStatus.Degraded;

            return SyncStatus.OK;
        }

        private GUIStyle StatusStyle(SyncStatus status)
        {
            switch (status)
            {
                case SyncStatus.Issues:   return _criticalStyle!;
                case SyncStatus.Degraded: return _elevatedStyle!;
                default:                  return _successStyle!;
            }
        }

        private SyncStatus SectionStatus_Units()
        {
            if (StateApplier.OrphanCandidateCount > 3) return SyncStatus.Issues;
            bool isPvP = Plugin.Instance.CfgPvP.Value;
            if (isPvP)
            {
                if (StateApplier.PvpShipDriftMax > 100f || StateApplier.PvpAirDriftMax > 200f) return SyncStatus.Issues;
                if (StateApplier.PvpShipDriftAvg > 20f || StateApplier.PvpAirDriftAvg > 40f) return SyncStatus.Degraded;
            }
            if (StateApplier.OrphanCandidateCount > 0) return SyncStatus.Degraded;
            return SyncStatus.OK;
        }

        private SyncStatus SectionStatus_Projectiles()
        {
            int pending = ProjectileIdMapper.PendingHostCount + ProjectileIdMapper.PendingLocalCount;
            if (pending > 5) return SyncStatus.Issues;
            if (pending > 0 || StateApplier.ProjectilesDestroyedByTimeout > 0) return SyncStatus.Degraded;
            return SyncStatus.OK;
        }

        private SyncStatus SectionStatus_MissileState()
        {
            if (MissileStateSyncHandler.JammedCount > 0 || MissileStateSyncHandler.TargetLostCount > 0) return SyncStatus.Degraded;
            return SyncStatus.OK;
        }

        private SyncStatus SectionStatus_FlightOps()
        {
            if (FlightOpsHandler.DeferredSpawnCount > 0) return SyncStatus.Degraded;
            return SyncStatus.OK;
        }

        private SyncStatus SectionStatus_FireAuth()
        {
            if (PvPFireAuth.SuppressedCount > 10) return SyncStatus.Issues;
            if (PvPFireAuth.SuppressedCount > 0) return SyncStatus.Degraded;
            return SyncStatus.OK;
        }

        private SyncStatus SectionStatus_Combat()
        {
            // In PvP, "not found" is expected — both sides resolve combat independently,
            // so the remote's event often arrives after the missile is already dead locally.
            if (!Plugin.Instance.CfgPvP.Value && CombatEventHandler.EventsNotFound > 0)
                return SyncStatus.Degraded;
            return SyncStatus.OK;
        }

        private SyncStatus SectionStatus_Drift()
        {
            if (DriftDetector.DriftLevel == DriftTier.Critical || DriftDetector.HardSyncRequested) return SyncStatus.Issues;
            if (DriftDetector.DriftLevel == DriftTier.Elevated || DriftDetector.DriftLevel == DriftTier.High) return SyncStatus.Degraded;
            return SyncStatus.OK;
        }

        private bool DrawSectionHeader(string label, bool foldout, SyncStatus status)
        {
            GUILayout.BeginHorizontal();
            string arrow = foldout ? "\u25be" : "\u25b8";
            if (GUILayout.Button($"{arrow} {label}", _sectionHeaderStyle))
                foldout = !foldout;
            GUILayout.FlexibleSpace();
            GUILayout.Label(status.ToString(), StatusStyle(status));
            GUILayout.EndHorizontal();
            return foldout;
        }

        private void DrawSyncHealth()
        {
            bool isPvP = Plugin.Instance.CfgPvP.Value;

            // Master header with overall status
            var overall = ComputeOverallStatus();
            GUILayout.BeginHorizontal();
            GUILayout.Label("\u2500\u2500 Sync Health \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500", _labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label(overall.ToString(), StatusStyle(overall));
            GUILayout.EndHorizontal();

            // Summary line
            GUILayout.Label($"RTT: {NetworkManager.Instance.LastRttMs} ms   Orders: {OrderDelayQueue.PendingCount} queued", _dimLabelStyle);

            GUILayout.Space(2);

            // ── Units ────────────────────────────────────────────────────────
            _foldUnits = DrawSectionHeader("Units", _foldUnits, SectionStatus_Units());
            if (_foldUnits)
            {
                if (isPvP)
                {
                    GUILayout.Label($"  Ships: own {_ownVessels}  enemy {_enemyVessels}  (remote {_remoteUnitCount})", _labelStyle);
                    GUILayout.Label($"  Subs:  own {_ownSubs}  enemy {_enemySubs}", _labelStyle);
                    GUILayout.Label($"  Air:   own {_ownAir}  enemy {_enemyAir}", _labelStyle);
                    if (_ownLand + _enemyLand > 0)
                        GUILayout.Label($"  Land:  own {_ownLand}  enemy {_enemyLand}", _labelStyle);

                    // Per-category position drift
                    var shipDriftStyle = StateApplier.PvpShipDriftMax > 100f ? _warningStyle
                        : StateApplier.PvpShipDriftAvg > 20f ? _elevatedStyle : _dimLabelStyle;
                    GUILayout.Label($"  Ship drift: {StateApplier.PvpShipDriftAvg:F1} avg / {StateApplier.PvpShipDriftMax:F1} max", shipDriftStyle);

                    var airDriftStyle = StateApplier.PvpAirDriftMax > 200f ? _warningStyle
                        : StateApplier.PvpAirDriftAvg > 40f ? _elevatedStyle : _dimLabelStyle;
                    GUILayout.Label($"  Air drift:  {StateApplier.PvpAirDriftAvg:F1} avg / {StateApplier.PvpAirDriftMax:F1} max", airDriftStyle);
                }
                else
                {
                    GUILayout.Label($"  Ships: {_ownVessels + _enemyVessels}   Subs: {_ownSubs + _enemySubs}", _labelStyle);
                    GUILayout.Label($"  Air: {_ownAir + _enemyAir}", _labelStyle);
                    if (_ownLand + _enemyLand > 0)
                        GUILayout.Label($"  Land: {_ownLand + _enemyLand}", _labelStyle);
                }
                if (StateApplier.OrphanCandidateCount > 0)
                    GUILayout.Label($"  Orphan candidates: {StateApplier.OrphanCandidateCount}", _warningStyle);
            }

            GUILayout.Space(2);

            // ── Projectiles ──────────────────────────────────────────────────
            _foldProjectiles = DrawSectionHeader("Projectiles", _foldProjectiles, SectionStatus_Projectiles());
            if (_foldProjectiles)
            {
                int totalMsl = _ownMissiles + _enemyMissiles;
                int totalTorp = _ownTorps + _enemyTorps;
                if (isPvP)
                {
                    GUILayout.Label($"  Missiles: {totalMsl} (own {_ownMissiles} / enemy {_enemyMissiles})", _labelStyle);
                    GUILayout.Label($"  Torpedoes: {totalTorp} (own {_ownTorps} / enemy {_enemyTorps})", _labelStyle);
                }
                else
                {
                    GUILayout.Label($"  Missiles: {totalMsl}   Torpedoes: {totalTorp}", _labelStyle);
                }
                GUILayout.Label($"  ID mapped: {ProjectileIdMapper.MappedCount}  Host pend: {ProjectileIdMapper.PendingHostCount}  Local pend: {ProjectileIdMapper.PendingLocalCount}  Stale purged: {ProjectileIdMapper.StalePurgedCount}", _dimLabelStyle);
                if (StateApplier.ProjectilesDestroyedByTimeout > 0)
                    GUILayout.Label($"  Disappeared: {StateApplier.ProjectilesDestroyedByTimeout}", _dimLabelStyle);
            }

            GUILayout.Space(2);

            // ── Missile State (PvP only) ─────────────────────────────────────
            if (isPvP)
            {
                _foldMissileState = DrawSectionHeader("Missile State (PvP)", _foldMissileState, SectionStatus_MissileState());
                if (_foldMissileState)
                {
                    GUILayout.Label($"  State sync: {MissileStateSyncHandler.LastAppliedCount} entries last cycle", _labelStyle);
                    GUILayout.Label($"  Jammed: {MissileStateSyncHandler.JammedCount}  Target lost: {MissileStateSyncHandler.TargetLostCount}", _dimLabelStyle);
                }
                GUILayout.Space(2);
            }

            // ── Flight Ops ───────────────────────────────────────────────────
            _foldFlightOps = DrawSectionHeader("Flight Ops", _foldFlightOps, SectionStatus_FlightOps());
            if (_foldFlightOps)
            {
                GUILayout.Label($"  Pipeline: {FlightOpsHandler.PipelineCount} aircraft", _labelStyle);
                GUILayout.Label($"  Deferred spawns: {FlightOpsHandler.DeferredSpawnCount}", _dimLabelStyle);
                GUILayout.Label($"  Synced carriers: {FlightOpsHandler.SyncedCarrierCount}", _dimLabelStyle);
            }

            GUILayout.Space(2);

            // ── Fire Auth (PvP only) ─────────────────────────────────────────
            if (isPvP)
            {
                _foldFireAuth = DrawSectionHeader("Fire Auth (PvP)", _foldFireAuth, SectionStatus_FireAuth());
                if (_foldFireAuth)
                {
                    GUILayout.Label($"  Active: {PvPFireAuth.ActiveAuthCount} shots authorized", _labelStyle);
                    if (PvPFireAuth.SuppressedCount > 0)
                        GUILayout.Label($"  Suppressed: {PvPFireAuth.SuppressedCount} unauthorized", _warningStyle);
                    else
                        GUILayout.Label($"  Suppressed: 0 unauthorized", _dimLabelStyle);
                }
                GUILayout.Space(2);
            }

            // ── Combat Events ────────────────────────────────────────────────
            _foldCombatEvents = DrawSectionHeader("Combat Events", _foldCombatEvents, SectionStatus_Combat());
            if (_foldCombatEvents)
            {
                GUILayout.Label($"  Received: {CombatEventHandler.EventsReceived}  Not found: {CombatEventHandler.EventsNotFound}", _labelStyle);
            }

            GUILayout.Space(2);

            // ── Drift (co-op only) ───────────────────────────────────────────
            if (!isPvP)
            {
                _foldDrift = DrawSectionHeader("Drift (co-op)", _foldDrift, SectionStatus_Drift());
                if (_foldDrift)
                {
                    // Drift level with tier-colored label
                    GUIStyle tierStyle;
                    switch (DriftDetector.DriftLevel)
                    {
                        case DriftTier.Elevated: tierStyle = _elevatedStyle!; break;
                        case DriftTier.High:     tierStyle = _warningStyle!;  break;
                        case DriftTier.Critical: tierStyle = _criticalStyle!; break;
                        default:                 tierStyle = _successStyle!;  break;
                    }

                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"  Drift: {DriftDetector.AvgPositionDrift:F1} / {DriftDetector.MaxPositionDrift:F1} max", _labelStyle, GUILayout.Width(180));
                    GUILayout.Label(DriftDetector.DriftLevel.ToString(), tierStyle);
                    GUILayout.EndHorizontal();

                    GUILayout.Label($"  Speed: {DriftDetector.SpeedDriftAvg:F1} kts  Hdg: {DriftDetector.HeadingDriftAvg:F1}\u00b0", _labelStyle);
                    GUILayout.Label($"  Units: \u00b1{DriftDetector.UnitCountDelta}  Lerp: {DriftDetector.EffectiveLerpFactor:F2}  Corrections: {DriftDetector.CorrectionCount}", _labelStyle);
                    GUILayout.Label($"  Trend: {DriftDetector.DriftTrend:F1}  Max ever: {DriftDetector.MaxDeltaSeen:F1}", _dimLabelStyle);

                    if (DriftDetector.HardSyncRequested)
                        GUILayout.Label("  Hard sync requested!", _criticalStyle);
                }
            }
        }
    }
}
