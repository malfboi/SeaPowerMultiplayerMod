using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SeapowerMultiplayer.Launcher.Services
{
    public static class GameDetector
    {
        private const string GameExe = "Sea Power.exe";

        public static string? AutoDetect()
        {
            foreach (var candidate in GetCandidatePaths())
            {
                if (IsValidGameDir(candidate))
                    return candidate;
            }
            return null;
        }

        public static bool IsValidGameDir(string path)
            => !string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, GameExe));

        public static bool IsBepInExInstalled(string gameDir)
            => File.Exists(Path.Combine(gameDir, "BepInEx", "core", "BepInEx.dll"));

        public static bool IsModInstalled(string gameDir)
            => File.Exists(Path.Combine(gameDir, "BepInEx", "plugins", "SeapowerMultiplayer.dll"));

        public static bool IsProxyStored(string gameDir)
            => File.Exists(Path.Combine(gameDir, "BepInEx", "proxy", "winhttp.dll"));

        private static IEnumerable<string> GetCandidatePaths()
        {
            // Default Steam install path
            yield return @"C:\Program Files (x86)\Steam\steamapps\common\Sea Power";

            // Parse Steam library folders for alternate install locations
            var vdfPath = @"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf";
            if (File.Exists(vdfPath))
            {
                foreach (var libPath in ParseLibraryFolders(vdfPath))
                    yield return Path.Combine(libPath, "steamapps", "common", "Sea Power");
            }
        }

        private static IEnumerable<string> ParseLibraryFolders(string vdfPath)
        {
            // Valve VDF format has lines like:  "path"		"D:\\SteamLibrary"
            var content = File.ReadAllText(vdfPath);
            var regex = new Regex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
            foreach (Match match in regex.Matches(content))
            {
                var path = match.Groups[1].Value.Replace("\\\\", "\\");
                yield return path;
            }
        }
    }
}
