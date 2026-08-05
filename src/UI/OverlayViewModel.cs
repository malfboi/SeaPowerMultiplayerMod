using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using Noesis;
using SeaPower;
using SeapowerMultiplayer.Transport;
using UnityEngine;
using Brush = Noesis.Brush;
using Color = Noesis.Color;
using DelegateCommand = SeaPower.DelegateCommand;

namespace SeapowerMultiplayer.UI
{
    /// <summary>
    /// Everything the overlay binds to.
    ///
    /// The state it exposes lives in statics and singletons across the mod, and
    /// the IMGUI overlay read it fresh on every OnGUI pass. Noesis is retained
    /// mode, so instead it is pulled here on a timer and pushed out as change
    /// notifications - <see cref="Set{T}"/> raises only on a real change, which
    /// keeps Noesis from re-rendering the panel every tick.
    ///
    /// Values are exposed pre-formatted (strings, Visibility, Brush) so the XAML
    /// needs no converters.
    /// </summary>
    public class OverlayViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
        {
            if (Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        // ── Palette ──────────────────────────────────────────────────────────
        // Mirrors the colours the IMGUI overlay used for status text.

        private static readonly Brush Ok       = Frozen(0.30f, 1.00f, 0.40f);
        private static readonly Brush Warn     = Frozen(1.00f, 0.70f, 0.20f);
        private static readonly Brush Critical = Frozen(1.00f, 0.40f, 0.40f);
        private static readonly Brush Dim      = Frozen(0.50f, 0.58f, 0.70f);

        private static Brush Frozen(float r, float g, float b)
        {
            var brush = new SolidColorBrush(Color.FromScRgb(1f, r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

        // ── Commands ─────────────────────────────────────────────────────────

        public DelegateCommand ToggleExpandedCommand   { get; }
        public DelegateCommand ToggleSettingsCommand   { get; }
        public DelegateCommand ToggleAdvancedCommand   { get; }
        public DelegateCommand ToggleDetailsCommand    { get; }
        public DelegateCommand ToggleUnitsCommand      { get; }
        public DelegateCommand ToggleProjectilesCommand{ get; }
        public DelegateCommand ToggleCountersCommand   { get; }

        public DelegateCommand HostLobbyCommand        { get; }
        public DelegateCommand JoinClipboardCommand    { get; }
        public DelegateCommand CopyCodeCommand         { get; }
        public DelegateCommand InviteFriendCommand     { get; }
        public DelegateCommand LeaveLobbyCommand       { get; }
        public DelegateCommand SendStateCommand        { get; }
        public DelegateCommand LiteNetPrimaryCommand   { get; }
        public DelegateCommand DisconnectCommand       { get; }

        public DelegateCommand TimeDecreaseCommand     { get; }
        public DelegateCommand TimeIncreaseCommand     { get; }
        public DelegateCommand TimeTogglePauseCommand  { get; }
        public DelegateCommand VoteAgreeCommand        { get; }
        public DelegateCommand VoteDeclineCommand      { get; }

        public DelegateCommand ReconnectNowCommand     { get; }
        public DelegateCommand ReinviteCommand         { get; }
        public DelegateCommand AbandonSessionCommand   { get; }

        public DelegateCommand JoinDiscordCommand      { get; }
        public DelegateCommand OpenWorkshopCommand     { get; }
        public DelegateCommand DismissMismatchCommand  { get; }
        public DelegateCommand DismissFatalCommand     { get; }
        public DelegateCommand ResetSettingsCommand    { get; }

        public DelegateCommand EnableDiagnosticsCommand  { get; }
        public DelegateCommand DeclineDiagnosticsCommand { get; }

        public OverlayViewModel()
        {
            ToggleExpandedCommand    = new DelegateCommand(_ => PanelExpanded    = !PanelExpanded);
            ToggleSettingsCommand    = new DelegateCommand(_ => SettingsExpanded = !SettingsExpanded);
            ToggleAdvancedCommand    = new DelegateCommand(_ => AdvancedExpanded = !AdvancedExpanded);
            ToggleDetailsCommand     = new DelegateCommand(_ => DetailsExpanded  = !DetailsExpanded);
            ToggleUnitsCommand       = new DelegateCommand(_ => UnitsExpanded    = !UnitsExpanded);
            ToggleProjectilesCommand = new DelegateCommand(_ => ProjectilesExpanded = !ProjectilesExpanded);
            ToggleCountersCommand    = new DelegateCommand(_ => CountersExpanded = !CountersExpanded);

            HostLobbyCommand     = new DelegateCommand(_ => SteamLobbyManager.CreateLobby());
            InviteFriendCommand  = new DelegateCommand(_ => SteamLobbyManager.InviteFriend());
            LeaveLobbyCommand    = new DelegateCommand(_ => SteamLobbyManager.LeaveLobby());
            SendStateCommand     = new DelegateCommand(_ => SessionManager.CaptureAndSend());

            CopyCodeCommand = new DelegateCommand(_ =>
            {
                UnityEngine.GUIUtility.systemCopyBuffer = SteamLobbyManager.ShareCode;
                SetLobbyMsg("Code copied to clipboard");
            });

            JoinClipboardCommand = new DelegateCommand(_ =>
            {
                string code = UnityEngine.GUIUtility.systemCopyBuffer;
                SetLobbyMsg(SteamLobbyManager.TryJoinByCode(code, out string error)
                    ? "Joining lobby..."
                    : error);
            });

            DisconnectCommand = new DelegateCommand(_ =>
            {
                if (Plugin.Instance.CfgTransport.Value == "Steam") SteamLobbyManager.LeaveLobby();
                else NetworkManager.Instance.Stop();
            });

            // Direct-IP path, kept for dev builds; the workshop build forces Steam.
            LiteNetPrimaryCommand = new DelegateCommand(_ =>
            {
                var nm = NetworkManager.Instance;
                bool isHost = Plugin.Instance.CfgIsHost.Value;
                if (isHost && !nm.IsHostRunning) nm.StartHost(Plugin.Instance.CfgPort.Value);
                else if (isHost) nm.Stop();
                else nm.StartClient(Plugin.Instance.CfgHostIP.Value, Plugin.Instance.CfgPort.Value);
            });

            TimeDecreaseCommand = new DelegateCommand(_ => TimeSyncManager.RequestDecrease());
            TimeIncreaseCommand = new DelegateCommand(_ => TimeSyncManager.RequestIncrease());
            TimeTogglePauseCommand = new DelegateCommand(_ =>
            {
                if (GameTime.IsPaused()) TimeSyncManager.RequestUnpause();
                else TimeSyncManager.RequestPause();
            });
            VoteAgreeCommand   = new DelegateCommand(_ => TimeSyncManager.AcceptProposal());
            VoteDeclineCommand = new DelegateCommand(_ => TimeSyncManager.DeclineProposal());

            ReconnectNowCommand   = new DelegateCommand(_ => ReconnectManager.BeginReconnect());
            ReinviteCommand       = new DelegateCommand(_ => SteamLobbyManager.InviteFriend());
            AbandonSessionCommand = new DelegateCommand(_ => ReconnectManager.AbandonSession());

            JoinDiscordCommand = new DelegateCommand(_ =>
                SetDiscordMsg(OverlayLinks.OpenDiscord()
                    ? "Opening Discord in your browser..."
                    : "No browser available - invite copied to clipboard"));

            OpenWorkshopCommand    = new DelegateCommand(_ => OverlayLinks.OpenWorkshopPage());
            DismissMismatchCommand = new DelegateCommand(_ => NetworkManager.DismissVersionMismatch());
            DismissFatalCommand    = new DelegateCommand(_ => { _fatalDismissed = true; Refresh(true); });
            ResetSettingsCommand   = new DelegateCommand(_ => ResetToDefaults());

            EnableDiagnosticsCommand  = new DelegateCommand(_ =>
            {
                Analytics.AcceptConsent();
                Raise(nameof(ShareDiagnostics)); Raise(nameof(DiagnosticsIdText));
            });
            DeclineDiagnosticsCommand = new DelegateCommand(_ =>
            {
                Analytics.DeclineConsent();
                Raise(nameof(ShareDiagnostics));
            });
        }

        // ── Panel chrome ─────────────────────────────────────────────────────

        // The panel hides independently of the view: popups must still show when
        // the panel is closed, so the camera stays on for them.
        private Visibility _panelVisibility = Visibility.Visible;
        public Visibility PanelVisibility { get => _panelVisibility; set => Set(ref _panelVisibility, value); }

        private bool _panelExpanded = true;
        public bool PanelExpanded
        {
            get => _panelExpanded;
            set { if (Set(ref _panelExpanded, value)) { Raise(nameof(BodyVisibility)); Raise(nameof(ExpandGlyph)); } }
        }
        public Visibility BodyVisibility => Vis(_panelExpanded);
        public string ExpandGlyph => _panelExpanded ? GlyphOpen : GlyphClosed;

        /// <summary>
        /// Foldout arrows, shared by all seven sections so the pair can only ever
        /// be changed together.
        ///
        /// The closed arrow is U+25BA BLACK RIGHT-POINTING POINTER, NOT the
        /// visually identical U+25B6 BLACK RIGHT-POINTING TRIANGLE it replaced.
        /// U+25B6 is an emoji codepoint (it is the base of the ▶️ play button) and
        /// most text fonts skip it - Segoe UI carries ▼ but not ▶ - so it dropped
        /// out of the UI font, fell through to the colour emoji font, and drew as
        /// a blue-backed play button sitting next to six flat triangles. U+25BA is
        /// in the same fonts as ▼ and needs no fallback at all.
        /// </summary>
        private const string GlyphOpen   = "▼";
        private const string GlyphClosed = "►";

        private string _versionText = "";
        public string VersionText { get => _versionText; private set => Set(ref _versionText, value); }

        private Visibility _updateBanner = Visibility.Collapsed;
        public Visibility UpdateBannerVisibility { get => _updateBanner; private set => Set(ref _updateBanner, value); }

        private Visibility _pvpBadge = Visibility.Collapsed;
        public Visibility PvPBadgeVisibility { get => _pvpBadge; private set => Set(ref _pvpBadge, value); }

        private Visibility _syncDot = Visibility.Collapsed;
        public Visibility SyncDotVisibility { get => _syncDot; private set => Set(ref _syncDot, value); }

        private Brush _syncDotBrush = Ok;
        public Brush SyncDotBrush { get => _syncDotBrush; private set => Set(ref _syncDotBrush, value); }

        // ── Fatal init error ─────────────────────────────────────────────────

        private bool _fatalDismissed;

        private Visibility _fatalNotice = Visibility.Collapsed;
        public Visibility FatalNoticeVisibility { get => _fatalNotice; private set => Set(ref _fatalNotice, value); }

        private Visibility _fatalPopup = Visibility.Collapsed;
        public Visibility FatalPopupVisibility { get => _fatalPopup; private set => Set(ref _fatalPopup, value); }

        private string _fatalMessage = "";
        public string FatalMessage { get => _fatalMessage; private set => Set(ref _fatalMessage, value); }

        /// <summary>Body sections hide entirely when init failed - a lobby button
        /// that cannot work is worse than no button.</summary>
        private Visibility _sections = Visibility.Visible;
        public Visibility SectionsVisibility { get => _sections; private set => Set(ref _sections, value); }

        // ── NETWORK ──────────────────────────────────────────────────────────

        private Visibility _steamNet = Visibility.Visible;
        public Visibility SteamVisibility { get => _steamNet; private set => Set(ref _steamNet, value); }

        private Visibility _liteNet = Visibility.Collapsed;
        public Visibility LiteNetVisibility { get => _liteNet; private set => Set(ref _liteNet, value); }

        private string _modeText = "";
        public string ModeText { get => _modeText; private set => Set(ref _modeText, value); }

        private string _statusText = "";
        public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

        private Brush _statusBrush = Critical;
        public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

        private string _detailText = "";
        public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }

        private Visibility _detailVis = Visibility.Collapsed;
        public Visibility DetailVisibility { get => _detailVis; private set => Set(ref _detailVis, value); }

        private string _peerText = "";
        public string PeerText { get => _peerText; private set => Set(ref _peerText, value); }

        private Visibility _peerVis = Visibility.Collapsed;
        public Visibility PeerVisibility { get => _peerVis; private set => Set(ref _peerVis, value); }

        private string _shareCode = "";
        public string ShareCode { get => _shareCode; private set => Set(ref _shareCode, value); }

        // Four mutually exclusive Steam button sets.
        private Visibility _connectedBtns = Visibility.Collapsed;
        public Visibility ConnectedButtonsVisibility { get => _connectedBtns; private set => Set(ref _connectedBtns, value); }

        private Visibility _ownerBtns = Visibility.Collapsed;
        public Visibility LobbyOwnerButtonsVisibility { get => _ownerBtns; private set => Set(ref _ownerBtns, value); }

        private Visibility _guestBtns = Visibility.Collapsed;
        public Visibility LobbyGuestButtonsVisibility { get => _guestBtns; private set => Set(ref _guestBtns, value); }

        private Visibility _noLobbyBtns = Visibility.Visible;
        public Visibility NoLobbyButtonsVisibility { get => _noLobbyBtns; private set => Set(ref _noLobbyBtns, value); }

        private Visibility _sendState = Visibility.Collapsed;
        public Visibility SendStateVisibility { get => _sendState; private set => Set(ref _sendState, value); }

        // Shown in the button's place while the host is still in the menu. Swapped
        // rather than disabled: a greyed-out button invites clicking it to find out
        // why, and the answer is a whole sentence.
        private Visibility _sendStateHint = Visibility.Collapsed;
        public Visibility SendStateHintVisibility { get => _sendStateHint; private set => Set(ref _sendStateHint, value); }

        private string _liteNetPrimary = "Connect";
        public string LiteNetPrimaryText { get => _liteNetPrimary; private set => Set(ref _liteNetPrimary, value); }

        private Visibility _liteNetPrimaryVis = Visibility.Visible;
        public Visibility LiteNetPrimaryVisibility { get => _liteNetPrimaryVis; private set => Set(ref _liteNetPrimaryVis, value); }

        // Transient toast under the lobby buttons.
        private const float LobbyMsgSeconds = 4f;
        private float _lobbyMsgUntil;

        private string _lobbyMsg = "";
        public string LobbyMsg { get => _lobbyMsg; private set => Set(ref _lobbyMsg, value); }

        private Visibility _lobbyMsgVis = Visibility.Collapsed;
        public Visibility LobbyMsgVisibility { get => _lobbyMsgVis; private set => Set(ref _lobbyMsgVis, value); }

        private void SetLobbyMsg(string msg)
        {
            LobbyMsg = msg;
            _lobbyMsgUntil = Time.realtimeSinceStartup + LobbyMsgSeconds;
            LobbyMsgVisibility = Vis(msg.Length > 0);
        }

        // Footer toast, kept separate from the lobby one so each message appears
        // next to the button that produced it.
        private float _discordMsgUntil;

        private string _discordMsg = "";
        public string DiscordMsg { get => _discordMsg; private set => Set(ref _discordMsg, value); }

        private Visibility _discordMsgVis = Visibility.Collapsed;
        public Visibility DiscordMsgVisibility { get => _discordMsgVis; private set => Set(ref _discordMsgVis, value); }

        private void SetDiscordMsg(string msg)
        {
            DiscordMsg = msg;
            _discordMsgUntil = Time.realtimeSinceStartup + LobbyMsgSeconds;
            DiscordMsgVisibility = Vis(msg.Length > 0);
        }

        // ── Sync state / issue ───────────────────────────────────────────────

        private Visibility _syncIssue = Visibility.Collapsed;
        public Visibility SyncIssueVisibility { get => _syncIssue; private set => Set(ref _syncIssue, value); }

        private string _syncIssueText = "";
        public string SyncIssueText { get => _syncIssueText; private set => Set(ref _syncIssueText, value); }

        private Brush _syncIssueBrush = Warn;
        public Brush SyncIssueBrush { get => _syncIssueBrush; private set => Set(ref _syncIssueBrush, value); }

        private string _syncIssueHint = "";
        public string SyncIssueHint { get => _syncIssueHint; private set => Set(ref _syncIssueHint, value); }

        private Visibility _syncIssueHintVis = Visibility.Collapsed;
        public Visibility SyncIssueHintVisibility { get => _syncIssueHintVis; private set => Set(ref _syncIssueHintVis, value); }

        private string _syncStateText = "";
        public string SyncStateText { get => _syncStateText; private set => Set(ref _syncStateText, value); }

        private Brush _syncStateBrush = Warn;
        public Brush SyncStateBrush { get => _syncStateBrush; private set => Set(ref _syncStateBrush, value); }

        private Visibility _syncStateVis = Visibility.Collapsed;
        public Visibility SyncStateVisibility { get => _syncStateVis; private set => Set(ref _syncStateVis, value); }

        private Visibility _receivingVis = Visibility.Collapsed;
        public Visibility ReceivingVisibility { get => _receivingVis; private set => Set(ref _receivingVis, value); }

        // ── TIME CONTROL ─────────────────────────────────────────────────────

        private string _timeText = "";
        public string TimeText { get => _timeText; private set => Set(ref _timeText, value); }

        private string _pauseText = "Pause";
        public string PauseButtonText { get => _pauseText; private set => Set(ref _pauseText, value); }

        private string _timeWait = "";
        public string TimeWaitText { get => _timeWait; private set => Set(ref _timeWait, value); }

        private Visibility _timeWaitVis = Visibility.Collapsed;
        public Visibility TimeWaitVisibility { get => _timeWaitVis; private set => Set(ref _timeWaitVis, value); }

        // ── SETTINGS ─────────────────────────────────────────────────────────

        private bool _settingsExpanded;
        public bool SettingsExpanded
        {
            get => _settingsExpanded;
            set { if (Set(ref _settingsExpanded, value)) { Raise(nameof(SettingsVisibility)); Raise(nameof(SettingsGlyph)); } }
        }
        public Visibility SettingsVisibility => Vis(_settingsExpanded);
        public string SettingsGlyph => _settingsExpanded ? GlyphOpen : GlyphClosed;

        private bool _advancedExpanded;
        public bool AdvancedExpanded
        {
            get => _advancedExpanded;
            set { if (Set(ref _advancedExpanded, value)) { Raise(nameof(AdvancedVisibility)); Raise(nameof(AdvancedGlyph)); } }
        }
        public Visibility AdvancedVisibility => Vis(_advancedExpanded);
        public string AdvancedGlyph => _advancedExpanded ? GlyphOpen : GlyphClosed;

        /// <summary>PvP is baked into the handshake and lobby metadata, so it can
        /// only change while nothing is running.</summary>
        private bool _modeLocked;
        public bool ModeUnlocked => !_modeLocked;
        public Visibility ModeLockedNoticeVisibility => Vis(_modeLocked);

        public bool IsPvP
        {
            get => Plugin.Instance.CfgPvP.Value;
            set
            {
                SetCfg(Plugin.Instance.CfgPvP, value);
                Raise(nameof(IsPvP)); Raise(nameof(IsCoop));
                Raise(nameof(SharedPictureEnabled)); Raise(nameof(PvPIntelNoticeVisibility));
            }
        }

        public bool IsCoop
        {
            get => !Plugin.Instance.CfgPvP.Value;
            set { if (value) IsPvP = false; }
        }

        public bool TimeVote
        {
            get => Plugin.Instance.CfgTimeVote.Value;
            set { SetCfg(Plugin.Instance.CfgTimeVote, value); Raise(nameof(TimeVote)); }
        }

        public bool ContactSync
        {
            get => Plugin.Instance.CfgContactSync.Value;
            set { SetCfg(Plugin.Instance.CfgContactSync, value); Raise(nameof(ContactSync)); }
        }

        public bool DrawingSync
        {
            get => Plugin.Instance.CfgDrawingSync.Value;
            set { SetCfg(Plugin.Instance.CfgDrawingSync, value); Raise(nameof(DrawingSync)); }
        }

        public bool VerboseLogging
        {
            get => Plugin.Instance.CfgVerboseDebug.Value;
            set { SetCfg(Plugin.Instance.CfgVerboseDebug, value); Raise(nameof(VerboseLogging)); }
        }

        /// <summary>Consent, revocable at any time. Takes effect immediately -
        /// SetEnabled starts or stops the capture and the uploader outright.</summary>
        public bool ShareDiagnostics
        {
            get => Plugin.Instance.CfgShareDiagnostics.Value;
            set { Analytics.SetEnabled(value); Raise(nameof(ShareDiagnostics)); Raise(nameof(DiagnosticsIdText)); }
        }

        /// <summary>Shown under the checkbox so a player can quote it in a deletion
        /// request. Empty until diagnostics have been enabled at least once.</summary>
        public string DiagnosticsIdText
        {
            get
            {
                string id = Plugin.Instance.CfgInstallId.Value;
                return string.IsNullOrEmpty(id) ? "" : $"Anonymous ID: {id}";
            }
        }

        /// <summary>Co-op only: in PvP the two pictures are meant to differ.</summary>
        public bool SharedPictureEnabled => !Plugin.Instance.CfgPvP.Value;
        public Visibility PvPIntelNoticeVisibility => Vis(Plugin.Instance.CfgPvP.Value);

        // Numeric settings are edited as text. A value that will not parse is
        // simply not committed, so a half-typed rate never reaches the streamer.
        public string UnitHzText
        {
            get => Plugin.Instance.CfgUnitStateHz.Value.ToString();
            set { if (int.TryParse(value, out int v)) SetCfg(Plugin.Instance.CfgUnitStateHz, Mathf.Clamp(v, 1, 60)); Raise(nameof(UnitHzText)); }
        }

        public string MissileHzText
        {
            get => Plugin.Instance.CfgMissileStateHz.Value.ToString();
            set { if (int.TryParse(value, out int v)) SetCfg(Plugin.Instance.CfgMissileStateHz, Mathf.Clamp(v, 1, 60)); Raise(nameof(MissileHzText)); }
        }

        public string DamageIntervalText
        {
            get => Plugin.Instance.CfgDamageSyncInterval.Value.ToString("0.##");
            set { if (float.TryParse(value, out float v)) SetCfg(Plugin.Instance.CfgDamageSyncInterval, Mathf.Clamp(v, 0.25f, 30f)); Raise(nameof(DamageIntervalText)); }
        }

        /// <summary>BepInEx saves on assignment, so only assign on a real change.</summary>
        private static void SetCfg<T>(ConfigEntry<T> entry, T value)
        {
            if (!Equals(entry.Value, value)) entry.Value = value;
        }

        private void ResetToDefaults()
        {
            var p = Plugin.Instance;
            // CfgTransport and CfgIsHost are driven by the Steam lobby flow, not
            // by the user, so resetting them here would fight it.
            // CfgShareDiagnostics, CfgDiagnosticsAsked and CfgInstallId are
            // deliberately absent. "Reset to defaults" silently revoking - or
            // silently re-granting - a consent decision would be wrong, and
            // re-prompting or churning the anonymous id is worse.
            ConfigEntryBase[] all =
            {
                p.CfgPvP, p.CfgTimeVote, p.CfgVerboseDebug,
                p.CfgDamageSyncInterval, p.CfgMissileStateHz, p.CfgUnitStateHz,
                p.CfgContactSync, p.CfgDrawingSync,
            };
            foreach (var e in all) e.BoxedValue = e.DefaultValue;

            foreach (var n in new[]
                     {
                         nameof(IsPvP), nameof(IsCoop), nameof(TimeVote), nameof(VerboseLogging),
                         nameof(ContactSync), nameof(DrawingSync), nameof(SharedPictureEnabled),
                         nameof(PvPIntelNoticeVisibility),
                         nameof(UnitHzText), nameof(MissileHzText), nameof(DamageIntervalText),
                     })
                Raise(n);

            Plugin.Log.LogInfo("[Settings] Reset to defaults");
        }

        // ── SYNC HEALTH ──────────────────────────────────────────────────────

        private Visibility _syncHealth = Visibility.Collapsed;
        public Visibility SyncHealthVisibility { get => _syncHealth; private set => Set(ref _syncHealth, value); }

        private bool _detailsExpanded;
        public bool DetailsExpanded
        {
            get => _detailsExpanded;
            set { if (Set(ref _detailsExpanded, value)) { Raise(nameof(DetailsVisibility)); Raise(nameof(DetailsGlyph)); } }
        }
        public Visibility DetailsVisibility => Vis(_detailsExpanded);
        public string DetailsGlyph => _detailsExpanded ? GlyphOpen : GlyphClosed;

        private bool _unitsExpanded = true;
        public bool UnitsExpanded
        {
            get => _unitsExpanded;
            set { if (Set(ref _unitsExpanded, value)) { Raise(nameof(UnitsVisibility)); Raise(nameof(UnitsGlyph)); } }
        }
        public Visibility UnitsVisibility => Vis(_unitsExpanded);
        public string UnitsGlyph => _unitsExpanded ? GlyphOpen : GlyphClosed;

        private bool _projectilesExpanded;
        public bool ProjectilesExpanded
        {
            get => _projectilesExpanded;
            set { if (Set(ref _projectilesExpanded, value)) { Raise(nameof(ProjectilesVisibility)); Raise(nameof(ProjectilesGlyph)); } }
        }
        public Visibility ProjectilesVisibility => Vis(_projectilesExpanded);
        public string ProjectilesGlyph => _projectilesExpanded ? GlyphOpen : GlyphClosed;

        private Brush _detailsDot = Ok;
        public Brush DetailsDotBrush { get => _detailsDot; private set => Set(ref _detailsDot, value); }

        private Brush _unitsDot = Ok;
        public Brush UnitsDotBrush { get => _unitsDot; private set => Set(ref _unitsDot, value); }

        private string _rttText = "";
        public string RttText { get => _rttText; private set => Set(ref _rttText, value); }

        private string _unitCountsText = "";
        public string UnitCountsText { get => _unitCountsText; private set => Set(ref _unitCountsText, value); }

        private string _shipDriftText = "";
        public string ShipDriftText { get => _shipDriftText; private set => Set(ref _shipDriftText, value); }

        private Brush _shipDriftBrush = Dim;
        public Brush ShipDriftBrush { get => _shipDriftBrush; private set => Set(ref _shipDriftBrush, value); }

        private string _airDriftText = "";
        public string AirDriftText { get => _airDriftText; private set => Set(ref _airDriftText, value); }

        private Brush _airDriftBrush = Dim;
        public Brush AirDriftBrush { get => _airDriftBrush; private set => Set(ref _airDriftBrush, value); }

        private string _predictErrText = "";
        public string PredictErrText { get => _predictErrText; private set => Set(ref _predictErrText, value); }

        private string _projectilesText = "";
        public string ProjectilesText { get => _projectilesText; private set => Set(ref _projectilesText, value); }

        // ── NET v2 ───────────────────────────────────────────────────────────

        private string _protocolText = "";
        public string ProtocolText { get => _protocolText; private set => Set(ref _protocolText, value); }

        private Visibility _net2Detail = Visibility.Collapsed;
        public Visibility Net2DetailVisibility { get => _net2Detail; private set => Set(ref _net2Detail, value); }

        private string _ratesText = "";
        public string RatesText { get => _ratesText; private set => Set(ref _ratesText, value); }

        private string _lossText = "";
        public string LossText { get => _lossText; private set => Set(ref _lossText, value); }

        private string _sendFrameText = "";
        public string SendFrameText { get => _sendFrameText; private set => Set(ref _sendFrameText, value); }

        private string _replicasText = "";
        public string ReplicasText { get => _replicasText; private set => Set(ref _replicasText, value); }

        private bool _countersExpanded;
        public bool CountersExpanded
        {
            get => _countersExpanded;
            set { if (Set(ref _countersExpanded, value)) { Raise(nameof(CountersVisibility)); Raise(nameof(CountersGlyph)); } }
        }
        public Visibility CountersVisibility => Vis(_countersExpanded);
        public string CountersGlyph => _countersExpanded ? GlyphOpen : GlyphClosed;

        public ObservableCollection<string> Counters { get; } = new ObservableCollection<string>();

        // ── Popups ───────────────────────────────────────────────────────────

        private Visibility _timeVote = Visibility.Collapsed;
        public Visibility TimeVoteVisibility { get => _timeVote; private set => Set(ref _timeVote, value); }

        private string _timeVoteText = "";
        public string TimeVoteText { get => _timeVoteText; private set => Set(ref _timeVoteText, value); }

        private Visibility _connLost = Visibility.Collapsed;
        public Visibility ConnLostVisibility { get => _connLost; private set => Set(ref _connLost, value); }

        private string _connLostStatus = "";
        public string ConnLostStatus { get => _connLostStatus; private set => Set(ref _connLostStatus, value); }

        private Visibility _connLostButtons = Visibility.Collapsed;
        public Visibility ConnLostButtonsVisibility { get => _connLostButtons; private set => Set(ref _connLostButtons, value); }

        private Visibility _reconnectNow = Visibility.Collapsed;
        public Visibility ReconnectNowVisibility { get => _reconnectNow; private set => Set(ref _reconnectNow, value); }

        private Visibility _reinvite = Visibility.Collapsed;
        public Visibility ReinviteVisibility { get => _reinvite; private set => Set(ref _reinvite, value); }

        private string _abandonText = "Leave Session";
        public string AbandonText { get => _abandonText; private set => Set(ref _abandonText, value); }

        private Visibility _mismatch = Visibility.Collapsed;
        public Visibility MismatchVisibility { get => _mismatch; private set => Set(ref _mismatch, value); }

        private string _mismatchNotice = "";
        public string MismatchNotice { get => _mismatchNotice; private set => Set(ref _mismatchNotice, value); }

        public Visibility WorkshopButtonVisibility => Vis(OverlayLinks.WorkshopUrl != null);

        private Visibility _allyLock = Visibility.Collapsed;
        public Visibility AllyLockVisibility { get => _allyLock; private set => Set(ref _allyLock, value); }

        private string _allyLockText = "";
        public string AllyLockText { get => _allyLockText; private set => Set(ref _allyLockText, value); }

        private Visibility _consent = Visibility.Collapsed;
        public Visibility ConsentVisibility { get => _consent; private set => Set(ref _consent, value); }

        /// <summary>True when something must be on screen even with the panel
        /// closed - the overlay camera stays enabled for these.</summary>
        public bool AnyPopupActive =>
            _timeVote  == Visibility.Visible || _connLost  == Visibility.Visible ||
            _mismatch  == Visibility.Visible || _allyLock  == Visibility.Visible ||
            _fatalPopup == Visibility.Visible || _consent == Visibility.Visible;

        // ── Refresh ──────────────────────────────────────────────────────────

        private float _net2SampleAt;
        private long _prevBytesIn, _prevBytesOut;
        private float _rateInBps, _rateOutBps;

        /// <summary>
        /// Pulls every bound value. Called on a timer rather than per frame:
        /// these are all cheap reads, but the string formatting is not free and
        /// Noesis only needs to see a change when there is one.
        /// </summary>
        public void Refresh(bool panelVisible)
        {
            var nm = NetworkManager.Instance;
            var p  = Plugin.Instance;
            bool connected = nm.IsConnected;

            RefreshPopups(nm);

            VersionText = $"SeaPower MP  v{PluginInfo.PLUGIN_VERSION}";
            PvPBadgeVisibility = Vis(p.CfgPvP.Value);

            // Set before the fatal early-return: an outdated build is a plausible
            // reason for the failure, so the prompt is worth showing either way.
            UpdateBannerVisibility = Vis(WorkshopVersionCheck.UpdateAvailable);

            bool fatal = Plugin.FatalInitError != null;
            FatalMessage = Plugin.FatalInitError ?? "";
            FatalNoticeVisibility = Vis(fatal);
            SectionsVisibility = Vis(!fatal);

            if (!panelVisible || fatal) return;

            var overall = ComputeOverallStatus();
            SyncDotVisibility = Vis(connected);
            SyncDotBrush = StatusBrushFor(overall);

            RefreshNetwork(nm, p, connected);
            RefreshSyncState(nm, connected);
            RefreshTime(p);
            RefreshSettingsLock(nm);
            if (_settingsExpanded) RefreshSettingsMirror();
            RefreshNet2(nm, connected);

            SyncHealthVisibility = Vis(connected);
            if (connected && _detailsExpanded) RefreshSyncHealth(nm, p, overall);
        }

        private void RefreshPopups(NetworkManager nm)
        {
            // One-time diagnostics consent. Deliberately evaluated here rather
            // than behind the panel-visible check: it has to appear in the main
            // menu with the panel closed, which is what AnyPopupActive drives.
            ConsentVisibility = Vis(Analytics.ShouldPromptConsent);

            // Time vote
            bool vote = TimeSyncManager.HasPendingProposal;
            TimeVoteVisibility = Vis(vote);
            if (vote)
            {
                string who = Plugin.Instance.CfgIsHost.Value ? "Client" : "Host";
                TimeVoteText = $"{who} proposes:  {TimeSyncManager.ProposalDescription}";
            }

            // Connection lost
            bool frozen = ReconnectManager.IsFrozen;
            ConnLostVisibility = Vis(frozen);
            if (frozen)
            {
                bool isHost = nm.IsHost;
                ConnLostStatus = ReconnectManager.State switch
                {
                    LinkState.Reconnecting => $"Reconnecting...  (attempt {ReconnectManager.Attempts})",
                    LinkState.Resyncing    => "Reconnected - syncing the session...",
                    _ => isHost
                        ? "Waiting for the other player to reconnect..."
                        : $"Retrying in {ReconnectManager.RetryCountdown:F0}s",
                };
                // No controls during the resync: pressing anything would only
                // interrupt the transfer that is already fixing the problem.
                bool resyncing = ReconnectManager.State == LinkState.Resyncing;
                ConnLostButtonsVisibility = Vis(!resyncing);
                ReconnectNowVisibility = Vis(!resyncing && !isHost);
                ReinviteVisibility = Vis(!resyncing && isHost && SteamLobbyManager.InLobby);
                AbandonText = isHost ? "Continue Solo" : "Leave Session";
            }

            // Version mismatch
            string? notice = NetworkManager.VersionMismatchNotice;
            MismatchVisibility = Vis(notice != null);
            MismatchNotice = notice ?? "";

            // Fatal popup
            FatalPopupVisibility = Vis(Plugin.FatalInitError != null && !_fatalDismissed);

            // Ally lock banner - auto-expires, never dismissed.
            bool ally = Time.unscaledTime < UnitLockManager.RefusalNoticeUntil;
            AllyLockVisibility = Vis(ally);
            if (ally)
            {
                string name = UnitLockManager.LastRefusedUnitName;
                AllyLockText = name.Length > 0
                    ? $"{name} is being commanded by your ally"
                    : "That unit is being commanded by your ally";
            }

            // Toast expiry
            if (_lobbyMsg.Length > 0 && Time.realtimeSinceStartup >= _lobbyMsgUntil)
            {
                LobbyMsg = "";
                LobbyMsgVisibility = Visibility.Collapsed;
            }
            if (_discordMsg.Length > 0 && Time.realtimeSinceStartup >= _discordMsgUntil)
            {
                DiscordMsg = "";
                DiscordMsgVisibility = Visibility.Collapsed;
            }
        }

        private void RefreshNetwork(NetworkManager nm, Plugin p, bool connected)
        {
            // Only the host needs a mission of its own - the client gets one from
            // the host's save, so waiting in the menu is exactly what it should do.
            bool canSend = SessionManager.MissionIsLive;
            bool isSteam = p.CfgTransport.Value == "Steam";
            SteamVisibility = Vis(isSteam);
            LiteNetVisibility = Vis(!isSteam);

            if (isSteam)
            {
                bool inLobby = SteamLobbyManager.InLobby;
                bool isOwner = SteamLobbyManager.IsLobbyOwner;

                if (connected)
                {
                    ModeText = nm.IsHost ? "STEAM (HOST)" : "STEAM (CLIENT)";
                    StatusText = "Connected"; StatusBrush = Ok;
                    DetailText = $"Ping: {nm.LastRttMs} ms";
                }
                else if (inLobby)
                {
                    ModeText = isOwner ? "STEAM (HOST)" : "STEAM (CLIENT)";
                    StatusText = isOwner ? "In Lobby" : "Connecting"; StatusBrush = Warn;
                    DetailText = $"Lobby: {SteamLobbyManager.MemberCount}/2 players";
                }
                else
                {
                    ModeText = "STEAM";
                    StatusText = "Not in lobby"; StatusBrush = Critical;
                    DetailText = "";
                }
                DetailVisibility = Vis(DetailText.Length > 0);

                string peer = SteamLobbyManager.PeerName;
                PeerText = peer.Length > 0 ? $"Player: {peer}" : "";
                PeerVisibility = Vis(peer.Length > 0);

                ShareCode = SteamLobbyManager.ShareCode;

                ConnectedButtonsVisibility  = Vis(connected);
                LobbyOwnerButtonsVisibility = Vis(!connected && inLobby && isOwner);
                LobbyGuestButtonsVisibility = Vis(!connected && inLobby && !isOwner);
                NoLobbyButtonsVisibility    = Vis(!connected && !inLobby);
                SendStateVisibility         = Vis(connected && nm.IsHost && canSend);
                SendStateHintVisibility     = Vis(connected && nm.IsHost && !canSend);
            }
            else
            {
                bool isHost = p.CfgIsHost.Value;
                ModeText = isHost ? "HOST" : "CLIENT";

                if (connected)            { StatusText = "Connected";    StatusBrush = Ok; }
                else if (nm.IsHostRunning){ StatusText = "Listening";    StatusBrush = Warn; }
                else                      { StatusText = "Disconnected"; StatusBrush = Critical; }

                DetailText = connected
                    ? $"Ping: {nm.LastRttMs} ms"
                    : isHost
                        ? $"Port: {p.CfgPort.Value}"
                        : $"Host: {p.CfgHostIP.Value}:{p.CfgPort.Value}";
                DetailVisibility = Visibility.Visible;

                LiteNetPrimaryText = isHost
                    ? (nm.IsHostRunning ? "Stop Hosting" : "Start Hosting")
                    : "Connect";
                LiteNetPrimaryVisibility = Vis(!connected);
                ConnectedButtonsVisibility = Vis(connected);
                SendStateVisibility = Vis(connected && isHost && canSend);
                SendStateHintVisibility = Vis(connected && isHost && !canSend);
            }
        }

        private void RefreshSyncState(NetworkManager nm, bool connected)
        {
            bool issue = SimSyncManager.HasIssue;
            SyncIssueVisibility = Vis(issue);
            if (issue)
            {
                SyncIssueText  = SimSyncManager.IssueMessage;
                SyncIssueBrush = SimSyncManager.IssueIsWarning ? Warn : Critical;
                SyncIssueHint  = SimSyncManager.IssueHint ?? "";
                SyncIssueHintVisibility = Vis(SyncIssueHint.Length > 0);
            }

            bool isHost = nm.IsHost;
            var state = SimSyncManager.CurrentState;
            string text = "";
            Brush brush = Warn;

            if (state == SimState.WaitingForClient) { text = "Waiting for client to load..."; }
            else if (state == SimState.Synchronized)
            {
                text = GameTime.IsPaused() ? "Client ready - unpause to begin" : "Sim synced";
                brush = Ok;
            }
            else if (!issue && connected)
            {
                // Connected but nothing synced yet - saying nothing here used to
                // read as a healthy session. The host gets told to load a mission
                // rather than to press a button that is not on screen yet.
                text = !isHost                     ? "Not synced - waiting for host"
                     : SessionManager.MissionIsLive ? "Not synced - press Send State & Wait"
                                                    : "Not synced - start a mission first";
            }

            SyncStateText = text;
            SyncStateBrush = brush;
            SyncStateVisibility = Vis(text.Length > 0);
            ReceivingVisibility = Vis(!isHost && SessionManager.IsReceiving);
        }

        private void RefreshTime(Plugin p)
        {
            bool paused = GameTime.IsPaused();
            TimeText = paused ? "Time: PAUSED" : $"Time: {GameTime.TimeCompression:0.#}x";
            PauseButtonText = paused ? "Play" : "Pause";

            if (TimeSyncManager.PendingRequest && !p.CfgIsHost.Value)
            { TimeWaitText = "Waiting for host..."; TimeWaitVisibility = Visibility.Visible; }
            else if (TimeSyncManager.WaitingForVoteResponse)
            { TimeWaitText = "Waiting for other player..."; TimeWaitVisibility = Visibility.Visible; }
            else TimeWaitVisibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Re-asserts the config-backed toggles while the section is open. These
        /// are read straight off the ConfigEntry objects, so nothing would
        /// otherwise notify the UI: a binding that missed its first transfer, or
        /// a value changed by an env override or Reset, would display stale for
        /// the rest of the session. The text fields are deliberately excluded -
        /// re-raising those would overwrite whatever is being typed.
        /// </summary>
        private void RefreshSettingsMirror()
        {
            Raise(nameof(IsPvP));
            Raise(nameof(IsCoop));
            Raise(nameof(TimeVote));
            Raise(nameof(ContactSync));
            Raise(nameof(DrawingSync));
            Raise(nameof(VerboseLogging));
            Raise(nameof(ShareDiagnostics));
            Raise(nameof(DiagnosticsIdText));
            Raise(nameof(SharedPictureEnabled));
            Raise(nameof(PvPIntelNoticeVisibility));
        }

        private void RefreshSettingsLock(NetworkManager nm)
        {
            bool locked = nm.IsConnected || nm.IsHostRunning || SteamLobbyManager.InLobby;
            if (locked == _modeLocked) return;
            _modeLocked = locked;
            Raise(nameof(ModeUnlocked));
            Raise(nameof(ModeLockedNoticeVisibility));
        }

        private void RefreshNet2(NetworkManager nm, bool connected)
        {
            ProtocolText = $"Protocol {Net2.ProtocolInfo.ProtocolVersion}  -  Handshake: {nm.Handshake}";
            Net2DetailVisibility = Vis(connected);
            if (!connected) return;

            if (Time.unscaledTime >= _net2SampleAt)
            {
                float dt = _net2SampleAt > 0f ? Mathf.Max(0.25f, Time.unscaledTime - (_net2SampleAt - 0.5f)) : 0.5f;
                _rateInBps  = (Telemetry.TotalBytesIn  - _prevBytesIn)  / dt;
                _rateOutBps = (Telemetry.TotalBytesOut - _prevBytesOut) / dt;
                _prevBytesIn  = Telemetry.TotalBytesIn;
                _prevBytesOut = Telemetry.TotalBytesOut;
                _net2SampleAt = Time.unscaledTime + 0.5f;
            }

            RatesText = $"In {FormatRate(_rateInBps)}  -  Out {FormatRate(_rateOutBps)}  -  " +
                        $"Total {Telemetry.TotalBytesIn / 1024}K / {Telemetry.TotalBytesOut / 1024}K";
            LossText = $"Packet loss (10s): {(nm.PacketLossPct < 0f ? "n/a" : $"{nm.PacketLossPct:F1}%")}";

            var (sMin, sAvg, sMax) = Telemetry.FrameSendStats();
            SendFrameText = $"Send/frame: min {sMin}B  avg {sAvg:F0}B  max {sMax}B";

            ReplicasText = $"Replicas: {ReplicaRegistry.Count}  -  weapons {WeaponReplicaDriver.ActiveReplicas}" +
                           $"  -  air targets {AircraftReplicaDriver.ActiveTargets}  -  ledger {CaptureState.SpawnLedger.Count}";

            if (!_countersExpanded) return;
            Counters.Clear();
            if (Telemetry.Counters.Count == 0) Counters.Add("(no events)");
            else foreach (var kv in Telemetry.Counters) Counters.Add($"{kv.Key}: {kv.Value}");
        }

        private static string FormatRate(float bps)
            => bps >= 1024f * 1024f ? $"{bps / (1024f * 1024f):F2} MB/s"
             : bps >= 1024f         ? $"{bps / 1024f:F1} KB/s"
             : $"{bps:F0} B/s";

        // ── Sync health ──────────────────────────────────────────────────────

        private enum SyncStatus { Ok, Degraded, Issues }

        private static SyncStatus ComputeOverallStatus()
        {
            // A reported sync issue outranks drift - it means the two sides may
            // not even be in the same mission, which no drift metric reveals.
            if (SimSyncManager.HasIssue)
                return SimSyncManager.IssueIsWarning ? SyncStatus.Degraded : SyncStatus.Issues;
            return UnitDriftStatus();
        }

        private static SyncStatus UnitDriftStatus()
        {
            if (StateApplier.ShipDriftMax > 100f || StateApplier.AirDriftMax > 200f) return SyncStatus.Issues;
            if (StateApplier.ShipDriftAvg > 20f  || StateApplier.AirDriftAvg > 40f)  return SyncStatus.Degraded;
            return SyncStatus.Ok;
        }

        private static Brush StatusBrushFor(SyncStatus s) => s switch
        {
            SyncStatus.Issues   => Critical,
            SyncStatus.Degraded => Warn,
            _                   => Ok,
        };

        private void RefreshSyncHealth(NetworkManager nm, Plugin p, SyncStatus overall)
        {
            DetailsDotBrush = StatusBrushFor(overall);
            UnitsDotBrush = StatusBrushFor(UnitDriftStatus());
            RttText = $"RTT: {nm.LastRttMs} ms";

            if (!_unitsExpanded && !_projectilesExpanded) return;

            UnitCensus.Refresh(p.CfgPvP.Value);

            if (_unitsExpanded)
            {
                UnitCountsText = UnitCensus.DescribeUnits(p.CfgPvP.Value);

                // Shown in metres: the raw figures are horizontal Unity units
                // (~67 m each), so one decimal hid everything under 3 m as "0.0".
                // Thresholds still compare the raw values. The n= count matters
                // as much as the figures: n=0 means nothing was measured, which
                // otherwise displays as a healthy-looking 0.0.
                const float m = Net2.GeoCodec.MetresPerUnityUnit;

                bool shipsUnmeasured = StateApplier.ShipDriftCount == 0 && UnitCensus.SurfaceAndSubTotal > 0;
                ShipDriftBrush = shipsUnmeasured || StateApplier.ShipDriftMax > 100f ? Warn
                    : StateApplier.ShipDriftAvg > 20f ? Warn : Dim;
                ShipDriftText = $"Ship drift: {StateApplier.ShipDriftAvg * m:F0} m avg / " +
                                $"{StateApplier.ShipDriftMax * m:F0} m max  (n={StateApplier.ShipDriftCount})";

                bool airUnmeasured = StateApplier.AirDriftCount == 0 && UnitCensus.AirTotal > 0;
                AirDriftBrush = airUnmeasured || StateApplier.AirDriftMax > 200f ? Warn
                    : StateApplier.AirDriftAvg > 40f ? Warn : Dim;
                // Left unscaled: the air figure sums metres (y) and Unity units
                // (xz), so it is not a length and converting it would only look
                // authoritative.
                AirDriftText = $"Air drift: {StateApplier.AirDriftAvg:F1} avg / {StateApplier.AirDriftMax:F1} max" +
                               $"  (n={StateApplier.AirDriftCount}, mixed units)";

                // Prediction error - how wrong the motion model was when a fresh
                // host sample landed. This is the figure that responds to stream
                // rate; drift does not.
                PredictErrText = $"Predict err: {StateApplier.ShipPredictErrAvg * m:F0} m avg / " +
                                 $"{StateApplier.ShipPredictErrMax * m:F0} m max (n={StateApplier.ShipPredictErrCount} ships), " +
                                 $"{StateApplier.AirPredictErrAvg * m:F0} m avg / " +
                                 $"{StateApplier.AirPredictErrMax * m:F0} m max (n={StateApplier.AirPredictErrCount} air)";
            }

            if (_projectilesExpanded)
                ProjectilesText = UnitCensus.DescribeProjectiles(p.CfgPvP.Value);
        }
    }
}
