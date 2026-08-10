using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Devices.Enumeration;
using Windows.Devices.Power;
using Windows.Gaming.Input;
using Windows.System.Power;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace Padlume
{
    /// <summary>
    /// Represents a controller detected by Windows (Windows.Gaming.Input.RawGameController),
    /// which covers controllers connected via Bluetooth, USB, or a wireless dongle.
    /// </summary>
    public class ControllerItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public RawGameController Controller { get; private set; }
        public string DisplayName { get; }

        /// <summary>Controller photo (Assets/*.png) picked by brand/model, or null if none exists.</summary>
        public BitmapImage? PhotoSource { get; }

        /// <summary>
        /// Windows device instance ID (SetupAPI) matching this controller, used to enable/disable it
        /// via <see cref="ControllerDeviceLock"/>. Null while not yet resolved, or if it couldn't be
        /// correlated (see ResolveDeviceInstanceId).
        /// </summary>
        public string? DeviceInstanceId { get; set; }

        private bool _isBlocked;
        /// <summary>True when Padlume disabled this controller to give priority to another one.</summary>
        public bool IsBlocked
        {
            get => _isBlocked;
            private set
            {
                if (_isBlocked == value) return;
                _isBlocked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBlocked)));
            }
        }

        public void SetBlocked(bool blocked) => IsBlocked = blocked;

        /// <summary>
        /// Called when the same physical controller reconnects with a new RawGameController instance
        /// (e.g. after being re-enabled via SetupAPI) — keeps the same ControllerItem (history, photo,
        /// DeviceInstanceId) instead of duplicating the entry in the list.
        /// </summary>
        public void UpdateControllerReference(RawGameController controller) => Controller = controller;

        /// <summary>Stable key used to reconcile this item across list refreshes (see HistoryKey).</summary>
        public static string KeyFor(RawGameController controller)
        {
            var displayName = string.IsNullOrWhiteSpace(controller.DisplayName)
                ? $"Controller (VID {controller.HardwareVendorId:X4} / PID {controller.HardwareProductId:X4})"
                : controller.DisplayName;
            return BatteryHistoryStore.KeyFor(displayName, controller.HardwareVendorId, controller.HardwareProductId);
        }

        private string _statusLine = Strings.Connected;
        public string StatusLine
        {
            get => _statusLine;
            private set
            {
                if (_statusLine == value) return;
                _statusLine = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLine)));
            }
        }

        /// <summary>"Bluetooth", "USB", or null while not yet determined.</summary>
        private string? _connectionType;
        public string? ConnectionType
        {
            get => _connectionType;
            private set
            {
                if (_connectionType == value) return;
                _connectionType = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionType)));
            }
        }

        /// <summary>
        /// Battery percentage already resolved — via RawGameController, via the background Bluetooth
        /// query (EnrichListItemAsync), or via a proprietary HID reader (DS4/DualSense/Xbox, see
        /// UpdateBatteryDisplay). Null while no data is available at all.
        /// </summary>
        private double? _batteryPercent;
        public double? BatteryPercent
        {
            get => _batteryPercent;
            private set
            {
                if (_batteryPercent == value) return;
                _batteryPercent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BatteryPercent)));
            }
        }

        // Cached at construction time (when Controller is guaranteed to be a "live" reference) so that
        // HistoryKey never needs to re-read properties off a potentially stale Controller — see
        // UpdateControllerReference.
        private readonly ushort _vendorId;
        private readonly ushort _productId;

        public ControllerItem(RawGameController controller)
        {
            Controller = controller;
            _vendorId = controller.HardwareVendorId;
            _productId = controller.HardwareProductId;
            DisplayName = string.IsNullOrWhiteSpace(controller.DisplayName)
                ? $"Controller (VID {controller.HardwareVendorId:X4} / PID {controller.HardwareProductId:X4})"
                : controller.DisplayName;
            PhotoSource = ControllerImageResolver.Resolve(DisplayName, controller.HardwareVendorId, controller.HardwareProductId);

            if (TryGetBatteryPercent(controller, out var pct))
            {
                _statusLine = Strings.ConnectedPercent(pct);
                _batteryPercent = pct;
            }
        }

        public void SetBatteryPercent(double pct)
        {
            StatusLine = Strings.ConnectedPercent(pct);
            BatteryPercent = pct;
        }

        public void SetConnectionType(bool isBluetooth) => ConnectionType = isBluetooth ? "Bluetooth" : "USB";

        /// <summary>
        /// True for controllers with a proprietary battery reader (DS4/DualSense) — for those, the
        /// value Windows caches for the paired Bluetooth device is known to be unreliable and must not
        /// feed the history/display over the direct HID report reading.
        /// </summary>
        public bool HasProprietaryBatteryReader =>
            DualShock4BatteryReader.IsDualShock4(_vendorId, _productId) ||
            DualSenseBatteryReader.IsDualSense(_vendorId, _productId);

        /// <summary>Stable-enough key used to group this controller's battery history.</summary>
        public string HistoryKey => BatteryHistoryStore.KeyFor(DisplayName, _vendorId, _productId);

        private static bool TryGetBatteryPercent(RawGameController controller, out double pct)
        {
            pct = 0;
            try
            {
                var report = controller.TryGetBatteryReport();
                if (report?.FullChargeCapacityInMilliwattHours is int full && full > 0 &&
                    report.RemainingCapacityInMilliwattHours is int remaining)
                {
                    pct = Math.Clamp(100.0 * remaining / full, 0, 100);
                    return true;
                }
            }
            catch (Exception ex)
            {
                // Some controllers throw when querying battery; treat that as unavailable.
                App.Log("ControllerItem", $"TryGetBatteryPercent({controller.DisplayName}) failed: {ex.Message}");
            }
            return false;
        }

        public override string ToString() => DisplayName;
    }

    public partial class MainWindow : Window
    {
        // DWMWA_USE_IMMERSIVE_DARK_MODE: asks Windows itself to draw the title bar (and the
        // min/max/close buttons) in the native dark theme, instead of the default white bar — no need
        // to rebuild the whole title bar by hand (a custom WindowChrome), which would risk breaking
        // drag/resize/maximize just to save a cosmetic effect.
        private const int DwmwaUseImmersiveDarkMode = 20;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

        private void ApplyDarkTitleBar()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int useDarkMode = 1;
            int hr = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int));
            if (hr != 0)
            {
                // Purely cosmetic (the title bar just stays the default light color) — worth a log
                // entry to explain a "wrong" title bar if it's ever reported, but not worth failing over.
                App.Log("ApplyDarkTitleBar", $"DwmSetWindowAttribute failed with HRESULT 0x{hr:X8}.");
            }
        }

        private readonly ObservableCollection<ControllerItem> _controllers = new();
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _countdownTimer;
        private TimeSpan? _displayedRemaining;
        private readonly TrayNotifier _trayNotifier = new();
        private readonly HashSet<string> _lowBatteryNotified = new();
        private readonly RemoteControlServer _remoteServer = new();
        private const int RemoteControlPort = 51820;

        /// <summary>
        /// Device instance IDs that Padlume itself disabled (to give priority to another controller) —
        /// used to (1) avoid treating the resulting RawGameControllerRemoved as a real disconnection and
        /// (2) re-enable everything when the app actually exits.
        /// </summary>
        private readonly HashSet<string> _selfDisabledInstanceIds = new();
        private IReadOnlyList<BatteryHistoryPoint> _currentHistory = Array.Empty<BatteryHistoryPoint>();
        private bool _suppressStartupCheckboxEvent;
        private bool _suppressThemeRadioEvent;
        private bool _isExiting;
        private CompactWidgetWindow? _compactWidget;

        private const double LowBatteryThreshold = 20.0;
        private const double LowBatteryResetThreshold = 30.0; // hysteresis: only re-arms the warning after rising well above the threshold
        private static readonly TimeSpan LowBatteryToastDuration = TimeSpan.FromSeconds(7);

        public MainWindow()
        {
            InitializeComponent();
            SourceInitialized += (_, _) => ApplyDarkTitleBar();
            ControllerListBox.ItemsSource = _controllers;

            _suppressStartupCheckboxEvent = true;
            StartWithWindowsCheckBox.IsChecked = StartupManager.IsEnabled();
            _suppressStartupCheckboxEvent = false;

            _suppressThemeRadioEvent = true;
            switch (ThemeManager.CurrentPreference)
            {
                case ThemePreference.Light:
                    ThemeLightRadio.IsChecked = true;
                    break;
                case ThemePreference.Dark:
                    ThemeDarkRadio.IsChecked = true;
                    break;
                default:
                    ThemeAutoRadio.IsChecked = true;
                    break;
            }
            _suppressThemeRadioEvent = false;

            _remoteServer.GetControllers = GetRemoteControllerSnapshot;
            _remoteServer.SelectController = SelectControllerByKeyFromRemote;

            RawGameController.RawGameControllerAdded += (_, _) => Dispatcher.Invoke(RefreshControllerList);
            RawGameController.RawGameControllerRemoved += (_, _) => Dispatcher.Invoke(RefreshControllerList);

            RecoverDevicesDisabledByPreviousSession();
            RefreshControllerList();

            // Periodically refreshes the selected controller's battery reading. Only re-reads what's
            // already available locally (RawGameController + item cache) — never triggers a new
            // Bluetooth query, so there's no race and no need for a cooldown here.
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += (_, _) => UpdateBatteryDisplay();
            _timer.Start();

            // "Time remaining" text and the chart's tip — neither waits for the next real battery
            // sample (which only arrives every 3s, or up to every 5min depending on the history). The
            // time remaining counts down on its own and the chart stretches to "now" every second; the
            // underlying data (usage rate, saved samples) still comes from the history as usual.
            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdownTimer.Tick += (_, _) =>
            {
                TickCountdown();
                if (_currentHistory.Count >= 2)
                    DrawHistoryChart(_currentHistory);
            };
            _countdownTimer.Start();

            ((Storyboard)Resources["BluetoothPulseStoryboard"]).Begin(this, true);

            _trayNotifier.OpenAppRequested += (_, _) => RestoreFromTray();
            _trayNotifier.SettingsRequested += (_, _) => RestoreFromTray(scrollToSettings: true);
            _trayNotifier.ExitRequested += (_, _) => ExitApplication();

            // Closing via the X button only hides the window (the app keeps running in the tray); only
            // "Exit" in the tray popup actually shuts it down — see ExitApplication().
            Closing += MainWindow_Closing;
            Closed += (_, _) => _trayNotifier.Dispose();

            _ = CheckForUpdatesAsync();
        }

        private async Task CheckForUpdatesAsync()
        {
            var update = await UpdateChecker.CheckForUpdateAsync();
            if (update == null)
                return;

            var window = new UpdateAvailableWindow(update.Version);
            window.UpdateRequested += (_, _) => _ = PerformUpdateAsync(window, update);
            window.Show();
        }

        private async Task PerformUpdateAsync(UpdateAvailableWindow window, UpdateInfo update)
        {
            window.SetBusy(Strings.Downloading);

            var progress = new Progress<double>(window.SetProgress);
            var (result, setupPath) = await UpdateChecker.DownloadAndVerifyAsync(update, progress);
            if (result == UpdateDownloadResult.ChecksumMismatch)
            {
                window.SetError(Strings.UpdateChecksumMismatch);
                return;
            }
            if (result != UpdateDownloadResult.Success || setupPath == null)
            {
                window.SetError(Strings.UpdateFailed);
                return;
            }

            if (!UpdateChecker.LaunchInstallerSilently(setupPath))
            {
                window.SetError(Strings.UpdateFailed);
                return;
            }

            // The installer's [Run] postinstall step relaunches Padlume on its own once it's done (see
            // installer/Padlume.iss) — same clean-shutdown path as the tray "Exit", so controllers this
            // session disabled for exclusivity get re-enabled instead of staying locked out.
            window.Close();
            ExitApplication();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isExiting)
                return;

            e.Cancel = true;
            Hide();
        }

        private void RestoreFromTray(bool scrollToSettings = false)
        {
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;

            if (scrollToSettings)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RootScrollViewer.ScrollToEnd());
        }

        private void ExitApplication()
        {
            _remoteServer.Dispose();

            // Returns Windows to the state it was in before the app opened — no controller should stay
            // disabled just because Padlume was closed.
            foreach (var instanceId in _selfDisabledInstanceIds)
                ControllerDeviceLock.SetEnabled(instanceId, true);
            _selfDisabledInstanceIds.Clear();
            PersistSelfDisabledIds();

            // Without this, if the compact widget was open, closing the main window alone wouldn't be
            // enough to end the process (the default ShutdownMode is "on last window close", and the
            // widget counts as an open window) — the app would get stuck with just the widget floating
            // and no tray icon left to close it (MainWindow's Closed is what disposes the tray).
            _compactWidget?.Close();

            _isExiting = true;
            Close();
        }

        /// <summary>
        /// Re-enables any device that a previous Padlume session disabled and failed to revert (process
        /// force-killed, crash, power loss, etc. — ExitApplication only runs on the normal exit path).
        /// Without this, that controller would stay disabled forever until someone manually fixed it in
        /// Device Manager.
        /// </summary>
        private void RecoverDevicesDisabledByPreviousSession()
        {
            var pending = DisabledDeviceStore.Load();
            if (pending.Count == 0)
                return;

            foreach (var instanceId in pending)
            {
                bool ok = ControllerDeviceLock.SetEnabled(instanceId, true);
                App.Log("Recovery", $"Re-enabling {instanceId} left pending by a previous session: {(ok ? "ok" : "failed")}.");
            }

            DisabledDeviceStore.Save(Array.Empty<string>());
        }

        private void PersistSelfDisabledIds() => DisabledDeviceStore.Save(_selfDisabledInstanceIds);

        private void RefreshControllerList()
        {
            var previousSelectedKey = (ControllerListBox.SelectedItem as ControllerItem)?.HistoryKey;

            var live = RawGameController.RawGameControllers;
            var liveByKey = live.ToDictionary(ControllerItem.KeyFor, c => c);

            // Only removes controllers that really disappeared — one that vanished because Padlume
            // itself disabled it (see EnforceExclusivity) stays in the list, marked as blocked, so the
            // user can still select it again to reclaim priority.
            for (int i = _controllers.Count - 1; i >= 0; i--)
            {
                var item = _controllers[i];
                bool isLive = liveByKey.ContainsKey(item.HistoryKey);
                bool isSelfDisabled = item.DeviceInstanceId != null && _selfDisabledInstanceIds.Contains(item.DeviceInstanceId);
                if (!isLive && !isSelfDisabled)
                    _controllers.RemoveAt(i);
            }

            var newItems = new List<ControllerItem>();
            foreach (var (key, controller) in liveByKey)
            {
                var existing = _controllers.FirstOrDefault(i => i.HistoryKey == key);
                if (existing != null)
                {
                    // Same physical controller, possibly a new RawGameController instance (e.g. just
                    // re-enabled) — updates the reference without duplicating the entry.
                    existing.UpdateControllerReference(controller);
                    continue;
                }

                var item = new ControllerItem(controller);
                _controllers.Add(item);
                newItems.Add(item);
            }

            foreach (var item in newItems)
                ResolveDeviceInstanceId(item);

            EmptyListText.Visibility = _controllers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ControllerListBox.Visibility = _controllers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            if (previousSelectedKey != null)
            {
                var match = _controllers.FirstOrDefault(i => i.HistoryKey == previousSelectedKey);
                if (match != null)
                    ControllerListBox.SelectedItem = match;
            }
            else if (_controllers.Count > 0)
            {
                ControllerListBox.SelectedIndex = 0;
            }

            // Discovers, once per controller, whether the connection is Bluetooth or USB and (when
            // available) the battery percentage reported by the paired Bluetooth device. The result is
            // cached on the ControllerItem itself; UpdateBatteryDisplay only reads that cache.
            foreach (var item in newItems)
                _ = EnrichListItemAsync(item);

            // Runs before UpdateBatteryDisplay on purpose: enforcing exclusivity is what actually
            // matters here, and it can't be held hostage by an exception in the (cosmetic) display.
            if (ControllerListBox.SelectedItem is ControllerItem selected)
                EnforceExclusivity(selected);

            UpdateBatteryDisplay();
        }

        /// <summary>Correlates the RawGameController with the matching Windows device (best-effort).</summary>
        private void ResolveDeviceInstanceId(ControllerItem item)
        {
            var excluded = new HashSet<string>(_controllers
                .Where(i => i != item && i.DeviceInstanceId != null)
                .Select(i => i.DeviceInstanceId!));

            item.DeviceInstanceId = ControllerDeviceLock.TryFindDeviceInstanceId(
                item.Controller.HardwareVendorId, item.Controller.HardwareProductId, excluded);
        }

        /// <summary>
        /// Ensures only <paramref name="selected"/> receives input: re-enables it if it was blocked and
        /// disables every other known controller. Idempotent — can be called every time the list or the
        /// selection changes at no extra cost (controllers already in the right state are skipped).
        /// </summary>
        private void EnforceExclusivity(ControllerItem selected)
        {
            bool anyFailure = false;

            if (selected.IsBlocked && selected.DeviceInstanceId != null)
            {
                if (ControllerDeviceLock.SetEnabled(selected.DeviceInstanceId, true))
                {
                    _selfDisabledInstanceIds.Remove(selected.DeviceInstanceId);
                    selected.SetBlocked(false);
                }
                else
                {
                    anyFailure = true;
                }
            }

            foreach (var other in _controllers)
            {
                if (other == selected || other.IsBlocked)
                    continue;

                if (other.DeviceInstanceId == null)
                {
                    // No resolved ID means there's no way to disable it — tries to resolve it again now
                    // (may have failed earlier due to a transient condition) instead of staying stuck forever.
                    ResolveDeviceInstanceId(other);
                    if (other.DeviceInstanceId == null)
                    {
                        anyFailure = true;
                        continue;
                    }
                }

                if (ControllerDeviceLock.SetEnabled(other.DeviceInstanceId, false))
                {
                    _selfDisabledInstanceIds.Add(other.DeviceInstanceId);
                    other.SetBlocked(true);
                }
                else
                {
                    anyFailure = true;
                }
            }

            PersistSelfDisabledIds();

            if (anyFailure)
            {
                InfoTitleText.Text = Strings.CouldNotBlockTitle;
                InfoSubtitleText.Text = Strings.RunAsAdminSubtitle;
            }
        }

        private async Task EnrichListItemAsync(ControllerItem item)
        {
            var (isBluetooth, pct) = await TryGetBluetoothInfoAsync(item.DisplayName);
            item.SetConnectionType(isBluetooth);

            // For DS4/DualSense, the value Windows caches for the paired Bluetooth device is known to
            // be unreliable (doesn't update properly) — that's exactly why the proprietary HID reader
            // (UpdateBatteryDisplay) exists. Letting this value overwrite what the proprietary reader
            // already showed produced wild jumps in the history (e.g. 45% -> 25% -> 35% within a few
            // minutes) every time the controller list refreshed. For these, only the connection type
            // (above) is used from here; the battery is entirely up to the proprietary reader.
            if (item.HasProprietaryBatteryReader)
                return;

            if (pct is int p)
            {
                item.SetBatteryPercent(p);
                BatteryHistoryStore.RecordPoint(item.HistoryKey, p);
                CheckLowBatteryNotification(item, p);
            }
        }

        private void ControllerPopup_Opened(object sender, EventArgs e)
        {
            // Anchors the list's right edge to the button's right edge, since the popup is wider than
            // the button itself and Placement="Bottom" aligns left edges by default.
            ControllerPopup.HorizontalOffset = SelectorToggle.ActualWidth - ControllerPopup.Width;
        }

        private void ControllerListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectorToggle.IsChecked = false;

            // Before UpdateBatteryDisplay on purpose — see the equivalent comment in
            // RefreshControllerList.
            if (ControllerListBox.SelectedItem is ControllerItem selected)
                EnforceExclusivity(selected);

            UpdateBatteryDisplay();
        }

        private static readonly Color ColorGood = Color.FromRgb(0x22, 0xC5, 0x5E);    // green
        private static readonly Color ColorMedium = Color.FromRgb(0xF5, 0xA6, 0x23);  // yellow/orange
        private static readonly Color ColorLow = Color.FromRgb(0xEF, 0x44, 0x44);     // red
        private static readonly Color ColorNeutral = Color.FromRgb(0x9A, 0x9F, 0xB3); // gray

        /// <summary>
        /// The property Windows uses internally to show the battery percentage in Settings > Bluetooth
        /// & devices. Available on the paired device's association endpoint (AEP) even when the gaming
        /// API (RawGameController) can't read battery over HID.
        /// </summary>
        private const string BatteryLevelProperty = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

        private static readonly string[] BluetoothProtocolIds =
        {
            "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}", // Classic Bluetooth (BR/EDR)
            "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}", // Bluetooth Low Energy
        };

        /// <summary>
        /// Looks for the controller among paired Bluetooth devices (classic and BLE) by name. A match
        /// confirms the connection is Bluetooth (even without battery data available); the absence of a
        /// match is the signal used to infer "via USB" (cable or proprietary dongle, since those never
        /// show up in this paired-Bluetooth-device list).
        /// </summary>
        private static async Task<(bool IsBluetooth, int? BatteryPercent)> TryGetBluetoothInfoAsync(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
                return (false, null);

            bool foundAsBluetoothDevice = false;

            foreach (var protocolId in BluetoothProtocolIds)
            {
                try
                {
                    var aqsFilter = $"(System.Devices.Aep.ProtocolId:=\"{protocolId}\")";

                    // On some machines (no Bluetooth radio, service turned off, etc.) FindAllAsync can
                    // hang forever instead of failing fast — and cancelling the token isn't always
                    // enough to unblock the native call. That's why we use Task.WhenAny: we move on
                    // after the timeout regardless, even if the original task keeps hanging in the
                    // background.
                    var findTask = DeviceInformation.FindAllAsync(
                        aqsFilter,
                        new[] { BatteryLevelProperty },
                        DeviceInformationKind.AssociationEndpoint).AsTask();

                    var winner = await Task.WhenAny(findTask, Task.Delay(TimeSpan.FromSeconds(6)));
                    if (winner != findTask)
                        continue; // timeout: try the next protocol (or give up)

                    var devices = await findTask;

                    var match = devices.FirstOrDefault(d =>
                        string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));

                    if (match == null)
                        continue;

                    foundAsBluetoothDevice = true;

                    if (match.Properties.TryGetValue(BatteryLevelProperty, out var raw) && raw != null)
                    {
                        var value = Convert.ToInt32(raw);
                        if (value is >= 0 and <= 100)
                            return (true, value);
                    }
                }
                catch (Exception ex)
                {
                    // No Bluetooth adapter, no permission, or the API is unavailable: continue without this data.
                    App.Log("TryGetBluetoothInfoAsync", $"Failed querying protocol {protocolId} for \"{deviceName}\": {ex.Message}");
                }
            }

            return (foundAsBluetoothDevice, null);
        }

        private void UpdateControllerPhoto(ControllerItem? item)
        {
            if (item?.PhotoSource != null)
            {
                ControllerPhotoImage.Source = item.PhotoSource;
                ControllerPhotoImage.Visibility = Visibility.Visible;
                ControllerPhotoFallback.Visibility = Visibility.Collapsed;
            }
            else
            {
                ControllerPhotoImage.Visibility = Visibility.Collapsed;
                ControllerPhotoFallback.Visibility = Visibility.Visible;
            }
        }

        // async void is acceptable here on purpose: the 3 call sites for this method (the 3s timer, list
        // selection, list refresh) already treated it as "fire and forget" before this change, and any
        // exception still falls through to the global DispatcherUnhandledException.
        private async void UpdateBatteryDisplay()
        {
            var item = ControllerListBox.SelectedItem as ControllerItem;
            RenderHistoryPanel(item);
            UpdateControllerPhoto(item);

            if (item == null)
            {
                ControllerNameText.Text = Strings.NoControllerSelected;
                ConnectionStatusText.Text = Strings.NotConnected;
                ConnectionStatusText.Foreground = new SolidColorBrush(ColorNeutral);
                ControllerIdText.Text = Strings.IdPlaceholder;
                ConnectionTypeText.Text = Strings.BluetoothOrUsb;
                BatteryLabelText.Text = "";
                SetBattery(item, "--", ColorNeutral, 0);

                if (_controllers.Count == 0)
                {
                    InfoTitleText.Text = Strings.NoControllerFoundTitle;
                    InfoSubtitleText.Text = Strings.PairControllerSubtitle;
                }
                else
                {
                    InfoTitleText.Text = Strings.SelectControllerTitle;
                    InfoSubtitleText.Text = Strings.UseButtonSubtitle;
                }
                return;
            }

            ControllerNameText.Text = item.DisplayName;
            ConnectionStatusText.Text = Strings.Connected;
            ConnectionStatusText.Foreground = new SolidColorBrush(ColorGood);
            ConnectionTypeText.Text = item.ConnectionType != null ? $"via {item.ConnectionType}" : Strings.BluetoothOrUsb;

            // item.Controller may be momentarily stale (e.g. right after being re-enabled via SetupAPI,
            // before the RawGameControllerAdded event updates the reference) — accessing its properties
            // in that state can throw. That must not stop the rest of the screen (and, more importantly,
            // the EnforceExclusivity call in our caller) from running.
            try
            {
                ControllerIdText.Text = $"VID {item.Controller.HardwareVendorId:X4} • PID {item.Controller.HardwareProductId:X4}";
            }
            catch (Exception ex)
            {
                ControllerIdText.Text = Strings.IdPlaceholder;
                App.Log("UpdateBatteryDisplay", $"Failed reading VID/PID for {item.DisplayName}: {ex.Message}");
            }

            BatteryReport? report;
            try
            {
                report = item.Controller.TryGetBatteryReport();
            }
            catch (Exception ex)
            {
                InfoTitleText.Text = Strings.BatteryReadErrorTitle;
                InfoSubtitleText.Text = ex.Message;
                BatteryLabelText.Text = "";
                SetBattery(item, "--", ColorNeutral, 0);
                return;
            }

            if (report?.FullChargeCapacityInMilliwattHours is int full && full > 0 &&
                report.RemainingCapacityInMilliwattHours is int remaining)
            {
                ApplyBatteryPercent(item, Math.Clamp(100.0 * remaining / full, 0, 100), FormatStatus(report.Status));
                return;
            }

            int vendorId = item.Controller.HardwareVendorId;
            int productId = item.Controller.HardwareProductId;

            // DS4/DualSense: always tries the proprietary HID report BEFORE using any cached value. It's
            // intentional to run this every tick (not just once) and before the cache — the value
            // Windows keeps for the paired Bluetooth device isn't reliable for these two (see the
            // comment in EnrichListItemAsync); using the cache here would let the more accurate
            // proprietary reading get overwritten on every list refresh.
            //
            // The read itself (enumerating HID devices via SetupAPI + opening/reading the report) is
            // blocking I/O that can take tens of ms — runs in Task.Run to avoid blocking the UI every
            // 3s. Reconfirms the selection after the await: if the user switched controllers while the
            // read was running in the background, that result is already stale and must not be applied.
            if (DualShock4BatteryReader.IsDualShock4(vendorId, productId))
            {
                var (ds4Ok, ds4Percent, ds4Charging, ds4Bluetooth) = await Task.Run(() =>
                {
                    bool ok = DualShock4BatteryReader.TryReadBattery(out int p, out bool c, out bool bt);
                    return (ok, p, c, bt);
                });

                if (ControllerListBox.SelectedItem != item)
                    return;

                if (ds4Ok)
                {
                    // Transport confirmed by actually reading the HID report — more reliable than
                    // detection by paired-Bluetooth-device name, which can diverge from the name
                    // RawGameController exposes and mislabel the transport. Overwrites whatever was
                    // cached.
                    item.SetConnectionType(ds4Bluetooth);
                    ConnectionTypeText.Text = $"via {item.ConnectionType}";
                    ApplyBatteryPercent(item, ds4Percent, ds4Charging ? Strings.Charging : null);
                    return;
                }
            }

            if (DualSenseBatteryReader.IsDualSense(vendorId, productId))
            {
                var (dsOk, dsPercent, dsCharging) = await Task.Run(() =>
                {
                    bool ok = DualSenseBatteryReader.TryReadUsbBattery(out int p, out bool c);
                    return (ok, p, c);
                });

                if (ControllerListBox.SelectedItem != item)
                    return;

                if (dsOk)
                {
                    item.SetConnectionType(false);
                    ConnectionTypeText.Text = $"via {item.ConnectionType}";
                    ApplyBatteryPercent(item, dsPercent, dsCharging ? Strings.Charging : null);
                    return;
                }
            }

            // RawGameController didn't provide battery directly and this isn't a DS4/DualSense (or the
            // proprietary read failed this tick) — falls back to the Bluetooth value, if any, already
            // resolved in the background by EnrichListItemAsync (cached on the item, no repeated query here).
            if (item.BatteryPercent is double pct2)
            {
                ApplyBatteryPercent(item, pct2, report != null ? FormatStatus(report.Status) : null);
                return;
            }

            // Xbox: neither RawGameController's generic report nor the paired Bluetooth device provided
            // battery (can happen due to xinputhid.sys compatibility driver instability) — tries the
            // official XInputGetBatteryInformation API, which works over both USB and Bluetooth.
            if (XboxBatteryReader.IsXboxController(vendorId) &&
                XboxBatteryReader.TryReadBattery(out int xboxPercent, out bool xboxCharging))
            {
                ApplyBatteryPercent(item, xboxPercent, xboxCharging ? Strings.Charging : null);
                return;
            }

            if (item.ConnectionType == null)
            {
                // The background query (which also resolves this) hasn't finished yet.
                InfoTitleText.Text = Strings.CheckingBluetoothTitle;
                InfoSubtitleText.Text = Strings.MayTakeAFewSecondsSubtitle;
                BatteryLabelText.Text = "";
                SetBattery(item, "...", ColorNeutral, 0);
                return;
            }

            if (report == null)
            {
                InfoTitleText.Text = Strings.ControllerDoesNotReportBatteryTitle;
                InfoSubtitleText.Text = Strings.SomeControllersDontReportSubtitle;
            }
            else
            {
                InfoTitleText.Text = Strings.BatteryStatusText(FormatStatus(report.Status));
                InfoSubtitleText.Text = Strings.ExactLevelUnavailableSubtitle;
            }
            BatteryLabelText.Text = "";
            SetBattery(item, Strings.NotAvailable, ColorNeutral, 0);
        }

        private void ApplyBatteryPercent(ControllerItem item, double pct, string? statusText)
        {
            Color color = pct >= 50 ? ColorGood : pct >= 20 ? ColorMedium : ColorLow;
            string label = pct >= 50 ? Strings.GoodBattery : pct >= 20 ? Strings.MediumBattery : Strings.BatteryLow;

            InfoTitleText.Text = Strings.ControllerConnectedWorkingTitle;
            InfoSubtitleText.Text = statusText != null
                ? Strings.ReconnectIfNotUpdating(statusText)
                : Strings.IfNotUpdatingReconnect;
            BatteryLabelText.Text = label;
            SetBattery(item, $"{pct:0}%", color, pct);
            item.SetBatteryPercent(pct);
            BatteryHistoryStore.RecordPoint(item.HistoryKey, pct);
            CheckLowBatteryNotification(item, pct);
        }

        private void CheckLowBatteryNotification(ControllerItem item, double pct)
        {
            if (pct <= LowBatteryThreshold)
            {
                if (_lowBatteryNotified.Add(item.DisplayName))
                    _trayNotifier.ShowLowBatteryWarning(item.DisplayName, pct, LowBatteryToastDuration);
            }
            else if (pct >= LowBatteryResetThreshold)
            {
                _lowBatteryNotified.Remove(item.DisplayName);
            }
        }

        /// <summary>Color of a token from the current theme (Themes/Dark.xaml or Light.xaml) — used by
        /// code that draws by hand (history chart, battery bars) instead of via XAML, so it can't use
        /// DynamicResource directly. Queried live on every call on purpose, to follow the theme even if
        /// Windows switches from light to dark while the app is already open.</summary>
        private static Color ThemeColor(string resourceKey) =>
            ((SolidColorBrush)System.Windows.Application.Current.Resources[resourceKey]).Color;

        private void SetBattery(ControllerItem? item, string text, Color color, double pct)
        {
            BatteryText.Text = text;
            AccentBar.Background = new SolidColorBrush(color);
            ControllerCardBorder.BorderBrush = new SolidColorBrush(color) { Opacity = 0.55 };

            var dimColor = ThemeColor("DimBrush");
            BatteryBar1.Background = new SolidColorBrush(pct > 0 ? color : dimColor);
            BatteryBar2.Background = new SolidColorBrush(pct >= 34 ? color : dimColor);
            BatteryBar3.Background = new SolidColorBrush(pct >= 67 ? color : dimColor);

            _trayNotifier.UpdateController(
                ControllerNameText.Text,
                ConnectionStatusText.Text,
                (ConnectionStatusText.Foreground as SolidColorBrush)?.Color ?? ColorNeutral,
                item?.PhotoSource,
                text,
                pct,
                color);

            _compactWidget?.UpdateController(ControllerNameText.Text, text, pct, color, item?.PhotoSource);
        }

        private void RenderHistoryPanel(ControllerItem? item)
        {
            if (item == null)
            {
                _currentHistory = Array.Empty<BatteryHistoryPoint>();
                _displayedRemaining = null;
                HistoryChartCanvas.Visibility = Visibility.Collapsed;
                HistoryEmptyText.Visibility = Visibility.Visible;
                HistoryEmptyText.Text = Strings.SelectToViewHistory;
                HistoryRangeText.Text = "";
                TimeRemainingText.Text = "--";
                AverageUsageText.Text = "--";
                return;
            }

            var history = BatteryHistoryStore.GetHistory(item.HistoryKey);
            _currentHistory = history;

            if (history.Count < 2)
            {
                _displayedRemaining = null;
                HistoryChartCanvas.Visibility = Visibility.Collapsed;
                HistoryEmptyText.Visibility = Visibility.Visible;
                HistoryEmptyText.Text = Strings.NotEnoughDataYet;
                HistoryRangeText.Text = "";
                TimeRemainingText.Text = "--";
                AverageUsageText.Text = "--";
                return;
            }

            HistoryEmptyText.Visibility = Visibility.Collapsed;
            HistoryChartCanvas.Visibility = Visibility.Visible;
            DrawHistoryChart(history);

            var span = history[^1].TimestampUtc - history[0].TimestampUtc;
            HistoryRangeText.Text = span.TotalHours >= 1 ? Strings.LastHours(span.TotalHours) : Strings.LastMinutes(span.TotalMinutes);

            var (remaining, ratePerHour, isCharging) = BatteryHistoryStore.Estimate(history, item.BatteryPercent ?? history[^1].Percent);
            if (isCharging)
            {
                _displayedRemaining = null;
                TimeRemainingText.Text = Strings.Charging;
                AverageUsageText.Text = "--";
            }
            else if (remaining is TimeSpan remainingSpan && ratePerHour is double rate)
            {
                // Re-anchors the countdown to the freshly-computed estimate (fixes any accumulated
                // drift) — TickCountdown handles counting down second by second until the next time
                // this method runs (every 3s, via UpdateBatteryDisplay).
                _displayedRemaining = remainingSpan;
                TimeRemainingText.Text = FormatDuration(remainingSpan);
                AverageUsageText.Text = Strings.PercentPerHour(rate);
            }
            else
            {
                _displayedRemaining = null;
                TimeRemainingText.Text = Strings.CollectingData;
                AverageUsageText.Text = "--";
            }
        }

        /// <summary>
        /// Makes "time remaining" count down on its own, one second at a time, between recalculations of
        /// the real estimate (which only changes every 3s or when a new battery sample arrives). Purely
        /// visual: it doesn't invent any data, it just keeps the number from sitting still between updates.
        /// </summary>
        private void TickCountdown()
        {
            if (_displayedRemaining is not TimeSpan remaining || remaining <= TimeSpan.Zero)
                return;

            remaining -= TimeSpan.FromSeconds(1);
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            _displayedRemaining = remaining;
            TimeRemainingText.Text = FormatDuration(remaining);
        }

        // Chart layout computed in DrawHistoryChart and reused by the hover handler (avoids recomputing
        // everything on every mouse move).
        private const double HistoryChartLeftMargin = 28; // space for the % labels on the left
        private const double HistoryChartPadding = 4; // margin so the line doesn't touch the top/bottom
        private double _historyPlotWidth;
        private DateTime _historyMinTime;
        private double _historySpanSeconds = 1;
        private double _lastGridWidth = -1;
        private double _lastGridHeight = -1;

        private void DrawHistoryChart(IReadOnlyList<BatteryHistoryPoint> history)
        {
            double width = HistoryChartCanvas.ActualWidth;
            double height = HistoryChartCanvas.ActualHeight;
            if (width <= 0 || height <= 0 || history.Count < 2)
                return;

            // Extends the line up to now with the controller's most recent reading (without writing
            // anything to disk) — without this the chart would sit frozen at the last saved sample's
            // timestamp (up to 5min ago) instead of tracking the real-time clock like the rest of the screen.
            IReadOnlyList<BatteryHistoryPoint> plotted = history;
            if (ControllerListBox.SelectedItem is ControllerItem selected && selected.BatteryPercent is double livePct)
            {
                plotted = new List<BatteryHistoryPoint>(history)
                {
                    new BatteryHistoryPoint { TimestampUtc = DateTime.UtcNow, Percent = livePct },
                };
            }

            var minTime = plotted[0].TimestampUtc;
            var maxTime = plotted[^1].TimestampUtc;
            var spanSeconds = (maxTime - minTime).TotalSeconds;
            if (spanSeconds <= 0)
                spanSeconds = 1;

            double plotWidth = Math.Max(1, width - HistoryChartLeftMargin);
            _historyPlotWidth = plotWidth;
            _historyMinTime = minTime;
            _historySpanSeconds = spanSeconds;

            double YFor(double pct) => HistoryChartPadding + (1 - Math.Clamp(pct, 0, 100) / 100.0) * (height - HistoryChartPadding * 2);
            double XFor(DateTime t) => HistoryChartLeftMargin + (t - minTime).TotalSeconds / spanSeconds * plotWidth;

            var points = new PointCollection();
            foreach (var p in plotted)
                points.Add(new Point(XFor(p.TimestampUtc), YFor(p.Percent)));

            HistoryPolyline.Points = points;

            var fillPoints = new PointCollection(points) { new Point(HistoryChartLeftMargin + plotWidth, height), new Point(HistoryChartLeftMargin, height) };
            HistoryFillPolygon.Points = fillPoints;

            // Reference grid (0/25/50/75/100%) — without it there'd be no way to tell if the line was
            // varying by 5% or 50%, just the shape of the curve. Only depends on width/height (not
            // time), and this method runs every 1s (to stretch the line to "now") — redrawing the 10
            // elements every time would be wasteful; only recreated when the box actually changes size.
            if (width != _lastGridWidth || height != _lastGridHeight)
            {
                _lastGridWidth = width;
                _lastGridHeight = height;

                HistoryGridCanvas.Children.Clear();
                var gridLineColor = new SolidColorBrush(ThemeColor("BorderBrush"));
                var gridLabelColor = new SolidColorBrush(ThemeColor("TextSecondaryBrush"));
                foreach (var gridPct in new[] { 0, 25, 50, 75, 100 })
                {
                    double y = YFor(gridPct);
                    HistoryGridCanvas.Children.Add(new Line
                    {
                        X1 = HistoryChartLeftMargin,
                        X2 = width,
                        Y1 = y,
                        Y2 = y,
                        Stroke = gridLineColor,
                        StrokeThickness = 1,
                        StrokeDashArray = new DoubleCollection { 2, 3 },
                    });

                    var label = new TextBlock
                    {
                        Text = $"{gridPct}%",
                        FontSize = 9,
                        Foreground = gridLabelColor,
                    };
                    Canvas.SetLeft(label, 0);
                    Canvas.SetTop(label, Math.Clamp(y - 6, 0, height - 10));
                    HistoryGridCanvas.Children.Add(label);
                }
            }

            int minPct = (int)plotted.Min(p => p.Percent);
            int maxPct = (int)plotted.Max(p => p.Percent);
            HistoryStatsText.Text = Strings.MinMaxReadings(minPct, maxPct, history.Count);
        }

        private void HistoryChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_currentHistory.Count >= 2)
                DrawHistoryChart(_currentHistory);
        }

        /// <summary>Shows the exact time and % of the point nearest the cursor, with a guide line to it.</summary>
        private void HistoryChartCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_currentHistory.Count < 2 || _historyPlotWidth <= 0)
                return;

            var pos = e.GetPosition(HistoryChartCanvas);
            if (pos.X < HistoryChartLeftMargin)
            {
                HideHistoryHover();
                return;
            }

            double ratio = Math.Clamp((pos.X - HistoryChartLeftMargin) / _historyPlotWidth, 0, 1);
            var targetTime = _historyMinTime.AddSeconds(ratio * _historySpanSeconds);
            var nearest = _currentHistory.OrderBy(p => Math.Abs((p.TimestampUtc - targetTime).TotalSeconds)).First();

            double height = HistoryChartCanvas.ActualHeight;
            double x = HistoryChartLeftMargin + (nearest.TimestampUtc - _historyMinTime).TotalSeconds / _historySpanSeconds * _historyPlotWidth;
            double y = HistoryChartPadding + (1 - Math.Clamp(nearest.Percent, 0, 100) / 100.0) * (height - HistoryChartPadding * 2);

            HistoryHoverLine.X1 = x;
            HistoryHoverLine.X2 = x;
            HistoryHoverLine.Y1 = 0;
            HistoryHoverLine.Y2 = height;
            HistoryHoverLine.Visibility = Visibility.Visible;

            Canvas.SetLeft(HistoryHoverDot, x - 4);
            Canvas.SetTop(HistoryHoverDot, y - 4);
            HistoryHoverDot.Visibility = Visibility.Visible;

            HistoryHoverTooltipText.Text = $"{nearest.TimestampUtc.ToLocalTime():HH:mm} — {nearest.Percent:0}%";
            HistoryHoverTooltip.Visibility = Visibility.Visible;
            HistoryHoverTooltip.UpdateLayout();
            double tooltipX = Math.Clamp(x - HistoryHoverTooltip.ActualWidth / 2, 0, Math.Max(0, HistoryChartCanvas.ActualWidth - HistoryHoverTooltip.ActualWidth));
            Canvas.SetLeft(HistoryHoverTooltip, tooltipX);
            Canvas.SetTop(HistoryHoverTooltip, Math.Max(0, y - 26));
        }

        private void HistoryChartCanvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => HideHistoryHover();

        private void HideHistoryHover()
        {
            HistoryHoverLine.Visibility = Visibility.Collapsed;
            HistoryHoverDot.Visibility = Visibility.Collapsed;
            HistoryHoverTooltip.Visibility = Visibility.Collapsed;
        }

        private static string FormatDuration(TimeSpan span)
        {
            if (span.TotalHours >= 1)
                return Strings.RemainingLong((int)span.TotalHours, span.Minutes);
            return Strings.RemainingShort(Math.Max(1, span.Minutes));
        }

        private void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressStartupCheckboxEvent)
                return;

            var wantEnabled = StartWithWindowsCheckBox.IsChecked == true;
            if (StartupManager.SetEnabled(wantEnabled))
                return;

            // Couldn't write to the registry (e.g. blocked by group policy). Reverts the checkbox to
            // its previous state instead of leaving it showing something that wasn't actually applied.
            _suppressStartupCheckboxEvent = true;
            StartWithWindowsCheckBox.IsChecked = !wantEnabled;
            _suppressStartupCheckboxEvent = false;

            InfoTitleText.Text = Strings.CouldNotChangeStartupTitle;
            InfoSubtitleText.Text = Strings.WindowsDeniedChangeSubtitle;
        }

        private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressThemeRadioEvent)
                return;

            var preference = sender switch
            {
                var s when s == ThemeLightRadio => ThemePreference.Light,
                var s when s == ThemeDarkRadio => ThemePreference.Dark,
                _ => ThemePreference.Auto,
            };
            ThemeManager.SetPreference(preference);
        }

        private void PhoneControlCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (PhoneControlCheckBox.IsChecked == true)
            {
                var ip = RemoteControlServer.GetLocalIPAddress();
                if (ip == null)
                {
                    PhoneControlCheckBox.IsChecked = false;
                    PhoneControlStatusText.Text = Strings.PhoneControlNoNetwork;
                    PhoneControlStatusText.Visibility = Visibility.Visible;
                    return;
                }

                if (!_remoteServer.Start(RemoteControlPort))
                {
                    PhoneControlCheckBox.IsChecked = false;
                    PhoneControlStatusText.Text = Strings.PhoneControlUnavailable;
                    PhoneControlStatusText.Visibility = Visibility.Visible;
                    return;
                }

                var url = $"http://{ip}:{RemoteControlPort}";
                PhoneControlStatusText.Text = Strings.PhoneControlHint($"{ip}:{RemoteControlPort}");
                PhoneControlStatusText.Visibility = Visibility.Visible;

                PhoneControlQrImage.Source = GenerateQrCode(url);
                PhoneControlQrBorder.Visibility = Visibility.Visible;
            }
            else
            {
                _remoteServer.Stop();
                PhoneControlStatusText.Visibility = Visibility.Collapsed;
                PhoneControlQrBorder.Visibility = Visibility.Collapsed;
                PhoneControlQrImage.Source = null;
            }
        }

        /// <summary>Renders a scannable "open this URL" QR code — lets a phone jump straight to the phone-control page via camera, without typing the local IP by hand.</summary>
        private static BitmapImage GenerateQrCode(string content)
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(content, QRCoder.QRCodeGenerator.ECCLevel.Q);
            var pngBytes = new QRCoder.PngByteQRCode(data).GetGraphic(8);

            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(pngBytes);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>Called from the RemoteControlServer's background HTTP thread — marshals onto the UI thread since it reads _controllers and the current selection.</summary>
        private IReadOnlyList<RemoteControllerInfo> GetRemoteControllerSnapshot()
        {
            return Dispatcher.Invoke(() =>
            {
                var selected = ControllerListBox.SelectedItem as ControllerItem;
                return _controllers.Select(c => new RemoteControllerInfo
                {
                    Key = c.HistoryKey,
                    Name = c.DisplayName,
                    BatteryText = c.BatteryPercent is double pct ? $"{pct:0}%" : Strings.NotAvailable,
                    BatteryPercent = c.BatteryPercent ?? 0,
                    IsSelected = c == selected,
                    IsBlocked = c.IsBlocked,
                }).ToList();
            });
        }

        /// <summary>Called from the RemoteControlServer's background HTTP thread — marshals onto the UI thread to reuse the exact same selection path as clicking a controller in the list.</summary>
        private void SelectControllerByKeyFromRemote(string key)
        {
            Dispatcher.Invoke(() =>
            {
                var match = _controllers.FirstOrDefault(c => c.HistoryKey == key);
                if (match != null)
                    ControllerListBox.SelectedItem = match;
            });
        }

        private static string FormatStatus(BatteryStatus status) => status switch
        {
            BatteryStatus.Charging => Strings.Charging,
            BatteryStatus.Discharging => Strings.Discharging,
            BatteryStatus.Idle => Strings.ConnectedIdle,
            BatteryStatus.NotPresent => Strings.BatteryNotFound,
            _ => status.ToString()
        };

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshControllerList();

        private void CompactModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_compactWidget == null)
            {
                _compactWidget = new CompactWidgetWindow();
                _compactWidget.RestoreRequested += (_, _) =>
                {
                    _compactWidget.Hide();
                    Show();
                    Activate();
                };
            }

            Hide();
            _compactWidget.ShowAtDefaultPositionIfFirstShow();

            // The widget only gets a battery update as a side effect of SetBattery (called from
            // UpdateBatteryDisplay) — without this it would show "--" until the next 3s tick.
            UpdateBatteryDisplay();
        }
    }
}
