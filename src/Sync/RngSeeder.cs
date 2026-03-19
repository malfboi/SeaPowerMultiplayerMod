using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Seeds the game's main RNG sources identically on both host and client
    /// to reduce simulation drift.
    /// Doesn't really work because of the way the game uses RNG (e.g. AI decisions are made at different times on host vs client), but it's worth doing anyway to minimize drift where possible.
    /// </summary>
    public static class RngSeeder
    {
        public static void SeedAll(int seed)
        {
            // Seed the global System.Random used by damage, compartments, etc.
            Globals._rnd = new System.Random(seed);

            // Seed AI._aiRandom (private static) via reflection
            var aiRngField = AccessTools.Field(typeof(AI), "_aiRandom");
            if (aiRngField != null)
                aiRngField.SetValue(null, new System.Random(seed + 1));

            // Seed UnityEngine.Random (used by Utils.RandomFloat etc.)
            Random.InitState(seed + 2);

            Plugin.Log.LogInfo($"[RNG] Seeded all RNGs with base seed {seed}");
        }
    }
}
