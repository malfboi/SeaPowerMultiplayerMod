using System;
using System.IO;
using System.Text.Json;

namespace SeapowerMultiplayer.Launcher.Services
{
    public class LauncherSettings
    {
        public string? GameDirectory { get; set; }
        public string Transport { get; set; } = "Steam";
        public bool IsHost { get; set; } = true;
        public string HostIP { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 7777;
        public bool AutoConnect { get; set; } = false;
        public bool TimeVote { get; set; } = false;
        public string? AcknowledgedVersion { get; set; }
    }

    public class ConfigManager
    {
        private static readonly string ConfigDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "SeapowerMultiplayer");

        private static readonly string ConfigPath =
            Path.Combine(ConfigDir, "launcher.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        public LauncherSettings Settings { get; private set; } = new();

        public void Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    Settings = JsonSerializer.Deserialize<LauncherSettings>(json, JsonOptions)
                               ?? new LauncherSettings();
                }
            }
            catch
            {
                Settings = new LauncherSettings();
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }

        /// <summary>
        /// Write the BepInEx config file that the mod reads on startup.
        /// Uses the exact INI format BepInEx expects.
        /// </summary>
        public static void WriteBepInExConfig(string gameDir, LauncherSettings settings)
        {
            var configDir = Path.Combine(gameDir, "BepInEx", "config");
            Directory.CreateDirectory(configDir);

            var configPath = Path.Combine(configDir, "com.seapowermultiplayer.plugin.cfg");
            var content = $"""
                [Network]

                ## Network transport: LiteNetLib (direct IP) or Steam (P2P with invites)
                # Setting type: String
                # Default value: LiteNetLib
                Transport = {settings.Transport}

                ## Are you the host?
                # Setting type: Boolean
                # Default value: true
                IsHost = {settings.IsHost.ToString().ToLower()}

                ## Host IP address to connect to (client only)
                # Setting type: String
                # Default value: 127.0.0.1
                HostIP = {settings.HostIP}

                ## Network port
                # Setting type: Int32
                # Default value: 7777
                Port = {settings.Port}

                ## Automatically connect on game start
                # Setting type: Boolean
                # Default value: false
                AutoConnect = {settings.AutoConnect.ToString().ToLower()}

                ## Time vote mode: both players must agree on time compression changes
                # Setting type: Boolean
                # Default value: false
                TimeVote = {settings.TimeVote.ToString().ToLower()}

                """;

            File.WriteAllText(configPath, content);
        }
    }
}
