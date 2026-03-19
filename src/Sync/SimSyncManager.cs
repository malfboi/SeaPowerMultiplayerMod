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
    /// Tracks whether both sides have loaded and are ready to run.
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

        public static bool BothSidesReady { get; set; }

        public static void Reset()
        {
            Plugin.Log.LogInfo("[SimSync] Reset()");
            CurrentState = SimState.Idle;
            BothSidesReady = false;
        }

        /// <summary>
        /// Called on host when a SessionReady message arrives from the client.
        /// </summary>
        public static void OnClientReady()
        {
            BothSidesReady = true;
            CurrentState = SimState.Synchronized;
            Plugin.Log.LogInfo($"[SimSync] Client ready — paused={GameTime.IsPaused()}, TC={GameTime.TimeCompression}");
        }
    }
}
