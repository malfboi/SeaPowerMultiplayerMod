using System.Collections.Generic;
using System.Reflection;
using SeaPower;
using SeapowerUI;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Tracks all live MapUnitViewModel instances so UnitLockManager can
    /// trigger PropertyChanged when lock state changes.
    /// </summary>
    public static class MapUnitViewModelRegistry
    {
        private static readonly HashSet<MapUnitViewModel> _instances = new HashSet<MapUnitViewModel>();

        private static readonly MethodInfo _onPropertyChanged =
            typeof(MapUnitViewModel).GetMethod(
                "OnPropertyChanged",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);

        public static void Register(MapUnitViewModel vm)
        {
            if (vm != null) _instances.Add(vm);
        }

        public static void Unregister(MapUnitViewModel vm)
        {
            _instances.Remove(vm);
        }

        /// <summary>
        /// Fire PropertyChanged("ContactInfoLine4") on the MapUnitViewModel for
        /// the given unit so Noesis re-reads the getter (which now returns "[BUSY]"
        /// or the original value depending on lock state).
        /// </summary>
        public static void NotifyLockChanged(int uniqueId)
        {
            foreach (var vm in _instances)
            {
                var obj = vm.Unit?.BaseObject as ObjectBase;
                if (obj != null && obj.UniqueID == uniqueId)
                {
                    _onPropertyChanged?.Invoke(vm, new object[] { "ContactInfoLine2" });
                    Plugin.Log.LogDebug($"[UnitLock] Notified MapUnitViewModel for unit {uniqueId}");
                    return;
                }
            }
            Plugin.Log.LogDebug($"[UnitLock] No MapUnitViewModel found for unit {uniqueId}");
        }

        public static void Clear()
        {
            _instances.Clear();
        }
    }
}
