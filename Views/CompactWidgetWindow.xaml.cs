using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace Padlume
{
    /// <summary>
    /// Small always-on-top window with just the essentials (photo, name, battery) of the selected
    /// controller — keeps it visible in a corner of the screen while gaming, without needing the main
    /// window open. Draggable (click and drag anywhere on the card); the position only lasts the
    /// current session, it isn't saved to disk.
    /// </summary>
    public partial class CompactWidgetWindow : Window
    {
        public event EventHandler? RestoreRequested;

        public CompactWidgetWindow()
        {
            InitializeComponent();
        }

        private bool _hasBeenPositioned;

        /// <summary>Positions the window at the bottom-right corner the first time it's shown this session — subsequent reopens (hide/show again) preserve wherever the user dragged it to.</summary>
        public void ShowAtDefaultPositionIfFirstShow()
        {
            Show();

            if (_hasBeenPositioned)
                return;

            _hasBeenPositioned = true;
            UpdateLayout();

            var workingArea = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1040);
            Left = workingArea.Right - ActualWidth - 20;
            Top = workingArea.Bottom - ActualHeight - 20;
        }

        public void UpdateController(string name, string percentText, double pct, Color batteryColor, BitmapImage? photo)
        {
            NameText.Text = name;
            PercentText.Text = percentText;

            if (photo != null)
            {
                PhotoImage.Source = photo;
                PhotoImage.Visibility = Visibility.Visible;
                PhotoFallback.Visibility = Visibility.Collapsed;
            }
            else
            {
                PhotoImage.Visibility = Visibility.Collapsed;
                PhotoFallback.Visibility = Visibility.Visible;
            }

            var dimColor = ((SolidColorBrush)System.Windows.Application.Current.Resources["DimBrush"]).Color;
            var color = pct > 0 ? batteryColor : dimColor;
            BatteryOutline.BorderBrush = new SolidColorBrush(color);
            BatteryNub.Background = new SolidColorBrush(color);
            BatteryFill.Background = new SolidColorBrush(color);
            BatteryFill.Width = Math.Clamp(pct / 100.0, 0, 1) * 15;
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

        private void RestoreButton_Click(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke(this, EventArgs.Empty);
    }
}
