using System.Windows;

namespace SeapowerMultiplayer.Launcher
{
    public partial class DisclaimerWindow : Window
    {
        public DisclaimerWindow()
        {
            InitializeComponent();
        }

        private void ChkAcknowledge_Changed(object sender, RoutedEventArgs e)
        {
            BtnAccept.IsEnabled = ChkAcknowledge.IsChecked == true;
        }

        private void BtnAccept_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
