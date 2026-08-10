using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Padlume
{
    /// <summary>
    /// Turns the app's automatic startup with Windows on/off via the standard registry key
    /// (HKCU\...\Run) — needs neither administrator privilege nor an installer.
    /// </summary>
    public static class StartupManager
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Padlume";

        /// <summary>
        /// Never lets a registry failure (permission denied by group policy, missing key, etc.)
        /// propagate — this is called during window startup, and an exception here would bring down the
        /// entire app over a checkbox.
        /// </summary>
        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string;
            }
            catch (Exception ex)
            {
                App.Log("StartupManager", $"IsEnabled() failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Returns false if the change couldn't be applied (e.g. blocked by group policy).</summary>
        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key == null)
                    return false;

                if (enabled)
                {
                    var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (string.IsNullOrEmpty(exePath))
                        return false;

                    key.SetValue(ValueName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }

                return true;
            }
            catch (Exception ex)
            {
                App.Log("StartupManager", $"SetEnabled({enabled}) failed: {ex.Message}");
                return false;
            }
        }
    }
}
