using System.Collections.Generic;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Maintains typed lists of active game objects, populated via Harmony patches
    /// on ObjectBase.Awake/OnDestroy. Replaces per-frame FindObjectsByType calls.
    /// </summary>
    public static class UnitRegistry
    {
        private static readonly List<ObjectBase> _allUnits    = new();
        private static readonly List<Vessel>     _vessels     = new();
        private static readonly List<Submarine>  _submarines  = new();
        private static readonly List<Aircraft>   _aircraft    = new();
        private static readonly List<Helicopter> _helicopters = new();
        private static readonly List<LandUnit>   _landUnits   = new();
        private static readonly List<Missile>    _missiles    = new();
        private static readonly List<Torpedo>    _torpedoes   = new();
        private static readonly List<Bomb>       _bombs       = new();

        // O(1) id lookup, replacing SceneCreator.FindObjectById (linear scan of
        // ObjectsByPvKey) and FindGlobalObjectById (Resources.FindObjectsOfTypeAll)
        // on the polled lookup paths - see StateSerializer.FindById.
        //
        // _idOf is the membership test Register/Unregister use. It has to exist
        // separately from _byId: SetUniqueId re-keys units at runtime (the
        // alignment pass), which leaves _byId holding the OLD id, and a membership
        // test keyed on the current id would then read as "not registered" and
        // double-add to the lists.
        private static readonly Dictionary<int, ObjectBase>  _byId = new();
        private static readonly Dictionary<ObjectBase, int>  _idOf = new();

        public static IReadOnlyList<ObjectBase> All         => _allUnits;
        public static IReadOnlyList<Vessel>     Vessels     => _vessels;
        public static IReadOnlyList<Submarine>  Submarines  => _submarines;
        public static IReadOnlyList<Aircraft>   AircraftList => _aircraft;
        public static IReadOnlyList<Helicopter> Helicopters => _helicopters;
        public static IReadOnlyList<LandUnit>   LandUnits   => _landUnits;
        public static IReadOnlyList<Missile>    Missiles    => _missiles;
        public static IReadOnlyList<Torpedo>    Torpedoes   => _torpedoes;
        public static IReadOnlyList<Bomb>       Bombs       => _bombs;

        /// <summary>O(1) id lookup. Returns null for an unknown id or one whose
        /// object Unity has since destroyed (purged lazily - OnDestroy normally
        /// gets there first via Unregister).</summary>
        public static ObjectBase? ById(int id)
        {
            if (id == 0 || !_byId.TryGetValue(id, out var obj)) return null;
            if (obj == null) // Unity lifetime check - destroyed without OnDestroy reaching us
            {
                _byId.Remove(id);
                _idOf.Remove(obj!); // still a live C# reference; only Unity's == calls it null
                return null;
            }
            return obj;
        }

        public static void Register(ObjectBase obj)
        {
            if (obj == null) return;
            // Idempotent: pooled weapons re-launch without a fresh Awake, so the
            // launch hook re-registers objects that may already be tracked.
            if (_idOf.ContainsKey(obj)) return;

            _allUnits.Add(obj);
            Index(obj);

            switch (obj)
            {
                case Vessel v:     _vessels.Add(v);     break;
                case Submarine s:  _submarines.Add(s);  break;
                case Helicopter h: _helicopters.Add(h); break;
                case Aircraft a:   _aircraft.Add(a);    break;
                case LandUnit l:   _landUnits.Add(l);   break;
                case Missile m:    _missiles.Add(m);    break;
                case Torpedo t:    _torpedoes.Add(t);   break;
                case Bomb b:       _bombs.Add(b);       break;
            }
        }

        public static void Unregister(ObjectBase obj)
        {
            if (obj == null) return;

            if (_idOf.TryGetValue(obj, out int id))
            {
                _idOf.Remove(obj);
                // Only drop the id slot if it still points at THIS object - a
                // re-keyed unit can leave another object owning the old id.
                if (_byId.TryGetValue(id, out var current) && ReferenceEquals(current, obj))
                    _byId.Remove(id);
            }

            _allUnits.Remove(obj);

            switch (obj)
            {
                case Vessel v:     _vessels.Remove(v);     break;
                case Submarine s:  _submarines.Remove(s);  break;
                case Helicopter h: _helicopters.Remove(h); break;
                case Aircraft a:   _aircraft.Remove(a);    break;
                case LandUnit l:   _landUnits.Remove(l);   break;
                case Missile m:    _missiles.Remove(m);    break;
                case Torpedo t:    _torpedoes.Remove(t);   break;
                case Bomb b:       _bombs.Remove(b);       break;
            }
        }

        private static void Index(ObjectBase obj)
        {
            int id = obj.UniqueID;
            _idOf[obj] = id;
            if (id != 0) _byId[id] = obj;
        }

        /// <summary>
        /// Rebuild the id index from the tracked objects. Needed after anything
        /// calls ObjectBase.SetUniqueId - the alignment pass re-keys units in
        /// place, which the Awake/OnDestroy hooks never see.
        /// </summary>
        public static void Reindex()
        {
            _byId.Clear();
            _idOf.Clear();
            for (int i = 0; i < _allUnits.Count; i++)
            {
                var obj = _allUnits[i];
                if (obj != null) Index(obj);
            }
        }

        /// <summary>
        /// Clear all lists. Call on scene load/reset.
        /// </summary>
        public static void Clear()
        {
            _byId.Clear();
            _idOf.Clear();
            _allUnits.Clear();
            _vessels.Clear();
            _submarines.Clear();
            _aircraft.Clear();
            _helicopters.Clear();
            _landUnits.Clear();
            _missiles.Clear();
            _torpedoes.Clear();
            _bombs.Clear();
        }

        /// <summary>
        /// Fallback: scan the scene once and fill all lists.
        /// Used for units that spawned before Harmony patches were active.
        /// </summary>
        public static void PopulateFromScene()
        {
            // Avoid duplicates: clear first, then repopulate
            Clear();

            foreach (var obj in Object.FindObjectsByType<ObjectBase>(FindObjectsSortMode.None))
                Register(obj);

            // The negative cache below remembers ids the fallback scan failed on;
            // a fresh scene makes every one of those verdicts stale.
            StateSerializer.ResetLookupCache();

            Plugin.Log.LogInfo(
                $"[UnitRegistry] PopulateFromScene: {_allUnits.Count} total " +
                $"(V={_vessels.Count} S={_submarines.Count} A={_aircraft.Count} " +
                $"H={_helicopters.Count} L={_landUnits.Count} M={_missiles.Count} T={_torpedoes.Count})");
        }
    }
}
