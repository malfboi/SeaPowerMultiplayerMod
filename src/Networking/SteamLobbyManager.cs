using System;
using BepInEx.Logging;
using Steamworks;

namespace SeapowerMultiplayer.Transport
{
    /// <summary>
    /// Manages Steam lobby lifecycle: create, join, invite, leave.
    /// Uses Callback&lt;T&gt;.Create() — callbacks fire automatically because the game's
    /// SteamManager already pumps SteamAPI.RunCallbacks() every frame.
    /// </summary>
    public static class SteamLobbyManager
    {
        private static ManualLogSource Log => Plugin.Log;

        // ── State ─────────────────────────────────────────────────────────────
        public static CSteamID LobbyId { get; private set; }
        public static CSteamID HostSteamId { get; private set; }
        public static bool InLobby => LobbyId != CSteamID.Nil;
        public static int MemberCount => InLobby ? SteamMatchmaking.GetNumLobbyMembers(LobbyId) : 0;

        // Pending join from launch arg — deferred until callbacks are registered
        private static ulong _pendingLobbyJoin;

        // ── Callbacks ─────────────────────────────────────────────────────────
        private static Callback<LobbyCreated_t>? _lobbyCreatedCb;
        private static Callback<LobbyEnter_t>? _lobbyEnteredCb;
        private static Callback<GameLobbyJoinRequested_t>? _lobbyJoinRequestedCb;
        private static Callback<LobbyChatUpdate_t>? _lobbyChatUpdateCb;

        /// <summary>
        /// Register Steam callbacks. Call once during plugin init.
        /// Safe to call even if transport is LiteNetLib — callbacks just won't fire.
        /// </summary>
        public static void Init()
        {
            _lobbyCreatedCb = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyEnteredCb = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            _lobbyJoinRequestedCb = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
            _lobbyChatUpdateCb = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);

            // If we have a pending join from launch args, do it now
            if (_pendingLobbyJoin != 0)
            {
                SteamMatchmaking.JoinLobby(new CSteamID(_pendingLobbyJoin));
                _pendingLobbyJoin = 0;
            }
        }

        /// <summary>
        /// Called from Plugin.Awake() when +connect_lobby arg is found.
        /// Defers the actual join until Init() registers callbacks.
        /// </summary>
        public static void JoinLobbyFromLaunchArg(ulong lobbyId)
        {
            Log.LogInfo($"[SteamLobby] Deferred join for lobby {lobbyId}");
            _pendingLobbyJoin = lobbyId;
        }

        // ── Public API ────────────────────────────────────────────────────────

        public static void CreateLobby()
        {
            if (InLobby)
            {
                Log.LogWarning("[SteamLobby] Already in a lobby");
                return;
            }

            Log.LogInfo("[SteamLobby] Creating lobby...");
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2);
        }

        public static void InviteFriend()
        {
            if (!InLobby) return;
            SteamFriends.ActivateGameOverlayInviteDialog(LobbyId);
        }

        public static void LeaveLobby()
        {
            if (!InLobby) return;

            Log.LogInfo("[SteamLobby] Leaving lobby");
            SteamMatchmaking.LeaveLobby(LobbyId);
            LobbyId = CSteamID.Nil;
            HostSteamId = CSteamID.Nil;
            NetworkManager.Instance.Stop();
        }

        public static void JoinLobby(CSteamID lobbyId)
        {
            if (InLobby)
            {
                Log.LogWarning("[SteamLobby] Already in a lobby, leaving first");
                LeaveLobby();
            }

            Log.LogInfo($"[SteamLobby] Joining lobby {lobbyId}...");
            SteamMatchmaking.JoinLobby(lobbyId);
        }

        // ── Callbacks ─────────────────────────────────────────────────────────

        private static void OnLobbyCreated(LobbyCreated_t result)
        {
            if (result.m_eResult != EResult.k_EResultOK)
            {
                Log.LogError($"[SteamLobby] Failed to create lobby: {result.m_eResult}");
                return;
            }

            LobbyId = new CSteamID(result.m_ulSteamIDLobby);
            Log.LogInfo($"[SteamLobby] Lobby created: {LobbyId}");

            // Set lobby metadata
            HostSteamId = SteamUser.GetSteamID();
            SteamMatchmaking.SetLobbyData(LobbyId, "host_steamid", HostSteamId.ToString());
            SteamMatchmaking.SetLobbyData(LobbyId, "mod_version", PluginInfo.PLUGIN_VERSION);
            SteamMatchmaking.SetLobbyData(LobbyId, "pvp", Plugin.Instance.CfgPvP.Value.ToString());

            // Start transport as host
            NetworkManager.Instance.StartTransport(asHost: true);
        }

        private static void OnLobbyEntered(LobbyEnter_t result)
        {
            var lobbyId = new CSteamID(result.m_ulSteamIDLobby);
            var response = (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse;

            if (response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Log.LogError($"[SteamLobby] Failed to join lobby: {response}");
                return;
            }

            LobbyId = lobbyId;
            Log.LogInfo($"[SteamLobby] Joined lobby: {LobbyId}");

            // If we're not the host (someone else created the lobby), connect as client
            string hostIdStr = SteamMatchmaking.GetLobbyData(LobbyId, "host_steamid");
            if (string.IsNullOrEmpty(hostIdStr))
            {
                Log.LogError("[SteamLobby] Lobby has no host_steamid data");
                return;
            }

            var hostSteamId = new CSteamID(ulong.Parse(hostIdStr));
            var mySteamId = SteamUser.GetSteamID();

            if (hostSteamId == mySteamId)
            {
                // We're the host — already started transport in OnLobbyCreated
                Log.LogInfo("[SteamLobby] We are the host, transport already running");
                return;
            }

            // We're joining as client — store host ID for SteamTransport to read
            Log.LogInfo($"[SteamLobby] Connecting to host {hostSteamId}");
            HostSteamId = hostSteamId;
            Plugin.Instance.CfgIsHost.Value = false;
            NetworkManager.Instance.StartTransport(asHost: false);
        }

        private static void OnLobbyJoinRequested(GameLobbyJoinRequested_t request)
        {
            Log.LogInfo($"[SteamLobby] Invite accepted, joining lobby {request.m_steamIDLobby}");
            JoinLobby(request.m_steamIDLobby);
        }

        private static void OnLobbyChatUpdate(LobbyChatUpdate_t update)
        {
            var who = new CSteamID(update.m_ulSteamIDUserChanged);
            var change = (EChatMemberStateChange)update.m_rgfChatMemberStateChange;
            string name = SteamFriends.GetFriendPersonaName(who);

            if ((change & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
                Log.LogInfo($"[SteamLobby] {name} joined the lobby");
            if ((change & EChatMemberStateChange.k_EChatMemberStateChangeLeft) != 0)
                Log.LogInfo($"[SteamLobby] {name} left the lobby");
            if ((change & EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) != 0)
                Log.LogInfo($"[SteamLobby] {name} disconnected from lobby");
        }
    }
}
