using System.Collections.Generic;
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
    /// Tracks whether all players have loaded and are ready to run.
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

        // Legacy compat
        public static bool BothSidesReady => AllPlayersReady;

        private static readonly HashSet<byte> _readyPlayers = new();

        public static bool AllPlayersReady
        {
            get
            {
                // Check all non-host players in registry have reported ready
                foreach (var kvp in PlayerRegistry.Players)
                {
                    if (kvp.Key == 0) continue; // skip host
                    if (!_readyPlayers.Contains(kvp.Key)) return false;
                }
                return PlayerRegistry.PlayerCount > 1; // at least one client
            }
        }

        public static int ReadyCount => _readyPlayers.Count;
        public static int ExpectedCount => PlayerRegistry.PlayerCount > 0 ? PlayerRegistry.PlayerCount - 1 : 0;

        public static void Reset()
        {
            Plugin.Log.LogInfo("[SimSync] Reset()");
            CurrentState = SimState.Idle;
            _readyPlayers.Clear();
        }

        public static void RemoveFromReady(byte playerId)
        {
            _readyPlayers.Remove(playerId);
            if (PlayerRegistry.Players.TryGetValue(playerId, out var info))
                info.IsReady = false;
        }

        /// <summary>
        /// Called on host when a SessionReady message arrives from a client.
        /// </summary>
        public static void OnClientReady(byte playerId)
        {
            _readyPlayers.Add(playerId);
            if (PlayerRegistry.Players.TryGetValue(playerId, out var info))
                info.IsReady = true;
            Plugin.Log.LogInfo($"[SimSync] Player {playerId} ready ({_readyPlayers.Count}/{ExpectedCount})");

            if (AllPlayersReady)
            {
                CurrentState = SimState.Synchronized;
                Plugin.Log.LogInfo($"[SimSync] All players ready — paused={GameTime.IsPaused()}, TC={GameTime.TimeCompression}");
            }
        }
    }
}
