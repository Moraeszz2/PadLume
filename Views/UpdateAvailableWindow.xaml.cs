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

        /// <summary>Shows the progress bar at 0% and disables both buttons — used once the download
        /// starts, so the user can't double-click "Update now" mid-download or dismiss out from under
        /// it.</summary>
        public void SetBusy(string statusText)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            StatusText.Text = statusText;
            ProgressPanel.Visibility = Visibility.Visible;
            SetProgress(0);
            UpdateButton.IsEnabled = false;
            LaterButton.IsEnabled = false;
        }

        /// <summary>Updates the progress bar fill and percentage label — percent is 0-100. Computes the
        /// fill width from the track's own measured width rather than binding, since a plain Border pair
        /// is simpler to keep visually consistent with the rest of the app than retemplating
        /// ProgressBar.</summary>
        public void SetProgress(double percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            ProgressPercentText.Text = $"{percent:0}%";
            ProgressFill.Width = ProgressTrack.ActualWidth * (percent / 100.0);
        }

        /// <summary>Re-enables the buttons so the user can retry or give up — used when the download/verify/launch step failed.</summary>
        public void SetError(string message)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
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
