namespace SeapowerMultiplayer
{
    /// <summary>
    /// Host-side change-detection reset, for when a player joins a battle already in
    /// progress.
    ///
    /// Every host stream is change-detected: it remembers what it last sent and stays
    /// quiet while nothing moves. That is exactly wrong for a joiner, whose picture
    /// starts empty while every one of those tables believes the far end is already up
    /// to date. Without this the new player waits out each channel's full-sweep interval
    /// - up to ten seconds of blank status lines, missing sensor state and an empty
    /// Flight Ops window - and anything with no periodic sweep at all never arrives.
    ///
    /// Deliberately a re-send rather than a per-peer diff: it costs one extra sweep for
    /// the players already in, which is far cheaper than teaching six independent
    /// streamers to track what each peer has seen.
    /// </summary>
    internal static class HostStreamers
    {
        public static void ForceFullResend()
        {
            if (!Plugin.Instance.CfgIsHost.Value) return;

            HostEntityStreamer.Instance?.ClearTracking();
            UnitStatusManager.Reset();
            SensorStateManager.ForceFullSweep();
            JamStateManager.ForceResend();
            FlightDeckStreamer.Reset();
            ContactSyncManager.ForceFullSweep();

            // The census is the backstop for anything the streams still miss - a fresh
            // manifest lets the joiner ask for whatever it did not get.
            EntityCensusManager.ForceCensusNow();

            Plugin.Log.LogInfo("[Session] Host streams reset for a joining player.");
        }
    }
}
