using System;
using System.Drawing;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace Padlume
{
    public partial class TrayFlyoutWindow : Window
    {
        public event EventHandler? OpenAppRequested;
        public event EventHandler? SettingsRequested;
        public event EventHandler? ExitRequested;

        private Rectangle _workingArea;

        public TrayFlyoutWindow()
        {
            InitializeComponent();
        }

        public void UpdateController(string name, string statusText, Color statusColor, BitmapImage? photo, string percentText, double pct, Color batteryColor)
        {
            FlyoutNameText.Text = name;
            FlyoutStatusText.Text = statusText;
            FlyoutStatusText.Foreground = new SolidColorBrush(statusColor);

            if (photo != null)
            {
                FlyoutPhotoImage.Source = photo;
                FlyoutPhotoImage.Visibility = Visibility.Visible;
                FlyoutPhotoFallback.Visibility = Visibility.Collapsed;
            }
            else
            {
                FlyoutPhotoImage.Visibility = Visibility.Collapsed;
                FlyoutPhotoFallback.Visibility = Visibility.Visible;
            }

            var dimColor = ((SolidColorBrush)System.Windows.Application.Current.Resources["DimBrush"]).Color;
            FlyoutPercentText.Text = percentText;
            FlyoutAccentBar.Background = new SolidColorBrush(batteryColor);
            FlyoutBar1.Background = new SolidColorBrush(pct > 0 ? batteryColor : dimColor);
            FlyoutBar2.Background = new SolidColorBrush(pct >= 34 ? batteryColor : dimColor);
            FlyoutBar3.Background = new SolidColorBrush(pct >= 67 ? batteryColor : dimColor);
        }

        public void ShowNear(Rectangle workingArea)
        {
            _workingArea = workingArea;

            // The real height is only known after a layout pass (SizeToContent="Height"); uses an
            // estimate to position without flickering, and Window_SizeChanged corrects it once the real
            // value arrives.
            Left = workingArea.Right - Width - 12;
            Top = workingArea.Bottom - 300 - 12;

            Show();
            Activate();
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_workingArea.IsEmpty)
                return;

            Left = _workingArea.Right - ActualWidth - 12;
            Top = _workingArea.Bottom - ActualHeight - 12;
        }

        private void Window_Deactivated(object sender, EventArgs e) => Hide();

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

        private void OpenApp_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            OpenAppRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
