using System.Collections.Generic;
using System.Reflection;
using SeaPower;
using SeapowerUI;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Tracks live MapUnitViewModel instances so UnitLockManager can push a
    /// PropertyChanged notification when the remote player's lock changes —
    /// the map unit label re-reads <see cref="MapUnitViewModel.ContactInfoLine2"/>
    /// and renders the "[ALLY]" badge.
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

        public static void NotifyLockChanged(int uniqueId)
        {
            foreach (var vm in _instances)
            {
                var obj = vm.Unit?.BaseObject as ObjectBase;
                if (obj != null && obj.UniqueID == uniqueId)
                {
                    _onPropertyChanged?.Invoke(vm, new object[] { "ContactInfoLine2" });
                    return;
                }
            }
        }

        public static void Clear()
        {
            _instances.Clear();
        }
    }
}
