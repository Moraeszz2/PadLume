using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Padlume
{
    /// <summary>
    /// Persists to disk which devices Padlume has disabled (see ControllerDeviceLock and
    /// MainWindow.EnforceExclusivity). ExitApplication re-enables everything on the normal exit path,
    /// but that doesn't run if the process is force-killed or crashes — without this on-disk record, a
    /// controller disabled under those conditions would stay that way forever. On the next launch, the
    /// app reads this file and tries to re-enable anything left pending from a previous session.
    /// </summary>
    public static class DisabledDeviceStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Padlume",
            "disabled-devices.json");

        public static HashSet<string> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var loaded = JsonSerializer.Deserialize<HashSet<string>>(json);
                    if (loaded != null)
                        return loaded;
                }
            }
            catch (Exception ex)
            {
                // Corrupted or unreadable file: treat it as if nothing were pending.
                App.Log("DisabledDeviceStore", $"Load() failed, skipping pending recovery: {ex.Message}");
            }

            return new HashSet<string>();
        }

        public static void Save(IEnumerable<string> instanceIds)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(FilePath, JsonSerializer.Serialize(instanceIds));
            }
            catch (Exception ex)
            {
                // Write failure (disk full, no permission, etc.): at worst, recovery on the next launch
                // won't know about this specific device.
                App.Log("DisabledDeviceStore", $"Save() failed: {ex.Message}");
            }
        }
    }
}
