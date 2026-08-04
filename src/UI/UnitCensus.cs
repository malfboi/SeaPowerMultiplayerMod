using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer.UI
{
    /// <summary>
    /// Unit and projectile tallies for the SYNC HEALTH readout.
    ///
    /// Seven FindObjectsByType sweeps is not something to run per frame - the
    /// IMGUI overlay ran them every 0.5 s forever, including at the main menu,
    /// to feed a section that only draws when connected and expanded. Here the
    /// caller decides when it is worth the cost.
    /// </summary>
    internal static class UnitCensus
    {
        private static readonly System.Reflection.FieldInfo LaunchPlatformField =
            AccessTools.Field(typeof(WeaponBase), "_launchPlatform");

        internal static int OwnVessels, OwnSubs, OwnAir, OwnLand, OwnMissiles, OwnTorps;
        internal static int EnemyVessels, EnemySubs, EnemyAir, EnemyLand, EnemyMissiles, EnemyTorps;

        internal static int SurfaceAndSubTotal => OwnVessels + EnemyVessels + OwnSubs + EnemySubs;
        internal static int AirTotal => OwnAir + EnemyAir;

        internal static void Refresh(bool isPvP)
        {
            var playerTf = Globals._playerTaskforce;

            OwnVessels = OwnSubs = OwnAir = OwnLand = OwnMissiles = OwnTorps = 0;
            EnemyVessels = EnemySubs = EnemyAir = EnemyLand = EnemyMissiles = EnemyTorps = 0;

            bool Mine(ObjectBase? o) => isPvP && playerTf != null && o != null && o._taskforce == playerTf;

            foreach (var v in Object.FindObjectsByType<Vessel>(FindObjectsSortMode.None))
                if (Mine(v)) OwnVessels++; else EnemyVessels++;

            foreach (var s in Object.FindObjectsByType<Submarine>(FindObjectsSortMode.None))
                if (Mine(s)) OwnSubs++; else EnemySubs++;

            foreach (var a in Object.FindObjectsByType<Aircraft>(FindObjectsSortMode.None))
                if (Mine(a)) OwnAir++; else EnemyAir++;

            foreach (var h in Object.FindObjectsByType<Helicopter>(FindObjectsSortMode.None))
                if (Mine(h)) OwnAir++; else EnemyAir++;

            foreach (var l in Object.FindObjectsByType<LandUnit>(FindObjectsSortMode.None))
                if (Mine(l)) OwnLand++; else EnemyLand++;

            foreach (var m in Object.FindObjectsByType<Missile>(FindObjectsSortMode.None))
                if (Mine(LaunchPlatformField?.GetValue(m) as ObjectBase)) OwnMissiles++; else EnemyMissiles++;

            foreach (var t in Object.FindObjectsByType<Torpedo>(FindObjectsSortMode.None))
                if (Mine(LaunchPlatformField?.GetValue(t) as ObjectBase)) OwnTorps++; else EnemyTorps++;
        }

        internal static string DescribeUnits(bool isPvP)
        {
            if (isPvP)
            {
                string s = $"Ships: own {OwnVessels}  enemy {EnemyVessels}\n"
                         + $"Subs:  own {OwnSubs}  enemy {EnemySubs}\n"
                         + $"Air:   own {OwnAir}  enemy {EnemyAir}";
                if (OwnLand + EnemyLand > 0)
                    s += $"\nLand:  own {OwnLand}  enemy {EnemyLand}";
                return s;
            }

            string c = $"Ships: {OwnVessels + EnemyVessels}   Subs: {OwnSubs + EnemySubs}\n"
                     + $"Air: {AirTotal}";
            if (OwnLand + EnemyLand > 0)
                c += $"\nLand: {OwnLand + EnemyLand}";
            return c;
        }

        internal static string DescribeProjectiles(bool isPvP)
        {
            int msl  = OwnMissiles + EnemyMissiles;
            int torp = OwnTorps + EnemyTorps;
            return isPvP
                ? $"Missiles: {msl} (own {OwnMissiles} / enemy {EnemyMissiles})\n"
                  + $"Torpedoes: {torp} (own {OwnTorps} / enemy {EnemyTorps})"
                : $"Missiles: {msl}   Torpedoes: {torp}";
        }
    }
}
