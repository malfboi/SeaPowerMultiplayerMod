using System.Collections.Generic;
using System.Reflection;
using SeaPower;
using SeapowerUI;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Tracks live MapUnitViewModel instances so <see cref="FormationOwnership"/> can
    /// push a PropertyChanged notification when ownership changes - the map unit label
    /// re-reads <see cref="MapUnitViewModel.ContactInfoLine2"/> and renders the owning
    /// player's name.
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
        /// Re-read the label for one unit.
        ///
        /// Note the loop does not stop at the first hit. One unit can have several live
        /// MapUnitViewModels across map layers, and the early `return` this replaced left
        /// every layer but one showing a stale badge.
        /// </summary>
        public static void NotifyOwnershipChanged(int uniqueId)
        {
            foreach (var vm in _instances)
            {
                var obj = vm.Unit?.BaseObject as ObjectBase;
                if (obj != null && obj.UniqueID == uniqueId)
                    _onPropertyChanged?.Invoke(vm, new object[] { "ContactInfoLine2" });
            }
        }

        /// <summary>Re-read every label. A full ownership snapshot can touch any unit on
        /// the map, and iterating the few dozen live view models once is cheaper than
        /// working out which ones actually moved.</summary>
        public static void NotifyAll()
        {
            foreach (var vm in _instances)
                _onPropertyChanged?.Invoke(vm, new object[] { "ContactInfoLine2" });
        }


        public static void Clear()
        {
            _instances.Clear();
        }
    }
}
