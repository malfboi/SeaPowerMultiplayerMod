namespace SeapowerMultiplayer
{
    /// <summary>
    /// Tracks which unit the remote player currently has selected.
    /// The locked unit is unselectable by the local player.
    /// Co-op only — ignored in PvP.
    /// </summary>
    public static class UnitLockManager
    {
        // The UniqueID of the unit the remote player currently has selected.
        // 0 = no unit locked.
        private static int _remoteLockedUnitId = 0;

        public static int RemoteLockedUnitId => _remoteLockedUnitId;

        // Cached ObjectBase reference for the remote-locked unit (used by the UI indicator).
        private static SeaPower.ObjectBase _remoteLockedUnit = null;
        public static SeaPower.ObjectBase RemoteLockedUnit => _remoteLockedUnit;

        private static float _selectionBlockedAt = -999f;
        public static float SelectionBlockedAt => _selectionBlockedAt;

        public static void NotifySelectionBlocked()
        {
            _selectionBlockedAt = UnityEngine.Time.time;
        }

        internal static void SetRemoteLockedUnit(SeaPower.ObjectBase unit)
        {
            _remoteLockedUnit = unit;
        }

        /// <summary>Called when a UnitSelected event arrives from the remote player.</summary>
        public static void OnRemoteSelected(int unitId)
        {
            int oldId = _remoteLockedUnitId;
            _remoteLockedUnitId = unitId;
            Plugin.Log.LogDebug($"[UnitLock] Remote player selected unit {unitId} — locked.");
            // If switching directly from one unit to another, clear the previous unit's label.
            if (oldId != 0 && oldId != unitId)
                MapUnitViewModelRegistry.NotifyLockChanged(oldId);
            MapUnitViewModelRegistry.NotifyLockChanged(unitId);
        }

        /// <summary>Called when a UnitDeselected event arrives from the remote player.</summary>
        public static void OnRemoteDeselected()
        {
            Plugin.Log.LogDebug($"[UnitLock] Remote player deselected unit {_remoteLockedUnitId} — unlocked.");
            _remoteLockedUnit = null;
            int oldId = _remoteLockedUnitId;
            _remoteLockedUnitId = 0;
            MapUnitViewModelRegistry.NotifyLockChanged(oldId);
        }

        /// <summary>Returns true if the given unit is currently locked by the remote player.</summary>
        public static bool IsLockedByRemote(int unitId)
        {
            return _remoteLockedUnitId != 0 && _remoteLockedUnitId == unitId;
        }

        /// <summary>Clear lock state on disconnect.</summary>
        public static void Reset()
        {
            int oldId = _remoteLockedUnitId;
            _remoteLockedUnitId = 0;
            _remoteLockedUnit = null;
            _selectionBlockedAt = -999f;
            if (oldId != 0)
                MapUnitViewModelRegistry.NotifyLockChanged(oldId);
            Plugin.Log.LogDebug("[UnitLock] Reset.");
        }
    }
}
