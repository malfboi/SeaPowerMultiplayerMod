using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;

namespace SeapowerMultiplayer.Launcher.Services
{
    public static class Installer
    {
        private const string ProxyDir = "BepInEx\\proxy";
        private const string PluginsDir = "BepInEx\\plugins";
        private const string LogFile = "BepInEx\\LogOutput.log";
        private const int InitTimeoutMs = 90_000;

        public static async Task InstallAsync(string gameDir, IProgress<string> progress)
        {
            // Step 1: Extract BepInEx
            progress.Report("Extracting BepInEx...");
            ExtractBepInEx(gameDir);

            // Step 2: Stash proxy files
            progress.Report("Configuring proxy...");
            StashProxy(gameDir);

            // Step 3: Initialize BepInEx (brief game launch)
            progress.Report("Initializing BepInEx (game will open briefly)...");
            await InitializeBepInEx(gameDir, progress);

            // Step 4: Install mod DLLs
            progress.Report("Installing multiplayer mod...");
            InstallModDlls(gameDir);

            progress.Report("Installation complete!");
        }

        public static async Task RepairAsync(string gameDir, IProgress<string> progress)
        {
            // Re-stash proxy if needed
            progress.Report("Checking proxy configuration...");
            var proxyPath = Path.Combine(gameDir, ProxyDir);
            if (!File.Exists(Path.Combine(proxyPath, "winhttp.dll")))
            {
                // If proxy DLL is in game root, stash it
                if (File.Exists(Path.Combine(gameDir, "winhttp.dll")))
                    StashProxy(gameDir);
                else
                {
                    // Need to re-extract BepInEx to get proxy files
                    progress.Report("Re-extracting BepInEx...");
                    ExtractBepInEx(gameDir);
                    StashProxy(gameDir);
                }
            }

            // Re-install mod DLLs
            progress.Report("Updating multiplayer mod...");
            InstallModDlls(gameDir);

            progress.Report("Repair complete!");
            await Task.CompletedTask;
        }

        private static void ExtractBepInEx(string gameDir)
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("BepInEx.zip")
                ?? throw new InvalidOperationException(
                    "BepInEx.zip not found as embedded resource. " +
                    "Place BepInEx_x64_5.4.23.2.zip in the Resources/ folder and rebuild.");

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

                var destPath = Path.Combine(gameDir, entry.FullName);
                var destDir = Path.GetDirectoryName(destPath)!;
                Directory.CreateDirectory(destDir);

                entry.ExtractToFile(destPath, overwrite: true);
            }
        }

        private static void StashProxy(string gameDir)
        {
            var proxyPath = Path.Combine(gameDir, ProxyDir);
            Directory.CreateDirectory(proxyPath);

            MoveIfExists(
                Path.Combine(gameDir, "winhttp.dll"),
                Path.Combine(proxyPath, "winhttp.dll"));
            MoveIfExists(
                Path.Combine(gameDir, "doorstop_config.ini"),
                Path.Combine(proxyPath, "doorstop_config.ini"));
        }

        private static async Task InitializeBepInEx(string gameDir, IProgress<string> progress)
        {
            var proxyDir = Path.Combine(gameDir, ProxyDir);

            // Temporarily place proxy in game root
            File.Copy(Path.Combine(proxyDir, "winhttp.dll"),
                       Path.Combine(gameDir, "winhttp.dll"), overwrite: true);
            File.Copy(Path.Combine(proxyDir, "doorstop_config.ini"),
                       Path.Combine(gameDir, "doorstop_config.ini"), overwrite: true);

            // Delete old log so we can detect fresh init
            var logPath = Path.Combine(gameDir, LogFile);
            if (File.Exists(logPath))
                File.Delete(logPath);

            Process? proc = null;
            try
            {
                proc = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(gameDir, "Sea Power.exe"),
                    WorkingDirectory = gameDir,
                    UseShellExecute = true,
                });

                if (proc == null)
                    throw new InvalidOperationException("Failed to start Sea Power.exe");

                // Wait for BepInEx to initialize.
                // Steam may restart the game (original process exits, new one spawns),
                // so we monitor the log file rather than relying on the process handle.
                var sw = Stopwatch.StartNew();
                bool initialized = false;
                while (sw.ElapsedMilliseconds < InitTimeoutMs)
                {
                    await Task.Delay(1000);

                    if (File.Exists(logPath))
                    {
                        try
                        {
                            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var sr = new StreamReader(fs);
                            var logContent = sr.ReadToEnd();
                            if (logContent.Contains("Chainloader startup complete"))
                            {
                                progress.Report("BepInEx initialized successfully. Closing game...");
                                await Task.Delay(2000);
                                initialized = true;
                                break;
                            }
                        }
                        catch (IOException)
                        {
                            // File might be locked — retry next iteration
                        }
                    }
                }

                if (!initialized)
                    progress.Report("BepInEx initialization timed out. Closing game...");
            }
            finally
            {
                proc?.Dispose();

                // Kill all Sea Power processes (original + any Steam-relaunched ones)
                await KillAllGameProcesses();

                // Remove proxy from game root
                TryDelete(Path.Combine(gameDir, "winhttp.dll"));
                TryDelete(Path.Combine(gameDir, "doorstop_config.ini"));
            }
        }

        private static void InstallModDlls(string gameDir)
        {
            var pluginsPath = Path.Combine(gameDir, PluginsDir);
            Directory.CreateDirectory(pluginsPath);

            ExtractEmbeddedResource("SeapowerMultiplayer.dll",
                Path.Combine(pluginsPath, "SeapowerMultiplayer.dll"));
            ExtractEmbeddedResource("LiteNetLib.dll",
                Path.Combine(pluginsPath, "LiteNetLib.dll"));
        }

        private static void ExtractEmbeddedResource(string resourceName, string destPath)
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{resourceName}' not found. Build the mod project first.");

            using var fs = File.Create(destPath);
            stream.CopyTo(fs);
        }

        private static void MoveIfExists(string source, string dest)
        {
            if (!File.Exists(source)) return;
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(source, dest);
        }

        private static async Task KillAllGameProcesses()
        {
            // Try multiple possible process names
            string[] names = { "Sea Power", "SeaPower", "Sea_Power" };
            foreach (var name in names)
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(); p.WaitForExit(5000); } catch { }
                    p.Dispose();
                }
            }

            // Also find by window title as a fallback
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowTitle.Contains("Sea Power", StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }

            // Give OS time to fully release
            await Task.Delay(1000);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
