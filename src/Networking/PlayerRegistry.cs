using System.Collections.Generic;
using System.Linq;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    public class PlayerInfo
    {
        public byte PlayerId;
        public int ConnectionId;      // -1 for host (local)
        public string DisplayName;
        public int AssignedTfIndex;    // index in TaskforceManager._taskForces, -1 = unassigned
        public byte TeamSide;         // 0 = player side, 1 = enemy side
        public bool IsReady;

        public PlayerInfo(byte playerId, int connectionId, string displayName)
        {
            PlayerId = playerId;
            ConnectionId = connectionId;
            DisplayName = displayName;
            AssignedTfIndex = -1;
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
        /// Each player is authoritative for units in their assigned task force.
        /// Host is also authoritative for unassigned task forces (fallback).
        /// </summary>
        public static bool IsLocallyAuthoritative(ObjectBase unit)
        {
            if (unit == null) return false;

            var localPlayer = GetLocalPlayer();
            if (localPlayer == null) return Plugin.Instance.CfgIsHost.Value;

            var unitTf = unit._taskforce;
            if (unitTf == null) return Plugin.Instance.CfgIsHost.Value;

            // Check if unit's task force matches local player's assignment
            if (localPlayer.AssignedTfIndex >= 0)
            {
                var assignedTf = GetTaskforceByIndex(localPlayer.AssignedTfIndex);
                if (assignedTf != null && unitTf == assignedTf)
                    return true;
            }

            // Host fallback: authoritative for any task force not assigned to another player
            if (Plugin.Instance.CfgIsHost.Value)
            {
                if (!IsTaskforceAssignedToAnyPlayer(unitTf))
                    return true;
            }

            return false;
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

        /// <summary>
        /// Returns the locally resolved Taskforce for the local player, or null if unassigned.
        /// </summary>
        public static Taskforce GetLocalTaskforce()
        {
            var local = GetLocalPlayer();
            if (local == null || local.AssignedTfIndex < 0) return null;
            return GetTaskforceByIndex(local.AssignedTfIndex);
        }

        public static PlayerInfo GetLocalPlayer()
        {
            _players.TryGetValue(LocalPlayerId, out var p);
            return p;
        }

        private static bool IsTaskforceAssignedToAnyPlayer(Taskforce tf)
        {
            int idx = GetTaskforceIndex(tf);
            if (idx < 0) return false;
            foreach (var p in _players.Values)
                if (p.AssignedTfIndex == idx) return true;
            return false;
        }

        // ── Event handlers (called from NetworkManager on main thread) ────────

        public static void OnPeerConnected(int connectionId)
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;

            string name = $"Player {_nextPlayerId}";
            byte playerId = RegisterClient(connectionId, name);

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
                    AssignedTfIndex = entry.AssignedTfIndex,
                    TeamSide = entry.TeamSide,
                };
                _players[entry.PlayerId] = info;
            }
            LocalPlayerId = msg.YourPlayerId;
            Plugin.Log.LogInfo($"[PlayerRegistry] Assignment received: {_players.Count} players, localId={LocalPlayerId}");
        }

        /// <summary>Host: assign a task force to a player by playerId.</summary>
        public static void HostAssignTaskforce(byte playerId, int tfIndex)
        {
            if (!_players.TryGetValue(playerId, out var info)) return;
            info.AssignedTfIndex = tfIndex;
            Plugin.Log.LogInfo($"[PlayerRegistry] Assigned player {playerId} to TF index {tfIndex}");
            BroadcastAssignment();
        }

        /// <summary>Host: assign a team side to a player.</summary>
        public static void HostAssignTeam(byte playerId, byte teamSide)
        {
            if (!_players.TryGetValue(playerId, out var info)) return;
            info.TeamSide = teamSide;
            Plugin.Log.LogInfo($"[PlayerRegistry] Assigned player {playerId} to team {teamSide}");
            BroadcastAssignment();
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
                    AssignedTfIndex = p.AssignedTfIndex,
                    DisplayName = p.DisplayName,
                });
            }

            return msg;
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
