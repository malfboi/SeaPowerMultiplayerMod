using System;
using BepInEx.Logging;
using Steamworks;

namespace SeapowerMultiplayer.Transport
{
    /// <summary>
    /// Manages Steam lobby lifecycle: create, join, invite, leave.
    /// Uses Callback&lt;T&gt;.Create() - callbacks fire automatically because the game's
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

        /// <summary>Shareable code for the current lobby, empty when not in one.</summary>
        public static string ShareCode { get; private set; } = "";

        /// <summary>
        /// True when we own this lobby (so we are the one handing out the code).
        /// Read from Steam rather than our own host_steamid metadata so it is
        /// correct regardless of the order the create/enter callbacks arrive in.
        /// </summary>
        public static bool IsLobbyOwner => InLobby
                                        && SteamMatchmaking.GetLobbyOwner(LobbyId) == SteamUser.GetSteamID();

        /// <summary>
        /// Steam persona name of the FIRST other lobby member, empty while we are alone.
        /// Kept for the pre-session status line, which has room for one name; the roster
        /// is what shows everybody once the session is up.
        /// Cached rather than queried per-frame - the UI reads this every OnGUI pass.
        /// </summary>
        public static string PeerName { get; private set; } = "";

        private static CSteamID _peerSteamId;

        /// <summary>Persona name per lobby member, excluding ourselves. Empty string
        /// means Steam is still fetching it.</summary>
        private static readonly System.Collections.Generic.Dictionary<CSteamID, string> _names = new();

        /// <summary>
        /// Team this machine should ask for when it joins, read from the lobby's
        /// "pending_team" key on entry.
        ///
        /// The Steam overlay's invite dialog carries no payload and never tells us who was
        /// invited, so "Invite to Red" works by the host publishing its intent to the
        /// lobby and the next joiner picking it up. It is only ever a REQUEST - the host
        /// seats people, and two players accepting the same invite both read the same key.
        /// Null when we have no intent to report (direct connect, or a plain lobby join).
        /// </summary>
        public static Team? PendingTeamForJoin { get; internal set; }

        // Pending join from launch arg - deferred until callbacks are registered
        private static ulong _pendingLobbyJoin;

        // ── Callbacks ─────────────────────────────────────────────────────────
        private static Callback<LobbyCreated_t>? _lobbyCreatedCb;
        private static Callback<LobbyEnter_t>? _lobbyEnteredCb;
        private static Callback<GameLobbyJoinRequested_t>? _lobbyJoinRequestedCb;
        private static Callback<LobbyChatUpdate_t>? _lobbyChatUpdateCb;
        private static Callback<PersonaStateChange_t>? _personaStateCb;

        /// <summary>
        /// Register Steam callbacks. Call once during plugin init.
        /// Safe to call even if transport is LiteNetLib - callbacks just won't fire.
        /// </summary>
        public static void Init()
        {
            _lobbyCreatedCb = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyEnteredCb = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            _lobbyJoinRequestedCb = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
            _lobbyChatUpdateCb = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            _personaStateCb = Callback<PersonaStateChange_t>.Create(OnPersonaStateChange);

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
            // Public, not FriendsOnly: a share code has to work for someone who
            // is not on the host's friends list, and Steam refuses JoinLobby by
            // ID for friends-only lobbies. It stops being joinable once full, so
            // listing it costs nothing.
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, PlayerRegistry.MaxPlayers);
        }

        public static void InviteFriend()
        {
            if (!InLobby) return;
            SteamFriends.ActivateGameOverlayInviteDialog(LobbyId);
        }

        /// <summary>
        /// Invite someone to a specific team.
        ///
        /// Steam's invite dialog carries no payload and does not report who was invited,
        /// so the team travels out-of-band: the host publishes its intent on the lobby
        /// and the next joiner reads it in <see cref="OnLobbyEntered"/> and carries it
        /// into its Hello. That makes it a REQUEST rather than a promise - if two people
        /// accept the same invite they both ask for the same side, and the host simply
        /// seats the second one elsewhere and shows it in the roster, where it can be
        /// corrected. Losing a joiner to a "that team is full" refusal would be a far
        /// worse trade than a wrong seat the host can fix in one click.
        /// </summary>
        public static void InviteToTeam(Team team)
        {
            if (!InLobby || !IsLobbyOwner) return;
            SteamMatchmaking.SetLobbyData(LobbyId, "pending_team", team.ToString());
            Log.LogInfo($"[SteamLobby] Inviting to {team}.");
            SteamFriends.ActivateGameOverlayInviteDialog(LobbyId);
        }

        /// <summary>
        /// Joins the lobby named by a share code. Returns false with a reason
        /// suitable for display when the code is not a lobby code.
        /// </summary>
        public static bool TryJoinByCode(string? code, out string error)
        {
            if (!LobbyCode.TryDecode(code, out ulong raw))
            {
                error = "Not a valid lobby code";
                return false;
            }

            // Lobby IDs are chat-type SteamIDs; anything else decoded cleanly but
            // is not a lobby, and Steam would just time out on it.
            const ulong AccountTypeChat = 8;
            if (((raw >> 52) & 0xF) != AccountTypeChat)
            {
                error = "Not a valid lobby code";
                return false;
            }

            error = "";
            JoinLobby(new CSteamID(raw));
            return true;
        }

        public static void LeaveLobby()
        {
            if (!InLobby) return;

            Log.LogInfo("[SteamLobby] Leaving lobby");
            SteamMatchmaking.LeaveLobby(LobbyId);
            LobbyId = CSteamID.Nil;
            HostSteamId = CSteamID.Nil;
            ShareCode = "";
            _peerSteamId = CSteamID.Nil;
            PeerName = "";
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
            ShareCode = LobbyCode.Encode(LobbyId.m_SteamID);
            Log.LogInfo($"[SteamLobby] Lobby created: {LobbyId} (code {ShareCode})");

            // Creating the lobby is what makes us the host. There is no Role
            // setting any more, so this is the only thing that sets CfgIsHost -
            // without it a player who previously joined someone else's lobby would
            // keep running the client-side logic while hosting the transport.
            Plugin.Instance.CfgIsHost.Value = true;

            // Set lobby metadata
            HostSteamId = SteamUser.GetSteamID();
            Plugin.Instance.CfgIsHost.Value = true;
            SteamMatchmaking.SetLobbyData(LobbyId, "host_steamid", HostSteamId.ToString());
            SteamMatchmaking.SetLobbyData(LobbyId, "mod_version", PluginInfo.PLUGIN_VERSION);
            // No mode key any more: there is no session-wide mode to agree on. What a
            // joiner reads instead is "pending_team", the host's most recent invite
            // intent - see PendingTeamForJoin.

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
            ShareCode = LobbyCode.Encode(LobbyId.m_SteamID);
            Log.LogInfo($"[SteamLobby] Joined lobby: {LobbyId}");
            RefreshPeerName();

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
                // We're the host - already started transport in OnLobbyCreated
                Log.LogInfo("[SteamLobby] We are the host, transport already running");
                return;
            }

            // We're joining as client - store host ID for SteamTransport to read
            Log.LogInfo($"[SteamLobby] Connecting to host {hostSteamId}");
            HostSteamId = hostSteamId;
            Plugin.Instance.CfgIsHost.Value = false;

            // Pick up the host's invite intent, if there is one. Only a REQUEST carried
            // into our Hello - the host does the seating, so two people accepting the
            // same "Invite to Red" is a non-event rather than a race to lose.
            // Nothing is written to config: team is per-session, not a saved preference.
            string teamStr = SteamMatchmaking.GetLobbyData(LobbyId, "pending_team");
            if (!string.IsNullOrEmpty(teamStr))
            {
                PendingTeamForJoin = teamStr.Equals("Red", StringComparison.OrdinalIgnoreCase)
                    ? Team.Red : Team.Blue;
                Log.LogInfo($"[SteamLobby] Host invited us to {PendingTeamForJoin}.");
            }
            else
            {
                PendingTeamForJoin = Plugin.Instance.CfgDefaultTeam.Value
                    .Equals("Red", StringComparison.OrdinalIgnoreCase) ? Team.Red : Team.Blue;
            }

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

            RefreshPeerName();
        }

        /// <summary>Persona names arrive asynchronously for players we are not friends
        /// with, so a name that was not cached locally lands here later.</summary>
        private static void OnPersonaStateChange(PersonaStateChange_t change)
        {
            var id = new CSteamID(change.m_ulSteamID);
            if (!_names.ContainsKey(id)) return;
            _names[id] = SteamFriends.GetFriendPersonaName(id);
            RefreshFirstPeerName();
        }

        /// <summary>
        /// Cache the persona name of every OTHER lobby member.
        ///
        /// Used to stop at the first non-self member, which was correct when a lobby held
        /// exactly two people and silently wrong the moment it could hold four - the
        /// third and fourth players simply had no name anywhere in the UI.
        ///
        /// A code-joined player is usually not a friend, so the name may not be cached
        /// locally yet; RequestUserInformation fetches it and OnPersonaStateChange fills
        /// it in when it lands.
        /// </summary>
        private static void RefreshPeerName()
        {
            _names.Clear();
            if (!InLobby) { PeerName = ""; _peerSteamId = CSteamID.Nil; return; }

            var me = SteamUser.GetSteamID();
            int count = SteamMatchmaking.GetNumLobbyMembers(LobbyId);
            for (int i = 0; i < count; i++)
            {
                var member = SteamMatchmaking.GetLobbyMemberByIndex(LobbyId, i);
                if (member == me) continue;

                // RequestUserInformation returns true when it had to go and fetch, in
                // which case the name arrives via OnPersonaStateChange.
                _names[member] = SteamFriends.RequestUserInformation(member, true)
                    ? "" : SteamFriends.GetFriendPersonaName(member);
            }
            RefreshFirstPeerName();
        }

        private static void RefreshFirstPeerName()
        {
            foreach (var kv in _names)
            {
                _peerSteamId = kv.Key;
                PeerName = kv.Value;
                return;
            }
            _peerSteamId = CSteamID.Nil;
            PeerName = "";
        }

        /// <summary>Persona name for a lobby member, or "" if not yet resolved.</summary>
        public static string NameOf(CSteamID id) => _names.TryGetValue(id, out var n) ? n : "";
    }
}
