using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Padlume
{
    public partial class App : System.Windows.Application
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Padlume",
            "crash.log");

        public App()
        {
            // Without this, any unhandled exception anywhere (a UI event, a "fire-and-forget" task like
            // the background Bluetooth queries, etc.) brings down the entire app without leaving any
            // trace of what happened. Here we log the error and, on the UI thread, keep the app alive
            // instead of letting it die silently.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Must run before base.OnStartup, which is what shows the MainWindow (via StartupUri) —
            // without this the window would appear for an instant without the theme dictionary loaded.
            ThemeManager.ApplyCurrentSystemTheme();
            base.OnStartup(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogException("UI", e.Exception);
            e.Handled = true; // keeps the app open; the screen may show stale data, but it doesn't close
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                LogException("Background", ex);
            // Exception outside the UI thread with IsTerminating=true: the process is going to exit
            // either way (there's no "recovering" from this), but at least the reason gets logged.
        }

        private void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            LogException("Task", e.Exception);
            e.SetObserved(); // keeps the process from being torn down by a failed "fire-and-forget" task
        }

        private static void LogException(string source, Exception ex) => Log(source, ex.ToString());

        /// <summary>Shared diagnostic log (%AppData%/Padlume/crash.log), also used outside exception
        /// scenarios — e.g. ControllerDeviceLock logging SetupAPI failures that don't throw (the API
        /// just returns false).</summary>
        public static void Log(string source, string message)
        {
            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({source}) {message}\n\n");
            }
            catch
            {
                // If even the log can't be written, there's nothing more to do here.
            }
        }
    }
}
