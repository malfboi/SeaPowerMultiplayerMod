using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SeapowerMultiplayer.Launcher.Services
{
    public class UpdateInfo
    {
        public string Version { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
    }

    public static class UpdateService
    {
        private const string RepoOwner = "malfboi";
        private const string RepoName = "SeaPowerMultiplayerMod";
        private const string AssetName = "SeapowerMultiplayerLauncher.exe";

        private static readonly HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestHeaders =
            {
                { "User-Agent", "SeapowerMultiplayerLauncher" },
                { "Accept", "application/vnd.github+json" },
            },
        };

        /// <summary>
        /// Check GitHub Releases for a newer version.
        /// Returns null if up-to-date or on any error (fail silently).
        /// </summary>
        public static async Task<UpdateInfo?> CheckForUpdateAsync(string currentVersion)
        {
            try
            {
                var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Parse version from tag_name first, fall back to release name
                // (handles tags like "Beta" where the name is "v0.1.0 Beta Release")
                var tagName = root.GetProperty("tag_name").GetString() ?? "";
                var releaseName = root.TryGetProperty("name", out var nameEl)
                    ? nameEl.GetString() ?? "" : "";

                var versionStr = ExtractVersion(tagName) ?? ExtractVersion(releaseName);
                if (versionStr == null || !Version.TryParse(versionStr, out var remoteVersion))
                    return null;
                if (!Version.TryParse(currentVersion, out var localVersion))
                    return null;
                if (remoteVersion <= localVersion)
                    return null;

                // Find the launcher exe asset
                string? downloadUrl = null;
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString() ?? "";
                        if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString();
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(downloadUrl))
                    return null;

                var releaseNotes = root.TryGetProperty("body", out var body)
                    ? body.GetString() ?? ""
                    : "";

                return new UpdateInfo
                {
                    Version = versionStr,
                    DownloadUrl = downloadUrl,
                    ReleaseNotes = releaseNotes,
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extract a version string (e.g. "0.1.0") from text like "v0.1.0", "v0.1.0 Beta Release", etc.
        /// </summary>
        private static string? ExtractVersion(string text)
        {
            var match = Regex.Match(text, @"(\d+\.\d+\.\d+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Download the update and replace the running exe via a batch script.
        /// This method shuts down the application.
        /// </summary>
        public static async Task ApplyUpdateAsync(UpdateInfo update, IProgress<string> progress)
        {
            var tempDir = Path.GetTempPath();
            var tempExe = Path.Combine(tempDir, "SeapowerMultiplayerLauncher_update.exe");
            var tempBat = Path.Combine(tempDir, "spm_update.bat");
            var currentExe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine current exe path");

            // Download
            progress.Report($"Downloading v{update.Version}...");
            using (var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? -1;

                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long downloaded = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    downloaded += bytesRead;

                    if (totalBytes > 0)
                    {
                        var pct = (int)(downloaded * 100 / totalBytes);
                        progress.Report($"Downloading v{update.Version}... {pct}%");
                    }
                }
            }

            progress.Report("Applying update...");

            // Write batch script that waits for this process to exit, then replaces the exe
            var script = $"""
                @echo off
                timeout /t 2 /nobreak >nul
                move /y "{tempExe}" "{currentExe}"
                start "" "{currentExe}" --post-update
                del "%~f0"
                """;
            await File.WriteAllTextAsync(tempBat, script);

            // Launch the batch script (hidden window)
            Process.Start(new ProcessStartInfo
            {
                FileName = tempBat,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            // Shut down the launcher so the batch script can replace the exe
            System.Windows.Application.Current.Shutdown();
        }
    }
}
