using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using SeapowerMultiplayer.Launcher.Services;

namespace SeapowerMultiplayer.Launcher
{
    public partial class FeedbackWindow : Window
    {
        private readonly string? _gameDir;

        public FeedbackWindow(string? gameDir)
        {
            InitializeComponent();
            _gameDir = gameDir;

            UpdateLogStatus();
        }

        private void TxtDescription_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            BtnSubmit.IsEnabled = !string.IsNullOrWhiteSpace(TxtDescription.Text);
        }

        private async void BtnSubmit_Click(object sender, RoutedEventArgs e)
        {
            var category = ((System.Windows.Controls.ComboBoxItem)CmbCategory.SelectedItem).Content.ToString()!;
            var description = TxtDescription.Text.Trim();

            BtnSubmit.Content = "Submitting...";
            BtnSubmit.IsEnabled = false;
            BtnCancel.IsEnabled = false;

            try
            {
                await FeedbackService.SubmitAsync(category, description, _gameDir);
                MessageBox.Show("Feedback submitted. Thank you!", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            catch (HttpRequestException ex)
            {
                MessageBox.Show($"Could not reach feedback server. Check your internet connection.\n\n{ex.Message}",
                    "Submission Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (TaskCanceledException)
            {
                MessageBox.Show("Request timed out. Check your internet connection.",
                    "Submission Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unexpected error: {ex.Message}",
                    "Submission Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnSubmit.Content = "Submit";
                BtnSubmit.IsEnabled = !string.IsNullOrWhiteSpace(TxtDescription.Text);
                BtnCancel.IsEnabled = true;
            }
        }

        private void CmbCategory_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateLogStatus();
        }

        private void UpdateLogStatus()
        {
            if (TxtLogStatus == null) return;

            var selected = CmbCategory.SelectedIndex == 0; // Bug Report
            if (selected && !string.IsNullOrEmpty(_gameDir))
            {
                var logPath = Path.Combine(_gameDir, "BepInEx", "LogOutput.log");
                TxtLogStatus.Text = File.Exists(logPath)
                    ? "BepInEx log will be attached"
                    : "No log file found";
            }
            else
            {
                TxtLogStatus.Text = "";
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
