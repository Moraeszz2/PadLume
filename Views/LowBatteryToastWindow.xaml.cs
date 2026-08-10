using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Padlume
{
    /// <summary>
    /// Low battery warning: a banner in the app's own palette that slides down from the top, sits at
    /// top-center for a while, and disappears on its own. Replaces NotifyIcon's balloon because that
    /// depends on Windows' Notification Center being turned on (not the case on every PC) — a plain WPF
    /// window has no such dependency.
    /// </summary>
    public partial class LowBatteryToastWindow : Window
    {
        private DispatcherTimer? _dismissTimer;

        public LowBatteryToastWindow()
        {
            InitializeComponent();
        }

        public void ShowWarning(double percent, TimeSpan duration)
        {
            ToastText.Text = $"{percent:0}%";

            Opacity = 0;
            Show();
            UpdateLayout();

            var workingArea = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1040);
            Left = workingArea.Left + (workingArea.Width - ActualWidth) / 2;
            Top = workingArea.Top + 24;

            SlideTransform.Y = -30;
            SlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(-30, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));

            _dismissTimer?.Stop();
            _dismissTimer = new DispatcherTimer { Interval = duration };
            _dismissTimer.Tick += (_, _) =>
            {
                _dismissTimer!.Stop();
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
                fadeOut.Completed += (_, _) => Hide();
                BeginAnimation(OpacityProperty, fadeOut);
            };
            _dismissTimer.Start();
        }
    }
}
