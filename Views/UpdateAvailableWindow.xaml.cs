using System;
using System.Windows;

namespace Padlume
{
    public partial class UpdateAvailableWindow : Window
    {
        public event EventHandler? UpdateRequested;
        public event EventHandler? DismissRequested;

        public UpdateAvailableWindow(string version)
        {
            InitializeComponent();
            TitleText.Text = Strings.UpdateAvailableTitle(version);
        }

        /// <summary>Disables both buttons and shows a status line — used while downloading/verifying/launching, so the user can't double-click "Update now" mid-download or dismiss out from under it.</summary>
        public void SetBusy(string statusText)
        {
            StatusText.Text = statusText;
            StatusText.Foreground = System.Windows.Media.Brushes.SkyBlue;
            StatusText.Visibility = Visibility.Visible;
            UpdateButton.IsEnabled = false;
            LaterButton.IsEnabled = false;
        }

        /// <summary>Re-enables the buttons so the user can retry or give up — used when the download/verify/launch step failed.</summary>
        public void SetError(string message)
        {
            StatusText.Text = message;
            StatusText.Foreground = System.Windows.Media.Brushes.IndianRed;
            StatusText.Visibility = Visibility.Visible;
            UpdateButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }

        private void UpdateButton_Click(object sender, RoutedEventArgs e) => UpdateRequested?.Invoke(this, EventArgs.Empty);

        private void LaterButton_Click(object sender, RoutedEventArgs e)
        {
            DismissRequested?.Invoke(this, EventArgs.Empty);
            Close();
        }
    }
}
