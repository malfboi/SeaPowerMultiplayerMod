using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using SeapowerMultiplayer.Launcher.Services;

namespace SeapowerMultiplayer.Launcher
{
    public partial class MainWindow : Window
    {
        private readonly ConfigManager _config = new();
        private bool _gameRunning;
        private UpdateInfo? _pendingUpdate;
        private string _lastIP = "0.0.0.0";

        public MainWindow()
        {
            InitializeComponent();
            TxtVersion.Text = $"v{CurrentVersion}";
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _config.Load();
            ApplyConfigToUI();

            // Auto-detect game path if not saved
            if (string.IsNullOrEmpty(_config.Settings.GameDirectory) ||
                !GameDetector.IsValidGameDir(_config.Settings.GameDirectory))
            {
                var detected = GameDetector.AutoDetect();
                if (detected != null)
                {
                    _config.Settings.GameDirectory = detected;
                    _config.Save();
                }
            }

            TxtGamePath.Text = _config.Settings.GameDirectory ?? "";

            // Clean up any proxy files left from a crash
            if (!string.IsNullOrEmpty(_config.Settings.GameDirectory))
                GameLauncher.CleanupProxy(_config.Settings.GameDirectory);

            UpdateInstallStatus();

            // After a self-update, automatically reinstall the mod DLLs
            // so the game gets the new plugin without a manual "Repair" click.
            if (Environment.GetCommandLineArgs().Contains("--post-update"))
                _ = PostUpdateRepairAsync();

            // Check for updates in the background (don't block startup)
            _ = CheckForUpdateAsync();
        }

        private async Task PostUpdateRepairAsync()
        {
            var gameDir = _config.Settings.GameDirectory;
            if (string.IsNullOrEmpty(gameDir) || !GameDetector.IsValidGameDir(gameDir))
                return;

            var progress = new Progress<string>(msg => TxtStatus.Text = msg);
            try
            {
                await Installer.RepairAsync(gameDir, progress);
                TxtStatus.Text = "Update applied — mod reinstalled!";
                UpdateInstallStatus();
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Post-update repair failed: {ex.Message}";
            }
        }

        private async Task CheckForUpdateAsync()
        {
            var update = await UpdateService.CheckForUpdateAsync(CurrentVersion);
            if (update != null)
            {
                _pendingUpdate = update;
                TxtUpdateInfo.Text = $"Update available: v{update.Version}";
                PnlUpdate.Visibility = Visibility.Visible;
            }
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate == null) return;
            SetControlsEnabled(false);
            BtnUpdate.IsEnabled = false;
            var progress = new Progress<string>(msg => TxtStatus.Text = msg);
            try
            {
                await UpdateService.ApplyUpdateAsync(_pendingUpdate, progress);
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Update failed: {ex.Message}";
                SetControlsEnabled(true);
                BtnUpdate.IsEnabled = true;
            }
        }

        private void ApplyConfigToUI()
        {
            bool isSteam = _config.Settings.Transport == "Steam";
            RbSteam.IsChecked = isSteam;
            RbDirectIP.IsChecked = !isSteam;
            PnlDirectIP.Visibility = isSteam ? Visibility.Collapsed : Visibility.Visible;

            RbHost.IsChecked = _config.Settings.IsHost;
            RbClient.IsChecked = !_config.Settings.IsHost;
            TxtHostIP.Text = _config.Settings.HostIP;
            TxtPort.Text = _config.Settings.Port.ToString();
            ChkAutoConnect.IsChecked = _config.Settings.AutoConnect;
            ChkTimeVote.IsChecked = _config.Settings.TimeVote;
            TxtHostIP.IsEnabled = !_config.Settings.IsHost;
        }

        private void SaveUIToConfig()
        {
            _config.Settings.Transport = RbSteam.IsChecked == true ? "Steam" : "LiteNetLib";
            _config.Settings.IsHost = RbHost.IsChecked == true;
            _config.Settings.HostIP = TxtHostIP.Text.Trim();
            if (int.TryParse(TxtPort.Text.Trim(), out int port))
                _config.Settings.Port = port;
            _config.Settings.AutoConnect = ChkAutoConnect.IsChecked == true;
            _config.Settings.TimeVote = ChkTimeVote.IsChecked == true;
            _config.Save();
        }

        private void UpdateInstallStatus()
        {
            var gameDir = TxtGamePath.Text;
            if (string.IsNullOrEmpty(gameDir) || !GameDetector.IsValidGameDir(gameDir))
            {
                StatusDot.Fill = FindResource("ErrorRed") as System.Windows.Media.SolidColorBrush;
                TxtInstallStatus.Text = "Game not found";
                BtnInstall.IsEnabled = false;
                BtnLaunch.IsEnabled = false;
                return;
            }

            BtnInstall.IsEnabled = true;

            bool bepinex = GameDetector.IsBepInExInstalled(gameDir);
            bool mod = GameDetector.IsModInstalled(gameDir);
            bool proxy = GameDetector.IsProxyStored(gameDir);

            if (bepinex && mod && proxy)
            {
                StatusDot.Fill = FindResource("SuccessGreen") as System.Windows.Media.SolidColorBrush;
                TxtInstallStatus.Text = "Installed";
                BtnInstall.Content = "Repair";
                BtnLaunch.IsEnabled = !_gameRunning;
            }
            else if (bepinex && !proxy)
            {
                // BepInEx installed but proxy not stashed — needs repair
                StatusDot.Fill = FindResource("WarningOrange") as System.Windows.Media.SolidColorBrush;
                TxtInstallStatus.Text = "Needs repair (proxy not configured)";
                BtnInstall.Content = "Repair";
                BtnLaunch.IsEnabled = false;
            }
            else
            {
                StatusDot.Fill = FindResource("WarningOrange") as System.Windows.Media.SolidColorBrush;
                TxtInstallStatus.Text = "Not Installed";
                BtnInstall.Content = "Install";
                BtnLaunch.IsEnabled = false;
            }
        }

        private void Transport_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlDirectIP != null)
            {
                if (RbSteam.IsChecked == true)
                {
                    PnlDirectIP.Visibility = Visibility.Collapsed;
                    //This prevents someone from enabling the launch button by flipping between steam and direct networking
                    if (!ValidInstall())
                    {
                        return;
                    }
                    BtnLaunch.IsEnabled = true;
                    TxtStatus.Text = "Ready";
                   
                }
                else
                {
                    //This prevents someone from enabling the launch button by flipping between steam and direct networking. Then checks the network settings to highlight the boxes
                    PnlDirectIP.Visibility = Visibility.Visible;
                    if (!ValidInstall())
                        return;
                    ValidateNetworkSettings();
                }
            }

        }

        private void Role_Changed(object sender, RoutedEventArgs e)
        {
            if (TxtHostIP != null)
            {
                //If the user has changed the IP, and the new one is valid. Remember it if they flip back over to client mode
                if (TxtHostIP.Text != "0.0.0.0" && IsValidIP(TxtHostIP.Text))
                {
                    _lastIP = TxtHostIP.Text;
                }

                TxtHostIP.IsEnabled = RbClient.IsChecked == true;

                //Set the host IP box to show 0.0.0.0 when it's reselected
                if (RbHost.IsChecked == true)
                {
                    TxtHostIP.Text = "0.0.0.0";
                }
                else
                {
                    TxtHostIP.Text = _lastIP;
                }
            }
            ValidateNetworkSettings();

        }
        private bool IsValidIP(string IP)
        {
            //Just return true if it's set to all IPs still
            if (IP == "0.0.0.0") return true;
            //Check if .Net thinks it's a valid IP and it has all 4 octets like it should
            if(System.Net.IPAddress.TryParse(IP, out var Address))
            {
                return IP.Count(c => c == '.') == 3;
            }
            return false;
        }
        //Make sure someone doesn't enter something ridiculous for the port
        private bool IsValidPort(string EnteredPort)
        {
            if (int.TryParse(EnteredPort, out var Port))
            {
                //Ports must be between this range. Really users shouldn't use anything below 1024
                return Port >= 1 && Port <= 65535;
            }
            return false;
        }
        //Update the boarder of the host IP box to indicate that something is invalid to the user
        private void TxtHostIP_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(TxtHostIP.Text) || !IsValidIP(TxtHostIP.Text))
            {
                TxtHostIP.BorderBrush = System.Windows.Media.Brushes.Red;
            }
            else
            {
                TxtHostIP.BorderBrush = System.Windows.Media.Brushes.Green;
            }
            ValidateNetworkSettings();
        }
        //Update the boarder of the Port box to indicate that something is invalid to the user
        private void TxtPort_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (!IsValidPort(TxtPort.Text))
            {
                TxtPort.BorderBrush = System.Windows.Media.Brushes.Red;
            }
            else
            {
                TxtPort.BorderBrush = System.Windows.Media.Brushes.Green;
            }
            ValidateNetworkSettings();
        }
        private void ValidateNetworkSettings()
        {
            if (BtnLaunch == null)
                return;
            if (IsValidIP(TxtHostIP.Text) && IsValidPort(TxtPort.Text))
            {
                BtnLaunch.IsEnabled = true;
                TxtStatus.Text = "Ready";
            }
            else
            {
                BtnLaunch.IsEnabled = false;
                TxtStatus.Text = "Invalid network configuration.";
            }
        }
        //Checks to make sure everything needed for the mod is where it should be
        private bool ValidInstall()
        {
            var gameDir = TxtGamePath.Text;
            bool bepinex = GameDetector.IsBepInExInstalled(gameDir);
            bool mod = GameDetector.IsModInstalled(gameDir);
            bool proxy = GameDetector.IsProxyStored(gameDir);
            if (string.IsNullOrEmpty(gameDir) || !bepinex || !mod || !proxy)
            {
                UpdateInstallStatus();
                return false;
            }
            return true;
        }

        private void BtnDiscord_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("https://discord.gg/rMMnwJHc8w") { UseShellExecute = true });
        }

        private void BtnFeedback_Click(object sender, RoutedEventArgs e)
        {
            var window = new FeedbackWindow(TxtGamePath.Text);
            window.Owner = this;
            window.ShowDialog();
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Sea Power.exe",
                Filter = "Sea Power|Sea Power.exe",
                FileName = "Sea Power.exe",
            };

            if (dialog.ShowDialog() == true)
            {
                var dir = System.IO.Path.GetDirectoryName(dialog.FileName)!;
                TxtGamePath.Text = dir;
                _config.Settings.GameDirectory = dir;
                _config.Save();
                UpdateInstallStatus();
            }
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            var gameDir = TxtGamePath.Text;
            if (string.IsNullOrEmpty(gameDir)) return;

            SetControlsEnabled(false);
            var progress = new Progress<string>(msg =>
                TxtStatus.Text = msg);

            try
            {
                bool alreadyInstalled = GameDetector.IsBepInExInstalled(gameDir)
                                     && GameDetector.IsProxyStored(gameDir);

                if (alreadyInstalled)
                    await Installer.RepairAsync(gameDir, progress);
                else
                    await Installer.InstallAsync(gameDir, progress);

                TxtStatus.Text = "Installation complete!";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Error: {ex.Message}";
                MessageBox.Show(ex.ToString(), "Installation Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetControlsEnabled(true);
                UpdateInstallStatus();
            }
        }

        private const string CurrentVersion = "0.1.2";

        private async void BtnLaunch_Click(object sender, RoutedEventArgs e)
        {
            //Don't let the game launch with an invalid mod install
            var gameDir = TxtGamePath.Text;
            if (!ValidInstall())
                return;
            

            // Show disclaimer once per version
            if (_config.Settings.AcknowledgedVersion != CurrentVersion)
            {
                var disclaimer = new DisclaimerWindow { Owner = this };
                if (disclaimer.ShowDialog() != true)
                    return;

                _config.Settings.AcknowledgedVersion = CurrentVersion;
                _config.Save();
            }

            SaveUIToConfig();

            try
            {
                ConfigManager.WriteBepInExConfig(gameDir, _config.Settings);
                TxtStatus.Text = "Launching game...";
                _gameRunning = true;
                BtnLaunch.IsEnabled = false;

                await GameLauncher.LaunchAsync(gameDir, () =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _gameRunning = false;
                        TxtStatus.Text = "Ready";
                        UpdateInstallStatus();
                    });
                });

                TxtStatus.Text = "Game running...";
            }
            catch (Exception ex)
            {
                _gameRunning = false;
                TxtStatus.Text = $"Launch error: {ex.Message}";
                UpdateInstallStatus();
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            BtnInstall.IsEnabled = enabled;
            BtnLaunch.IsEnabled = enabled;
            BtnBrowse.IsEnabled = enabled;
            RbSteam.IsEnabled = enabled;
            RbDirectIP.IsEnabled = enabled;
            RbHost.IsEnabled = enabled;
            RbClient.IsEnabled = enabled;
            TxtHostIP.IsEnabled = enabled && RbClient.IsChecked == true;
            TxtPort.IsEnabled = enabled;
            ChkAutoConnect.IsEnabled = enabled;
            ChkTimeVote.IsEnabled = enabled;
        }

        
    }
}
