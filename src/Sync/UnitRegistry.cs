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

        public static IReadOnlyList<ObjectBase> All         => _allUnits;
        public static IReadOnlyList<Vessel>     Vessels     => _vessels;
        public static IReadOnlyList<Submarine>  Submarines  => _submarines;
        public static IReadOnlyList<Aircraft>   AircraftList => _aircraft;
        public static IReadOnlyList<Helicopter> Helicopters => _helicopters;
        public static IReadOnlyList<LandUnit>   LandUnits   => _landUnits;
        public static IReadOnlyList<Missile>    Missiles    => _missiles;
        public static IReadOnlyList<Torpedo>    Torpedoes   => _torpedoes;

        public static void Register(ObjectBase obj)
        {
            if (obj == null) return;

            _allUnits.Add(obj);

            switch (obj)
            {
                case Vessel v:     _vessels.Add(v);     break;
                case Submarine s:  _submarines.Add(s);  break;
                case Helicopter h: _helicopters.Add(h); break;
                case Aircraft a:   _aircraft.Add(a);    break;
                case LandUnit l:   _landUnits.Add(l);   break;
                case Missile m:    _missiles.Add(m);    break;
                case Torpedo t:    _torpedoes.Add(t);   break;
            }
        }

        public static void Unregister(ObjectBase obj)
        {
            if (obj == null) return;

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
            }
        }

        /// <summary>
        /// Clear all lists. Call on scene load/reset.
        /// </summary>
        public static void Clear()
        {
            _allUnits.Clear();
            _vessels.Clear();
            _submarines.Clear();
            _aircraft.Clear();
            _helicopters.Clear();
            _landUnits.Clear();
            _missiles.Clear();
            _torpedoes.Clear();
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

            Plugin.Log.LogInfo(
                $"[UnitRegistry] PopulateFromScene: {_allUnits.Count} total " +
                $"(V={_vessels.Count} S={_submarines.Count} A={_aircraft.Count} " +
                $"H={_helicopters.Count} L={_landUnits.Count} M={_missiles.Count} T={_torpedoes.Count})");
        }
    }
}
