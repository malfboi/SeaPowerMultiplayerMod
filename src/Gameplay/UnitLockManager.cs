using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Tracks which unit the remote player controls and which unit we've claimed
    /// control of in co-op. The Harmony patch on ObjectBase.IsControllable reads
    /// <see cref="IsLockedByRemote"/> to force the game's built-in ally handling
    /// for remote-controlled units on our side.
    ///
    /// "Controlling" = we broadcast a UnitSelected. "Spectating" = we selected a
    /// unit the remote already controls and stayed silent. Co-op only; ignored in PvP.
    /// </summary>
    public static class UnitLockManager
    {
        // Unit the remote player has claimed control of (0 = none).
        private static int _remoteLockedUnitId;

        // Unit we've claimed control of by broadcasting UnitSelected (0 = none).
        private static int _localControlledUnitId;

        public static int RemoteLockedUnitId => _remoteLockedUnitId;
        public static int LocalControlledUnitId => _localControlledUnitId;

        public static void SetLocalControlled(int unitId) => _localControlledUnitId = unitId;
        public static void ClearLocalControlled() => _localControlledUnitId = 0;

        /// <summary>Called when a UnitSelected event arrives from the remote player.</summary>
        public static void OnRemoteSelected(int unitId)
        {
            int previouslyLocked = _remoteLockedUnitId;
            _remoteLockedUnitId = unitId;
            Plugin.Log.LogDebug($"[UnitLock] Remote player selected unit {unitId} — marked uncontrollable locally.");

            if (previouslyLocked != 0 && previouslyLocked != unitId)
                MapUnitViewModelRegistry.NotifyLockChanged(previouslyLocked);
            if (unitId != 0)
                MapUnitViewModelRegistry.NotifyLockChanged(unitId);

            // Remote switched between units — if we were spectating the released one, take over.
            TryAutoClaim(previouslyLocked, unitId);
        }

        /// <summary>Called when a UnitDeselected event arrives from the remote player.</summary>
        public static void OnRemoteDeselected()
        {
            int released = _remoteLockedUnitId;
            _remoteLockedUnitId = 0;
            Plugin.Log.LogDebug($"[UnitLock] Remote player deselected unit {released} — controllable restored.");

            if (released != 0)
                MapUnitViewModelRegistry.NotifyLockChanged(released);

            TryAutoClaim(released, 0);
        }

        /// <summary>
        /// When the remote player releases a unit we're currently spectating locally,
        /// broadcast a UnitSelected so we become the controller without the user
        /// having to deselect-and-reselect.
        /// </summary>
        private static void TryAutoClaim(int releasedRemoteId, int newRemoteId)
        {
            if (releasedRemoteId == 0) return;
            if (releasedRemoteId == newRemoteId) return;
            if (_localControlledUnitId == releasedRemoteId) return;

            var rp = Singleton<RenderPosition>.Instance;
            var selected = rp?.SelectedObject;
            if (selected == null || selected.UniqueID != releasedRemoteId) return;

            NetworkManager.Instance.SendToOther(new GameEventMessage
            {
                EventType = GameEventType.UnitSelected,
                Param     = (float)releasedRemoteId,
            });
            _localControlledUnitId = releasedRemoteId;
            Plugin.Log.LogDebug($"[UnitLock] Auto-claimed control of unit {releasedRemoteId} after remote release.");
        }

        /// <summary>Returns true if the given unit is currently held by the remote player.</summary>
        public static bool IsLockedByRemote(int unitId)
        {
            return _remoteLockedUnitId != 0 && _remoteLockedUnitId == unitId;
        }

        /// <summary>Clear lock state on disconnect.</summary>
        public static void Reset()
        {
            int released = _remoteLockedUnitId;
            _remoteLockedUnitId = 0;
            _localControlledUnitId = 0;
            if (released != 0)
                MapUnitViewModelRegistry.NotifyLockChanged(released);
            Plugin.Log.LogDebug("[UnitLock] Reset.");
        }
    }
}
