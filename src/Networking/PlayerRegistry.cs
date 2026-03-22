using System.Collections.Generic;
using System.Linq;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    public class PlayerInfo
    {
        public byte PlayerId;
        public int ConnectionId;      // -1 for host (local)
        public string DisplayName;
        public HashSet<string> AssignedTfNames = new(); // empty = "All" (controls entire team)
        public byte TeamSide;         // 0 = player side, 1 = enemy side
        public bool IsReady;

        public PlayerInfo(byte playerId, int connectionId, string displayName)
        {
            PlayerId = playerId;
            ConnectionId = connectionId;
            DisplayName = displayName;
            TeamSide = 0;
        }
    }

    public static class PlayerRegistry
    {
        private static readonly Dictionary<byte, PlayerInfo> _players = new();
        private static byte _nextPlayerId = 1;

        public static byte LocalPlayerId { get; private set; }
        public static IReadOnlyDictionary<byte, PlayerInfo> Players => _players;
        public static int PlayerCount => _players.Count;

        /// <summary>
        /// Set by UI invite buttons, consumed by OnPeerConnected to auto-assign team.
        /// 0 = Blue (player side), 1 = Red (enemy side).
        /// </summary>
        public static byte PendingInviteTeam { get; set; }

        public static void RegisterHost()
        {
            _players.Clear();
            _nextPlayerId = 1;

            var host = new PlayerInfo(0, -1, GetLocalDisplayName());
            _players[0] = host;
            LocalPlayerId = 0;

            Plugin.Log.LogInfo($"[PlayerRegistry] Host registered: {host.DisplayName}");
        }

        public static byte RegisterClient(int connectionId, string name)
        {
            byte id = _nextPlayerId++;
            var info = new PlayerInfo(id, connectionId, name);
            _players[id] = info;
            Plugin.Log.LogInfo($"[PlayerRegistry] Client registered: id={id} conn={connectionId} name={name}");
            return id;
        }

        public static void UnregisterClient(byte playerId)
        {
            if (_players.Remove(playerId))
                Plugin.Log.LogInfo($"[PlayerRegistry] Player {playerId} unregistered");
        }

        public static PlayerInfo FindByConnectionId(int connectionId)
        {
            foreach (var p in _players.Values)
                if (p.ConnectionId == connectionId) return p;
            return null;
        }

        public static void Reset()
        {
            _players.Clear();
            _nextPlayerId = 1;
            LocalPlayerId = 0;
        }

        /// <summary>
        /// Returns true if the local player is authoritative for this unit.
        /// AssignedTfNames empty ("All"): authoritative for every TF on the player's team side.
        /// AssignedTfNames non-empty: authoritative ONLY for those specific TFs.
        /// Host fallback: also authoritative for TFs not assigned to any player.
        /// </summary>
        public static bool IsLocallyAuthoritative(ObjectBase unit)
        {
            if (unit == null) return false;

            var localPlayer = GetLocalPlayer();
            if (localPlayer == null) return Plugin.Instance.CfgIsHost.Value;

            var unitTf = unit._taskforce;
            if (unitTf == null) return Plugin.Instance.CfgIsHost.Value;

            if (localPlayer.AssignedTfNames.Count > 0)
            {
                // Specific group assignment: check if unit is in an assigned formation group
                if (FormationRegistry.IsInAssignedGroup(unit, localPlayer.AssignedTfNames))
                    return true;
            }
            else
            {
                // "All" (empty set): authoritative for every unit on the local player's side.
                // Use Globals._playerTaskforce directly — it's always correct after side swap.
                if (unitTf == Globals._playerTaskforce)
                    return true;
            }

            // Host fallback: authoritative for any unit whose group isn't assigned to any player
            if (Plugin.Instance.CfgIsHost.Value)
            {
                if (!FormationRegistry.IsAssignedToAnyPlayer(unit))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true if the given taskforce belongs to the specified team side.
        /// TeamSide 0 (Blue): tf.Side == Player or Ally
        /// TeamSide 1 (Red): tf.Side == Enemy
        /// </summary>
        public static bool IsOnTeamSide(Taskforce tf, byte teamSide)
        {
            if (tf == null) return false;
            if (teamSide == 0)
                return tf.Side == Taskforce.TfType.Player || tf.Side == Taskforce.TfType.Ally;
            else
                return tf.Side == Taskforce.TfType.Enemy;
        }

        /// <summary>
        /// Returns the Taskforce object for a given index in TaskforceManager.
        /// </summary>
        public static Taskforce GetTaskforceByIndex(int index)
        {
            if (!Singleton<TaskforceManager>.InstanceExists(false)) return null;
            var taskForces = Singleton<TaskforceManager>.Instance._taskForces;
            if (index < 0 || index >= taskForces.Count) return null;
            return taskForces[index];
        }

        /// <summary>
        /// Returns the index of a Taskforce in TaskforceManager's list, or -1 if not found.
        /// </summary>
        public static int GetTaskforceIndex(Taskforce tf)
        {
            if (tf == null || !Singleton<TaskforceManager>.InstanceExists(false)) return -1;
            var taskForces = Singleton<TaskforceManager>.Instance._taskForces;
            for (int i = 0; i < taskForces.Count; i++)
                if (taskForces[i] == tf) return i;
            return -1;
        }

        public static PlayerInfo GetLocalPlayer()
        {
            _players.TryGetValue(LocalPlayerId, out var p);
            return p;
        }

        // ── Event handlers (called from NetworkManager on main thread) ────────

        public static void OnPeerConnected(int connectionId)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;

            string name = $"Player {_nextPlayerId}";
            byte playerId = RegisterClient(connectionId, name);

            // Apply pending invite team (set by "Invite to Blue/Red" buttons).
            // For direct connect (non-Steam), default to Red (1) since the host is already Blue.
            bool isSteam = Plugin.Instance.CfgTransport.Value == "Steam";
            byte defaultTeam = isSteam ? PendingInviteTeam : (byte)1;
            if (_players.TryGetValue(playerId, out var newPlayer))
                newPlayer.TeamSide = defaultTeam;

            // Send full assignment to all clients
            BroadcastAssignment();

            // Auto-sync: if a game session is active, send state to the new player
            if (SimSyncManager.CurrentState == SimState.Synchronized)
            {
                Plugin.Log.LogInfo($"[PlayerRegistry] Player {playerId} joined mid-game, triggering auto-sync");
                SessionManager.CaptureAndSend();
            }
        }

        public static void OnPeerDisconnected(int connectionId)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;

            var player = FindByConnectionId(connectionId);
            if (player == null) return;

            Plugin.Log.LogInfo($"[PlayerRegistry] Player {player.PlayerId} ({player.DisplayName}) disconnected");

            // Remove from ready tracking
            SimSyncManager.RemoveFromReady(player.PlayerId);

            UnregisterClient(player.PlayerId);

            // Broadcast updated assignment (host now covers disconnected player's TF)
            BroadcastAssignment();
        }

        /// <summary>Client-side handler for PlayerAssignment messages.</summary>
        public static void OnAssignmentReceived(PlayerAssignmentMessage msg)
        {
            _players.Clear();
            foreach (var entry in msg.Entries)
            {
                var info = new PlayerInfo(entry.PlayerId, -1, entry.DisplayName)
                {
                    TeamSide = entry.TeamSide,
                };
                info.AssignedTfNames = new HashSet<string>(entry.AssignedTfNames);
                _players[entry.PlayerId] = info;
            }
            LocalPlayerId = msg.YourPlayerId;
            Plugin.Log.LogInfo($"[PlayerRegistry] Assignment received: {_players.Count} players, localId={LocalPlayerId}");
        }

        /// <summary>Host: toggle a taskforce assignment for a player.</summary>
        public static void HostToggleTaskforce(byte playerId, string tfName)
        {
            if (!_players.TryGetValue(playerId, out var info)) return;
            if (info.AssignedTfNames.Contains(tfName))
                info.AssignedTfNames.Remove(tfName);
            else
                info.AssignedTfNames.Add(tfName);
            Plugin.Log.LogInfo($"[PlayerRegistry] Toggled TF '{tfName}' for player {playerId} (now: {string.Join(", ", info.AssignedTfNames)})");
            BroadcastAssignment();
        }

        /// <summary>Host: set a player to control all TFs on their team.</summary>
        public static void HostAssignAll(byte playerId)
        {
            if (!_players.TryGetValue(playerId, out var info)) return;
            info.AssignedTfNames.Clear();
            Plugin.Log.LogInfo($"[PlayerRegistry] Set player {playerId} to All TFs");
            BroadcastAssignment();
        }

        /// <summary>Host: assign a team side to a player. Resets TF to "All" since old TF may not exist on the new team.
        /// Triggers a session re-sync so the player reloads with the correct save file for their new side.</summary>
        public static void HostAssignTeam(byte playerId, byte teamSide)
        {
            if (!_players.TryGetValue(playerId, out var info)) return;
            info.TeamSide = teamSide;
            info.AssignedTfNames.Clear(); // reset to "All" — old TFs don't exist on new team
            Plugin.Log.LogInfo($"[PlayerRegistry] Assigned player {playerId} to team {teamSide}");
            BroadcastAssignment();

            // Re-sync the session so the player reloads with the correct save for their new side
            if (SimSyncManager.CurrentState == SimState.Synchronized)
            {
                Plugin.Log.LogInfo($"[PlayerRegistry] Team change for player {playerId}, triggering session re-sync");
                SessionManager.CaptureAndSend();
            }
        }

        public static void BroadcastAssignment()
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;

            // Send per-client (each gets their own YourPlayerId)
            foreach (var player in _players.Values)
            {
                if (player.ConnectionId < 0) continue; // skip host
                var msg = BuildAssignmentMessage(player.PlayerId);
                NetworkManager.Instance.SendToClient(player.ConnectionId, msg);
            }
        }

        private static PlayerAssignmentMessage BuildAssignmentMessage(byte recipientPlayerId)
        {
            var msg = new PlayerAssignmentMessage
            {
                YourPlayerId = recipientPlayerId,
            };

            foreach (var p in _players.Values)
            {
                msg.Entries.Add(new PlayerAssignmentEntry
                {
                    PlayerId = p.PlayerId,
                    TeamSide = p.TeamSide,
                    AssignedTfNames = new List<string>(p.AssignedTfNames),
                    DisplayName = p.DisplayName,
                });
            }

            return msg;
        }

        /// <summary>
        /// Returns formation/lone-unit groups for the given team side.
        /// Delegates to FormationRegistry for formation-level granularity.
        /// </summary>
        public static List<(string groupKey, string displayName, int unitCount)> GetTeamGroups(byte teamSide)
        {
            return FormationRegistry.GetTeamGroups(teamSide);
        }

        private static string GetLocalDisplayName()
        {
            try
            {
                if (Steamworks.SteamAPI.IsSteamRunning())
                    return Steamworks.SteamFriends.GetPersonaName();
            }
            catch { /* Steam not available */ }
            return "Host";
        }
    }
}
