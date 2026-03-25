using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Transport;
using System.Reflection;
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

        // Lock indicator: track last known locked ID to avoid per-frame FindObjectsOfType
        private int _lastLockedUnitId = 0;

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

            foreach (var v in UnitRegistry.Vessels)
            {
                if (v == null) continue;
                if (isPvP && playerTf != null && v._taskforce == playerTf) _ownVessels++;
                else _enemyVessels++;
            }
            foreach (var s in UnitRegistry.Submarines)
            {
                if (s == null) continue;
                if (isPvP && playerTf != null && s._taskforce == playerTf) _ownSubs++;
                else _enemySubs++;
            }
            foreach (var a in UnitRegistry.AircraftList)
            {
                if (a == null) continue;
                if (isPvP && playerTf != null && a._taskforce == playerTf) _ownAir++;
                else _enemyAir++;
            }
            foreach (var h in UnitRegistry.Helicopters)
            {
                if (h == null) continue;
                if (isPvP && playerTf != null && h._taskforce == playerTf) _ownAir++;
                else _enemyAir++;
            }
            foreach (var l in UnitRegistry.LandUnits)
            {
                if (l == null) continue;
                if (isPvP && playerTf != null && l._taskforce == playerTf) _ownLand++;
                else _enemyLand++;
            }
            foreach (var m in UnitRegistry.Missiles)
            {
                if (m == null) continue;
                var launcher = StateSerializer.GetLaunchPlatform(m);
                if (isPvP && playerTf != null && launcher != null && launcher._taskforce == playerTf) _ownMissiles++;
                else _enemyMissiles++;
            }
            foreach (var t in UnitRegistry.Torpedoes)
            {
                if (t == null) continue;
                var launcher = StateSerializer.GetLaunchPlatform(t);
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

            // ── Lock indicator: highlight the unit the remote player has selected ──
            int lockedId = UnitLockManager.RemoteLockedUnitId;
            if (lockedId != 0 && NetworkManager.Instance.IsConnected && !Plugin.Instance.CfgPvP.Value)
            {
                // Refresh cached unit reference only when the locked ID changes
                if (lockedId != _lastLockedUnitId)
                {
                    _lastLockedUnitId = lockedId;
                    UnitLockManager.SetRemoteLockedUnit(null);
                    var found = StateSerializer.FindById(lockedId);
                    if (found != null)
                        UnitLockManager.SetRemoteLockedUnit(found);
                }

                var lockedUnit = UnitLockManager.RemoteLockedUnit;
                if (lockedUnit != null && Camera.main != null)
                {
                    Vector3 screenPos = Camera.main.WorldToScreenPoint(lockedUnit.transform.position);
                    if (screenPos.z > 0) // unit is in front of camera
                    {
                        float guiX = screenPos.x - 30f;
                        float guiY = Screen.height - screenPos.y - 30f;
                        Color prev = GUI.color;
                        GUI.color = new Color(0.8f, 0.8f, 0.8f, 0.9f);
                        GUI.Label(new Rect(guiX, guiY, 80f, 20f), "[BUSY]");
                        GUI.color = prev;
                    }
                }

                // Show notification if player just tried to select this unit
                float blockedAt = UnitLockManager.SelectionBlockedAt;
                float elapsed = UnityEngine.Time.time - blockedAt;
                if (elapsed < 5f)
                {
                    float alpha = 1f - (elapsed / 5f);
                    Color prev2 = GUI.color;
                    GUI.color = new Color(1f, 0.6f, 0.2f, alpha);
                    float msgW = 360f;
                    float msgH = 36f;
                    var _notifStyle = new GUIStyle(GUI.skin.label)
                    {
                        fontSize  = 16,
                        fontStyle = FontStyle.Bold,
                        alignment = TextAnchor.MiddleCenter,
                    };
                    GUI.Label(new Rect((Screen.width - msgW) * 0.5f, Screen.height * 0.15f, msgW, msgH),
                        "UNIT IS BUSY - selected by your partner!", _notifStyle);
                    GUI.color = prev2;
                }
            }
            else if (lockedId == 0 && _lastLockedUnitId != 0)
            {
                _lastLockedUnitId = 0;
                UnitLockManager.SetRemoteLockedUnit(null);
            }
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
            else
                GUILayout.Label("Co-op", _successStyle);
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
            bool isHostRunning = NetworkManager.Instance.IsHostRunning;
            string statusStr;
            Color statusCol;

            GUILayout.Label("\u2500\u2500 Network \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500", _labelStyle);

            // Mode + status
            string modeStr   = isHost ? "HOST" : "CLIENT";

            if (isConnected)
            {
                statusStr = "Connected";
                statusCol = new Color(0.3f, 1f, 0.4f); //Green
            }
            else if (isHostRunning)
            {
                statusStr = "Listening";
                statusCol = new Color(1f, 0.7f, 0.2f); //Same color as _warningStyle
            }
            else
            {
                statusStr = "Disconnected";
                statusCol = new Color(1f, 0.4f, 0.4f); //Red
            }

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
                //Adjust button verbage depending on if the game is serving or a client
                string btnLabel;
                if (isHost && !isHostRunning)
                {
                    btnLabel = "Start Hosting";
                }    
                else if (isHost && isHostRunning)
                {
                    btnLabel = "Stop Hosting";
                }
                else
                {
                    btnLabel = "Connect";
                }

                if (GUILayout.Button(btnLabel, _buttonStyle))
                {
                    if (isHost && !isHostRunning)
                    {
                        NetworkManager.Instance.StartHost(Plugin.Instance.CfgPort.Value);
                    }
                    else if (isHost && isHostRunning)
                    {
                        NetworkManager.Instance.Stop();
                    }
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

                DrawTaskforceAssignment();
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
                GUILayout.Label($"Lobby: {SteamLobbyManager.MemberCount}/2 players", _labelStyle);
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

                DrawTaskforceAssignment();
            }
            else if (inLobby)
            {
                // In lobby, waiting for peer
                if (GUILayout.Button("Invite Friend", _buttonStyle))
                    SteamLobbyManager.InviteFriend();

                GUILayout.Space(2);

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

        private void DrawTaskforceAssignment()
        {
            if (Plugin.Instance.CfgPvP.Value) return; // Co-op only
            if (!NetworkManager.Instance.IsConnected) return;

            GUILayout.Space(4);
            GUILayout.Label("\u2500\u2500 Task Force Assignment \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500", _labelStyle);

            bool isHost = Plugin.Instance.CfgIsHost.Value;
            var assigned = TaskforceAssignmentManager.ClientAssignedTfType;

            if (isHost)
            {
                GUILayout.Label($"Client assigned: {(assigned == Taskforce.TfType.None ? "All Units" : assigned.ToString())}", _labelStyle);
                GUILayout.Space(2);

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("All Units", _buttonStyle))
                    TaskforceAssignmentManager.HostAssign(Taskforce.TfType.None);
                if (GUILayout.Button("TF Player", _buttonStyle))
                    TaskforceAssignmentManager.HostAssign(Taskforce.TfType.Player);
                if (GUILayout.Button("TF Allied", _buttonStyle))
                    TaskforceAssignmentManager.HostAssign(Taskforce.TfType.Enemy);
                GUILayout.EndHorizontal();
            }
            else
            {
                string desc = assigned == Taskforce.TfType.None
                    ? "All friendly units"
                    : $"Task Force: {assigned}";
                GUILayout.Label($"Your control: {desc}", _successStyle);
            }
        }

        private void DrawSyncState(bool isHost)
        {
            var state = SimSyncManager.CurrentState;
            if (state == SimState.Idle) return;

            GUILayout.Space(2);

            switch (state)
            {
                case SimState.WaitingForClient:
                    GUILayout.Label("Waiting for client to load...", _warningStyle);
                    break;
                case SimState.Synchronized when GameTime.IsPaused():
                    GUILayout.Label("Client ready \u2014 unpause to begin", _successStyle);
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
