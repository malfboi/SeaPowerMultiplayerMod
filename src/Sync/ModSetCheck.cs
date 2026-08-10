using System.Collections.Generic;
using System.Text;
using SeaPower;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Are both players running the same set of enabled mods?
    ///
    /// WHY IT MATTERS. SPMM addresses a great deal by INDEX, on the stated assumption
    /// that both machines build the same list from the same ini: weapon mounts
    /// (UnitStatusMessage.Mount is parallel to _obp._weaponSystems), sensors
    /// (SensorStateManager's bitmask is position in _obp._sensorSystems), and flight
    /// deck vehicles (FlightDeckStateMessage.VehicleIdx into _vehiclesOnBoard). A
    /// content mod that adds, removes or reorders any of those on one machine only
    /// shifts every index past it - so a mount reports the wrong engagement, a sensor
    /// toggle lands on the wrong emitter, and a launch readies the wrong airframe.
    ///
    /// The handshake already refuses outright on a Sea Power build mismatch for exactly
    /// this reason. Mods break the same invariants; the difference is that a build
    /// mismatch hangs the load visibly, while a mod mismatch corrupts quietly. Hence a
    /// warning that names the discrepancy rather than leaving players to discover it as
    /// "the mod is broken".
    ///
    /// A HASH AND A COUNT, NOT THE NAMES. Reliable packets above the ~1000-byte MTU
    /// floor fragment and arrive corrupted in the game's Mono runtime (the same finding
    /// that forced FlightDeckState to chunk), and a player with forty mods would put the
    /// handshake over it. Overflowing the handshake to diagnose a mod list would be a
    /// worse bug than the one being diagnosed. Each machine logs its own list instead,
    /// so the two players have everything they need to compare.
    ///
    /// SET, NOT ORDER. Two players with the same mods loaded in a different order are
    /// treated as matching. Order can matter - where two mods override the same ini, the
    /// later one wins - but that is rare enough, and flagging it would fire on pairs who
    /// are in fact fine.
    ///
    /// KNOWN GAP: NAMES, NOT VERSIONS. A mod is identified by its directory name, so two
    /// players on DIFFERENT VERSIONS of the same mod fingerprint identically and pass.
    /// Workshop items update in place under the same published-file-ID directory, so
    /// this is a live case rather than a theoretical one - and an update that adds a
    /// weapon system to a vessel shifts exactly the indices this exists to protect.
    /// _info.ini does not help: its ApproximateVersion tracks the GAME's MAJOR.MINOR as
    /// a compatibility marker, not the mod's own version. Closing it means folding
    /// SteamUGC.GetItemInstallInfo's punTimeStamp into the hash for subscribed items
    /// (UI.WorkshopVersionCheck already works that API), with something weaker - write
    /// time, or file names and sizes - for hand-installed mods.
    /// </summary>
    internal static class ModSetCheck
    {
        /// <summary>The enabled mod directories, by name, excluding the base-game
        /// entries. Mirrors FileManager's own ModsEnabled test (FileManager.cs:55) so
        /// this counts exactly what the game counts as "a mod is on".</summary>
        internal static List<string> LocalMods()
        {
            var names = new List<string>();

            if (!Singleton<FileManager>.InstanceExists(false)) return names;
            var fm = Singleton<FileManager>.Instance;
            var dirs = fm?.Directories;
            if (dirs == null) return names;

            for (int i = 0; i < dirs.Count; i++)
            {
                var d = dirs[i];
                if (d == null || !d.IsChecked || d.DirectoryInfo == null) continue;
                if (fm!.specialDirectories != null
                    && System.Array.IndexOf(fm.specialDirectories, d.DirectoryInfo) >= 0) continue;

                names.Add(d.DirectoryInfo.Name);
            }

            names.Sort(System.StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>FNV-1a over the sorted, lower-cased names.
        ///
        /// Not string.GetHashCode: that is not required to agree between processes or
        /// runtimes, and the two ends compute this independently - a hash that only
        /// happens to match would make this check silently useless.</summary>
        internal static uint Fingerprint(List<string> mods)
        {
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < mods.Count; i++)
                {
                    string s = mods[i].ToLowerInvariant();
                    for (int c = 0; c < s.Length; c++)
                    {
                        h ^= s[c];
                        h *= 16777619u;
                    }
                    h ^= (uint)'\n';   // separator, so {"ab","c"} and {"a","bc"} differ
                    h *= 16777619u;
                }
                return h;
            }
        }

        internal static uint LocalFingerprint() => Fingerprint(LocalMods());

        /// <summary>Log this machine's list once per handshake, on both ends. This is
        /// what makes a hash-only comparison actionable: each player can read their own
        /// side and the two can be compared directly.</summary>
        internal static void LogLocal(string role)
        {
            var mods = LocalMods();
            if (mods.Count == 0)
            {
                Plugin.Log.LogInfo($"[Mods] {role}: no mods enabled beyond the base game.");
                return;
            }

            var sb = new StringBuilder();
            for (int i = 0; i < mods.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(mods[i]);
            }
            Plugin.Log.LogInfo($"[Mods] {role}: {mods.Count} enabled (fp={LocalFingerprint():X8}) - {sb}");
        }

        /// <summary>The warning to show, or null when the two sets agree.</summary>
        internal static string? Compare(uint remoteFingerprint, int remoteCount)
        {
            var mine = LocalMods();
            if (Fingerprint(mine) == remoteFingerprint) return null;

            string counts = mine.Count == remoteCount
                // Same number, different set: the more confusing case, so say so plainly
                // rather than leaving the player counting and finding they agree.
                ? $"You each have {mine.Count}, but they are not the same mods."
                : $"You have {mine.Count}, they have {remoteCount}.";

            return "Your enabled mods do not match your partner's. " + counts +
                   " Mods that change ships, aircraft, weapons or sensors will desync — " +
                   "mounts, sensors and flight decks are matched by position, so a mod on " +
                   "one side only shifts them. Each player's list is in their log under [Mods]. " +
                   "Cosmetic mods (UI, sound) are usually harmless.";
        }
    }
}
