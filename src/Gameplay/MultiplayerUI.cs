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
        private GUIStyle? _sectionTitleStyle;
        private GUIStyle? _dimLabelStyle;
        private GUIStyle? _separatorStyle;
        private GUIStyle? _popupBoxStyle;
        private GUIStyle? _teamLabelStyle;
        private GUIStyle? _dropdownBoxStyle;
        private GUIStyle? _collapseButtonStyle;
        private bool _stylesInit = false;

        // Procedural background textures (created once in InitStyles)
        private Texture2D? _panelBgTex;
        private Texture2D? _sectionBgTex;
        private Texture2D? _btnNormalTex;
        private Texture2D? _btnHoverTex;
        private Texture2D? _btnActiveTex;
        private Texture2D? _sepTex;
        private Texture2D? _alertBgTex;
        private Texture2D? _dropdownBgTex;
        private Texture2D? _sectionTitleBgTex;

        // Cached unit counts (refreshed every 0.5s instead of every OnGUI frame)
        private float _unitCountTimer;
        private int _ownVessels, _ownSubs, _ownAir, _ownLand, _ownMissiles, _ownTorps;
        private int _enemyVessels, _enemySubs, _enemyAir, _enemyLand, _enemyMissiles, _enemyTorps;
        private int _remoteUnitCount;

        // Reflection for missile/torpedo ownership
        private static readonly FieldInfo _launchPlatformField =
            AccessTools.Field(typeof(WeaponBase), "_launchPlatform");

        private const int PanelWidth  = 310;
        private const int Margin     = 10;

        // Auto-sized panel: track content height from previous frame
        private float _contentHeight = 200f;
        private Vector2 _scrollPos;

        // Foldout state for sync health sections
        private bool _foldUnits = true;
        private bool _foldProjectiles, _foldMissileState, _foldFlightOps;
        private bool _foldFireAuth, _foldCombatEvents, _foldDrift;

        // Toggle for showing/hiding sync health section panels
        private bool _syncPanelsVisible = false;

        // TF dropdown state
        private byte? _tfDropdownOpenFor; // which player's dropdown is open, null = none

        // Panel expand/collapse state (clickable header toggle)
        private bool _panelExpanded = true;

        // Scroll hysteresis — prevents flicker when content is near the boundary
        private bool _scrollActive = false;

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

            // ── Procedural background textures ──────────────────────────────
            _panelBgTex      = MakeRoundedTex(64, 64, new Color(0.055f, 0.08f,  0.130f, 0.68f), 8);
            _sectionBgTex    = MakeRoundedTex(32, 32, new Color(0.095f, 0.135f, 0.205f, 0.55f), 4);
            _sectionTitleBgTex = MakeRoundedTex(32, 32, new Color(0.10f, 0.16f, 0.255f, 0.52f), 4);
            _btnNormalTex    = MakeRoundedTex(32, 32, new Color(0.13f,  0.195f, 0.315f, 0.62f), 4);
            _btnHoverTex     = MakeRoundedTex(32, 32, new Color(0.19f,  0.275f, 0.430f, 0.75f), 4);
            _btnActiveTex    = MakeRoundedTex(32, 32, new Color(0.25f,  0.375f, 0.560f, 0.85f), 4);
            _sepTex          = MakeTex(4, 4, new Color(0.22f,  0.32f,  0.460f, 0.40f));
            _alertBgTex      = MakeRoundedTex(64, 64, new Color(0.10f,  0.065f, 0.065f, 0.78f), 8);
            _dropdownBgTex   = MakeRoundedTex(32, 32, new Color(0.07f,  0.10f,  0.155f, 0.72f), 6);

            // ── Panel / box (main container) ─────────────────────────────────
            _boxStyle = new GUIStyle()
            {
                padding = new RectOffset(10, 10, 10, 10),
                border  = new RectOffset(8, 8, 8, 8),
                normal  = { background = _panelBgTex },
            };

            // ── Vote popup box (alert style) ─────────────────────────────────
            _popupBoxStyle = new GUIStyle()
            {
                padding = new RectOffset(14, 14, 12, 12),
                border  = new RectOffset(8, 8, 8, 8),
                normal  = { background = _alertBgTex },
            };

            // ── Title / version header ───────────────────────────────────────
            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize  = 14,
                padding   = new RectOffset(0, 0, 2, 2),
                normal    = { textColor = new Color(0.82f, 0.91f, 1.00f) },
            };

            // ── Regular body label ───────────────────────────────────────────
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal   = { textColor = new Color(0.78f, 0.85f, 0.92f) },
            };

            // ── Dim / secondary text ─────────────────────────────────────────
            _dimLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                normal   = { textColor = new Color(0.46f, 0.52f, 0.62f) },
            };

            // ── Section divider title (Network / Players / Time Control) ─────
            _sectionTitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize  = 11,
                padding   = new RectOffset(6, 6, 4, 4),
                margin    = new RectOffset(0, 0, 2, 2),
                normal    = { textColor  = new Color(0.50f, 0.80f, 0.98f),
                              background = _sectionTitleBgTex },
            };

            // ── Foldout section header (Sync Health subsections) ─────────────
            _sectionHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize  = 11,
                padding   = new RectOffset(4, 4, 3, 3),
                normal    = { textColor  = new Color(0.48f, 0.76f, 0.96f),
                              background = _sectionBgTex },
            };

            // ── Team label (BLUE TEAM / RED TEAM) ────────────────────────────
            _teamLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize  = 10,
                padding   = new RectOffset(4, 4, 3, 3),
                normal    = { textColor = new Color(0.46f, 0.52f, 0.62f) },
            };

            // ── Status / badge text ──────────────────────────────────────────
            _warningStyle  = MakeBoldLabel(new Color(1.00f, 0.72f, 0.15f)); // Amber
            _successStyle  = MakeBoldLabel(new Color(0.28f, 0.95f, 0.48f)); // Green
            _elevatedStyle = MakeBoldLabel(new Color(1.00f, 1.00f, 0.28f)); // Yellow
            _criticalStyle = MakeBoldLabel(new Color(1.00f, 0.28f, 0.28f)); // Red

            // ── Buttons ──────────────────────────────────────────────────────
            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize    = 11,
                fontStyle   = FontStyle.Bold,
                fixedHeight = 24,
                padding     = new RectOffset(8, 8, 4, 4),
                border      = new RectOffset(4, 4, 4, 4),
                normal      = { background = _btnNormalTex,
                                textColor  = new Color(0.80f, 0.88f, 0.98f) },
                hover       = { background = _btnHoverTex,
                                textColor  = new Color(0.92f, 0.96f, 1.00f) },
                active      = { background = _btnActiveTex,
                                textColor  = new Color(1.00f, 1.00f, 1.00f) },
            };

            // ── Thin horizontal separator ────────────────────────────────────
            _separatorStyle = new GUIStyle()
            {
                fixedHeight = 1,
                margin      = new RectOffset(0, 0, 5, 5),
                normal      = { background = _sepTex },
            };

            // ── TF dropdown box ─────────────────────────────────────────────
            _dropdownBoxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(4, 4, 4, 4),
                border  = new RectOffset(6, 6, 6, 6),
                normal  = { background = _dropdownBgTex },
            };

            // ── Collapse/expand toggle button (compact, no bg) ────────────
            _collapseButtonStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize    = 16,
                fontStyle   = FontStyle.Bold,
                fixedWidth  = 24,
                fixedHeight = 22,
                alignment   = TextAnchor.MiddleCenter,
                padding     = new RectOffset(0, 0, 0, 0),
                normal      = { textColor = new Color(0.50f, 0.70f, 0.92f) },
                hover       = { textColor = new Color(0.75f, 0.90f, 1.00f) },
            };

            _stylesInit = true;
        }

        private static GUIStyle MakeBoldLabel(Color color) => new GUIStyle(GUI.skin.label)
        {
            fontSize  = 11,
            fontStyle = FontStyle.Bold,
            normal    = { textColor = color },
        };

        private static Texture2D MakeTex(int width, int height, Color col)
        {
            var pix = new Color[width * height];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            var tex = new Texture2D(width, height);
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Creates a texture with rounded corners. The <paramref name="radius"/> controls
        /// corner curvature (in pixels). Pixels outside the rounded rect are fully transparent.
        /// </summary>
        private static Texture2D MakeRoundedTex(int width, int height, Color fill, int radius)
        {
            var tex = new Texture2D(width, height, TextureFormat.ARGB32, false);
            var clear = new Color(0, 0, 0, 0);
            int r2 = radius * radius;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Determine the closest corner center for SDF
                    int cx = x < radius ? radius : (x >= width  - radius ? width  - 1 - radius : x);
                    int cy = y < radius ? radius : (y >= height - radius ? height - 1 - radius : y);
                    int dx = x - cx;
                    int dy = y - cy;
                    float dist2 = dx * dx + dy * dy;

                    if (dist2 <= r2)
                    {
                        // Inside the rounded rect — apply slight AA at the edge
                        float dist = Mathf.Sqrt(dist2);
                        float edge = Mathf.Clamp01(radius - dist + 0.5f);
                        tex.SetPixel(x, y, new Color(fill.r, fill.g, fill.b, fill.a * edge));
                    }
                    else
                    {
                        tex.SetPixel(x, y, clear);
                    }
                }
            }
            tex.Apply();
            return tex;
        }

        // Draws a full-width styled section title bar (Network, Players, etc.)
        private void DrawSectionTitle(string icon, string title)
        {
            GUILayout.Space(2);
            GUILayout.Label($"{icon}  {title}", _sectionTitleStyle!, GUILayout.ExpandWidth(true));
            GUILayout.Box("", _separatorStyle!, GUILayout.ExpandWidth(true));
        }

        private void OnGUI()
        {
            InitStyles();

            // Vote popup is always visible, even when the main panel is hidden
            DrawTimeVotePopup();

            if (!_visible) return;

            float x = Screen.width - PanelWidth - Margin;
            float maxHeight = Screen.height - Margin * 2;
            // Hysteresis: enter scroll at maxHeight, exit only when content shrinks 30px below
            bool needsScroll = _scrollActive
                ? _contentHeight > maxHeight - 30f
                : _contentHeight > maxHeight;
            _scrollActive = needsScroll;

            // Outer area is just for positioning — transparent, full available height
            GUILayout.BeginArea(new Rect(x, Margin, PanelWidth, maxHeight));

            if (needsScroll)
            {
                _scrollPos = GUILayout.BeginScrollView(_scrollPos, false, false,
                    GUIStyle.none, GUI.skin.verticalScrollbar, _boxStyle!,
                    GUILayout.MaxHeight(maxHeight));
                // Inner group so we can measure true content height (not clipped by scroll)
                GUILayout.BeginVertical();
            }
            else
            {
                // Styled vertical group auto-sizes to content — no stale height
                GUILayout.BeginVertical(_boxStyle);
            }

            DrawHeader();

            // If panel is collapsed, only show the header
            if (!_panelExpanded)
            {
                GUILayout.EndVertical();
                if (Event.current.type == EventType.Repaint)
                    _contentHeight = GUILayoutUtility.GetLastRect().height;

                if (needsScroll)
                    GUILayout.EndScrollView();

                GUILayout.EndArea();
                return;
            }

            GUILayout.Space(4);
            DrawConnection();
            GUILayout.Space(6);
            bool isSteamLobby = Plugin.Instance.CfgTransport.Value == "Steam" && SteamLobbyManager.InLobby;
            if (NetworkManager.Instance.IsConnected || isSteamLobby)
            {
                DrawPlayerList();
                GUILayout.Space(6);
            }
            DrawTimeControls();
            GUILayout.Space(6);

            if (NetworkManager.Instance.IsConnected)
                DrawSyncHealth();

            // End the inner vertical — GetLastRect gives true content height
            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint)
                _contentHeight = GUILayoutUtility.GetLastRect().height;

            if (needsScroll)
                GUILayout.EndScrollView();

            GUILayout.EndArea();
        }

        // ── Time Vote Popup ──────────────────────────────────────────────────

        private void DrawTimeVotePopup()
        {
            if (!TimeSyncManager.HasPendingProposal) return;

            const float popupWidth  = 320f;
            const float popupHeight = 110f;
            float px = (Screen.width  - popupWidth)  / 2f;
            float py = (Screen.height - popupHeight) / 2f;

            GUILayout.BeginArea(new Rect(px, py, popupWidth, popupHeight), _popupBoxStyle);

            bool isHost = Plugin.Instance.CfgIsHost.Value;
            string who = isHost ? "Client" : "Host";

            GUILayout.Label($"\u23f1  Time Change Request", _sectionTitleStyle!, GUILayout.ExpandWidth(true));
            GUILayout.Space(6);
            GUILayout.Label($"{who} proposes:  {TimeSyncManager.ProposalDescription}", _warningStyle);
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("\u2714  Agree", _buttonStyle))
                TimeSyncManager.AcceptProposal();
            GUILayout.Space(6);
            if (GUILayout.Button("\u2716  Decline", _buttonStyle))
                TimeSyncManager.DeclineProposal();
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }

        // ── Header ────────────────────────────────────────────────────────────

        private void DrawHeader()
        {
            GUILayout.BeginHorizontal();

            // Collapse/expand toggle icon
            string collapseIcon = _panelExpanded ? "\u25bc" : "\u25b6";
            if (GUILayout.Button(collapseIcon, _collapseButtonStyle))
                _panelExpanded = !_panelExpanded;

            // Show overall sync status dot in header (green/yellow/red)
            if (NetworkManager.Instance.IsConnected)
            {
                var overall = ComputeOverallStatus();
                GUILayout.Label("\u25cf", StatusStyle(overall), GUILayout.Width(14));
            }

            GUILayout.Label($"SeaPower MP  v{PluginInfo.PLUGIN_VERSION}", _headerStyle);
            GUILayout.FlexibleSpace();
            if (Plugin.Instance.CfgPvP.Value)
                GUILayout.Label("PvP", _warningStyle);
            GUILayout.EndHorizontal();

            if (_panelExpanded)
                GUILayout.Box("", _separatorStyle!, GUILayout.ExpandWidth(true));
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

            DrawSectionTitle("\u2302", "NETWORK");

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
            GUILayout.FlexibleSpace();
            var prevColor = GUI.color;
            GUI.color = statusCol;
            GUILayout.Label($"\u25cf  {statusStr}", _labelStyle);
            GUI.color = prevColor;
            GUILayout.EndHorizontal();

            // Ping
            if (isConnected)
            {
                GUILayout.Label($"  Ping: {NetworkManager.Instance.LastRttMs} ms", _dimLabelStyle);
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
            }

            DrawSyncState(isHost);
        }

        private void DrawConnectionSteam()
        {
            bool isConnected = NetworkManager.Instance.IsConnected;
            bool inLobby     = SteamLobbyManager.InLobby;
            string modeStr;
            Color  statusCol;
            string statusStr;

            DrawSectionTitle("\u2302", "NETWORK");

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
            GUILayout.FlexibleSpace();
            var prevColor = GUI.color;
            GUI.color = statusCol;
            GUILayout.Label($"\u25cf  {statusStr}", _labelStyle);
            GUI.color = prevColor;
            GUILayout.EndHorizontal();

            if (isConnected)
            {
                GUILayout.Label($"  Ping: {NetworkManager.Instance.LastRttMs} ms", _dimLabelStyle);
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
            DrawSectionTitle("\u2694", "PLAYERS");

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
            GUILayout.BeginHorizontal();
            GUILayout.Label("\u25cf", _sectionHeaderStyle, GUILayout.Width(14));
            GUILayout.Label("BLUE TEAM", _teamLabelStyle);
            GUILayout.EndHorizontal();
            GUILayout.Box("", _separatorStyle!, GUILayout.ExpandWidth(true));
            foreach (var p in bluePlayers)
                DrawPlayerEntry(p, isHost);

            GUILayout.Space(4);

            // Red team
            GUILayout.BeginHorizontal();
            GUILayout.Label("\u25cf", _criticalStyle!, GUILayout.Width(14));
            GUILayout.Label("RED TEAM", _teamLabelStyle);
            GUILayout.EndHorizontal();
            GUILayout.Box("", _separatorStyle!, GUILayout.ExpandWidth(true));
            foreach (var p in redPlayers)
                DrawPlayerEntry(p, isHost);
        }

        private void DrawPlayerEntry(PlayerInfo p, bool isHost)
        {
            string tfLabel = p.AssignedTfNames.Count == 0
                ? "All Forces"
                : $"{p.AssignedTfNames.Count} TF{(p.AssignedTfNames.Count > 1 ? "s" : "")}";
            string readyStr = p.IsReady ? " \u2713" : "";
            string localStr = p.PlayerId == PlayerRegistry.LocalPlayerId ? " (you)" : "";

            // Row 1: name + ready indicator
            GUILayout.Label($"  {p.DisplayName}{localStr}{readyStr}", _labelStyle);

            if (isHost)
            {
                // Row 2: force assignment + swap team
                GUILayout.BeginHorizontal();
                GUILayout.Space(12);

                // TF dropdown button
                if (GUILayout.Button(tfLabel, _buttonStyle, GUILayout.Width(120)))
                {
                    if (_tfDropdownOpenFor == p.PlayerId)
                        _tfDropdownOpenFor = null;
                    else
                        _tfDropdownOpenFor = p.PlayerId;
                }

                GUILayout.Space(4);

                // Team swap button — clear label with color hint
                string swapLabel = p.TeamSide == 0 ? "\u21e8 Move to Red" : "\u21e8 Move to Blue";
                if (GUILayout.Button(swapLabel, _buttonStyle))
                {
                    PlayerRegistry.HostAssignTeam(p.PlayerId, (byte)(p.TeamSide == 0 ? 1 : 0));
                    _tfDropdownOpenFor = null;
                }

                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label($"    {tfLabel}", _dimLabelStyle);
            }

            GUILayout.Space(2);

            // Draw dropdown list if open for this player
            if (isHost && _tfDropdownOpenFor == p.PlayerId)
                DrawTfDropdown(p);
        }

        private void DrawTfDropdown(PlayerInfo p)
        {
            var options = PlayerRegistry.GetTeamGroups(p.TeamSide);

            GUILayout.BeginVertical(_dropdownBoxStyle!, GUILayout.MaxWidth(PanelWidth - Margin * 2));

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
            DrawSectionTitle("\u23f1", "TIME CONTROL");

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
            string arrow  = foldout ? "\u25bc" : "\u25b6";
            string dot    = status == SyncStatus.OK ? "\u25cf" : status == SyncStatus.Degraded ? "\u25cf" : "\u25cf";
            GUIStyle dotStyle = StatusStyle(status);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button($" {arrow}  {label}", _sectionHeaderStyle!, GUILayout.ExpandWidth(true)))
                foldout = !foldout;
            GUILayout.Label(dot, dotStyle, GUILayout.Width(16));
            GUILayout.EndHorizontal();
            return foldout;
        }

        private void DrawSyncHealth()
        {
            bool isPvP = Plugin.Instance.CfgPvP.Value;

            // Master header — foldout toggle for all sync panels
            var overall = ComputeOverallStatus();
            DrawSectionTitle("\u21bb", "SYNC HEALTH");

            // Resync tip
            GUILayout.Space(4);
            GUILayout.Label("  Tip: Press Ctrl+F10 to force a resync", _dimLabelStyle);

            _syncPanelsVisible = DrawSectionHeader("Details", _syncPanelsVisible, overall);
            if (!_syncPanelsVisible) return;

            // Summary line
            GUILayout.Label($"  RTT: {NetworkManager.Instance.LastRttMs} ms  \u00b7  Orders queued: {OrderDelayQueue.PendingCount}", _dimLabelStyle);

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