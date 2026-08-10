using System;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace Padlume
{
    /// <summary>
    /// Tray icon: clicking shows a custom flyout with the selected controller's state and shortcuts
    /// (open, settings, exit), plus the channel used for the "low battery" balloon.
    /// </summary>
    public sealed class TrayNotifier : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly System.Drawing.Icon? _icon;
        private readonly TrayFlyoutWindow _flyout = new();
        private readonly LowBatteryToastWindow _lowBatteryToast = new();

        public event EventHandler? OpenAppRequested;
        public event EventHandler? SettingsRequested;
        public event EventHandler? ExitRequested;

        public TrayNotifier()
        {
            // Reuses the app's own icon (embedded via pack resource) instead of generating a separate
            // bitmap: avoids dealing with GDI handles (Icon.FromHandle requires a manual DestroyIcon,
            // easy to forget and leaks a native handle per run) and keeps the branding consistent.
            _icon = LoadAppIcon();
            _notifyIcon = new NotifyIcon
            {
                Icon = _icon ?? System.Drawing.SystemIcons.Application,
                Text = "Padlume",
                Visible = true,
            };
            _notifyIcon.MouseClick += NotifyIcon_MouseClick;

            _flyout.OpenAppRequested += (_, _) => OpenAppRequested?.Invoke(this, EventArgs.Empty);
            _flyout.SettingsRequested += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
            _flyout.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateController(string name, string statusText, Color statusColor, BitmapImage? photo, string percentText, double pct, Color batteryColor) =>
            _flyout.UpdateController(name, statusText, statusColor, photo, percentText, pct, batteryColor);

        public void ShowLowBatteryWarning(string controllerName, double percent, TimeSpan duration)
        {
            // Custom banner (WPF window) instead of NotifyIcon's balloon: the balloon depends on
            // Windows' Notification Center being turned on (Settings > System > Notifications), which
            // has been seen turned off on real machines — a plain window always shows regardless of that setting.
            _lowBatteryToast.ShowWarning(percent, duration);
        }

        private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
        {
            // NotifyIcon.MouseClick is a WinForms event, not WPF — an exception here does NOT go
            // through Application.DispatcherUnhandledException (which only covers WPF's UI thread), and
            // since this app doesn't run a WinForms message loop (no Application.Run), there's also no
            // Application.ThreadException to catch it. Without this try/catch, any error here could
            // bring down the entire process without leaving a trace in any log.
            try
            {
                if (_flyout.IsVisible)
                {
                    _flyout.Hide();
                    return;
                }

                var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1040);
                _flyout.ShowNear(workingArea);
            }
            catch (Exception ex)
            {
                try
                {
                    var dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Padlume");
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(dir, "crash.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] (TrayIconClick) {ex}\n\n");
                }
                catch
                {
                    // Nothing more to do if even the log can't be written.
                }
            }
        }

        private static System.Drawing.Icon? LoadAppIcon()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/app.ico");
                var resource = System.Windows.Application.GetResourceStream(uri);
                if (resource == null)
                    return null;

                using var stream = resource.Stream;
                return new System.Drawing.Icon(stream);
            }
            catch (Exception ex)
            {
                // Missing resource (unusual build, missing file, etc.): falls back to the system default icon.
                App.Log("TrayNotifier", $"LoadAppIcon() failed: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            _flyout.Close();
            _lowBatteryToast.Close();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _icon?.Dispose();
        }
    }
}
