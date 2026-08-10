using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Padlume
{
    /// <summary>
    /// Turns the app's automatic startup with Windows on/off via a Scheduled Task ("At log on", highest
    /// privileges) rather than the classic HKCU\...\Run registry key. Run-key entries are launched at
    /// medium integrity with no way to elevate them — Windows never shows a UAC prompt for them, so an
    /// app whose manifest requires administrator (like Padlume) just silently fails to start. A
    /// Scheduled Task configured with RunLevel=Highest is the standard way around that: the Task
    /// Scheduler service itself is allowed to launch it elevated, no interactive consent needed.
    /// </summary>
    public static class StartupManager
    {
        private const string TaskName = "Padlume";

        // Legacy location used by older versions — cleaned up opportunistically so a stale entry from
        // before this change doesn't linger forever (harmless since it silently fails to launch anyway,
        // but no reason to leave it behind).
        private const string LegacyRunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string LegacyValueName = "Padlume";

        public static bool IsEnabled()
        {
            RemoveLegacyRunKeyEntry();
            return RunSchtasks($"/query /tn \"{TaskName}\"", timeoutMs: 5000) == 0;
        }

        /// <summary>Returns false if the change couldn't be applied (e.g. blocked by group policy).</summary>
        public static bool SetEnabled(bool enabled)
        {
            RemoveLegacyRunKeyEntry();

            if (!enabled)
                return RunSchtasks($"/delete /tn \"{TaskName}\" /f", timeoutMs: 5000) is 0 or 1;
            // Exit code 1 here typically just means "task didn't exist to begin with" — not a real failure.

            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
                return false;

            return RunSchtasks(
                $"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\"\" /sc onlogon /rl highest /f",
                timeoutMs: 5000) == 0;
        }

        private static void RemoveLegacyRunKeyEntry()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, writable: true);
                key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }
            catch
            {
                // Not worth failing IsEnabled()/SetEnabled() over cleaning up a leftover from an old version.
            }
        }

        /// <summary>Runs schtasks.exe and returns its exit code, or -1 on failure to even start it. Same
        /// deadlock-avoidance pattern as ControllerDeviceLock.SetEnabled: reads both streams
        /// asynchronously before waiting, with a timeout and a kill fallback.</summary>
        private static int RunSchtasks(string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null)
                {
                    App.Log("StartupManager", $"schtasks {arguments}: couldn't start schtasks.exe.");
                    return -1;
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(timeoutMs))
                {
                    App.Log("StartupManager", $"schtasks {arguments}: didn't respond within {timeoutMs}ms, terminating.");
                    try { process.Kill(entireProcessTree: true); } catch { /* may have already exited on its own */ }
                    return -1;
                }

                if (process.ExitCode != 0)
                    App.Log("StartupManager", $"schtasks {arguments} returned {process.ExitCode}. {stdoutTask.Result}{stderrTask.Result}".Trim());
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                App.Log("StartupManager", $"schtasks {arguments} threw: {ex.Message}");
                return -1;
            }
        }
    }
}
