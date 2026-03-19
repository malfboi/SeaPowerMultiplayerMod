using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SeapowerMultiplayer.Launcher.Services
{
    public static class FeedbackService
    {
        private const string WorkerUrl = "https://seapower-feedback.seapower-multiplayer.workers.dev";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

        public static async Task SubmitAsync(string category, string description, string? gameDir)
        {
            // Read log file for bug reports only
            string? logBase64 = null;
            if (category == "Bug Report" && !string.IsNullOrEmpty(gameDir))
            {
                var logPath = Path.Combine(gameDir, "BepInEx", "LogOutput.log");
                if (File.Exists(logPath))
                {
                    var logBytes = await Task.Run(() =>
                    {
                        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var ms = new MemoryStream();
                        fs.CopyTo(ms);
                        return ms.ToArray();
                    });
                    logBase64 = Convert.ToBase64String(logBytes);
                }
            }

            var payload = JsonSerializer.Serialize(new
            {
                category,
                description,
                log = logBase64,
            });

            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await Http.PostAsync(WorkerUrl, content);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Feedback submission failed ({(int)response.StatusCode}): {body}");
            }
        }
    }
}
