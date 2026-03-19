using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SeapowerMultiplayer.Launcher.Services
{
    public static class GameLauncher
    {
        private static Process? _gameProcess;

        public static Task LaunchAsync(string gameDir, Action onExit)
        {
            var proxyDir = Path.Combine(gameDir, "BepInEx", "proxy");

            // Place proxy files in game root
            File.Copy(Path.Combine(proxyDir, "winhttp.dll"),
                       Path.Combine(gameDir, "winhttp.dll"), overwrite: true);
            File.Copy(Path.Combine(proxyDir, "doorstop_config.ini"),
                       Path.Combine(gameDir, "doorstop_config.ini"), overwrite: true);

            // Launch the game
            _gameProcess = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(gameDir, "Sea Power.exe"),
                WorkingDirectory = gameDir,
                UseShellExecute = true,
            });

            if (_gameProcess == null)
            {
                CleanupProxy(gameDir);
                throw new InvalidOperationException("Failed to start Sea Power.exe");
            }

            _gameProcess.EnableRaisingEvents = true;
            _gameProcess.Exited += (_, _) =>
            {
                CleanupProxy(gameDir);
                _gameProcess?.Dispose();
                _gameProcess = null;
                onExit();
            };

            // Register cleanup in case the launcher is closed while game is running
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupProxy(gameDir);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Remove proxy files from game root. Safe to call multiple times.
        /// Called on game exit, launcher startup (crash recovery), and launcher shutdown.
        /// </summary>
        public static void CleanupProxy(string gameDir)
        {
            TryDelete(Path.Combine(gameDir, "winhttp.dll"));
            TryDelete(Path.Combine(gameDir, "doorstop_config.ini"));
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
