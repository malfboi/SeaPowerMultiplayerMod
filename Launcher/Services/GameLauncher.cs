using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SeapowerMultiplayer.Launcher.Services
{
    public static class GameLauncher
    {
        /// <summary>
        /// How long (in seconds) to wait for a new game process to appear after one exits.
        /// Steam may restart the game, so we need to give it time to spawn a new process.
        /// </summary>
        private const int RestartGracePeriodSeconds = 15;

        public static Task LaunchAsync(string gameDir, Action onExit)
        {
            var proxyDir = Path.Combine(gameDir, "BepInEx", "proxy");

            // Place proxy files in game root
            File.Copy(Path.Combine(proxyDir, "winhttp.dll"),
                       Path.Combine(gameDir, "winhttp.dll"), overwrite: true);
            File.Copy(Path.Combine(proxyDir, "doorstop_config.ini"),
                       Path.Combine(gameDir, "doorstop_config.ini"), overwrite: true);

            // Launch the game
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(gameDir, "Sea Power.exe"),
                WorkingDirectory = gameDir,
                UseShellExecute = true,
            });

            if (proc == null)
            {
                CleanupProxy(gameDir);
                throw new InvalidOperationException("Failed to start Sea Power.exe");
            }

            // Monitor game lifecycle on a background thread.
            // Steam often restarts the game (original process exits, new one spawns
            // via Steam), so we track by process name rather than a single PID.
            _ = MonitorGameLifecycleAsync(proc, gameDir, onExit);

            // Register cleanup in case the launcher is closed while game is running
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupProxy(gameDir);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Monitors the game process lifecycle, handling Steam restarts.
        /// Waits for the initial process to exit, then polls for new game processes.
        /// Only cleans up proxy files and signals exit when the game is truly closed.
        /// </summary>
        private static async Task MonitorGameLifecycleAsync(
            Process initialProc, string gameDir, Action onExit)
        {
            try
            {
                await Task.Run(() => initialProc.WaitForExit());
                initialProc.Dispose();

                // After each process exit, wait for a potential Steam restart
                while (true)
                {
                    Process? gameProc = null;
                    for (int i = 0; i < RestartGracePeriodSeconds; i++)
                    {
                        await Task.Delay(1_000);
                        gameProc = FindGameProcess();
                        if (gameProc != null) break;
                    }

                    if (gameProc == null)
                        break; // No new process appeared — game is truly closed

                    // A restarted process was found — wait for it to exit
                    await Task.Run(() => gameProc.WaitForExit());
                    gameProc.Dispose();
                }
            }
            finally
            {
                CleanupProxy(gameDir);
                onExit();
            }
        }

        /// <summary>
        /// Find any running Sea Power game process.
        /// </summary>
        private static Process? FindGameProcess()
        {
            string[] names = { "Sea Power", "SeaPower", "Sea_Power" };
            foreach (var name in names)
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0)
                {
                    for (int i = 1; i < procs.Length; i++)
                        procs[i].Dispose();
                    return procs[0];
                }
            }
            return null;
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
