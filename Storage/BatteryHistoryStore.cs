using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Padlume
{
    public class BatteryHistoryPoint
    {
        public DateTime TimestampUtc { get; set; }
        public double Percent { get; set; }
    }

    /// <summary>
    /// Stores, on disk, the time series of battery readings for each controller, to feed the history
    /// chart and the remaining-time estimate. A new reading is only saved if it changed meaningfully
    /// since the last one, so the file doesn't get bloated with the same value repeated every few
    /// seconds (the app re-reads the battery much more often than it actually changes).
    /// </summary>
    public static class BatteryHistoryStore
    {
        private const int MaxPointsPerController = 500;
        private const int MaxTrackedControllers = 50;
        private const int MaxDisplayNameLength = 80;
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
        private static readonly TimeSpan MinIntervalBetweenPoints = TimeSpan.FromMinutes(5);

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Padlume",
            "history.json");

        private static readonly object Lock = new();
        private static Dictionary<string, List<BatteryHistoryPoint>>? _cache;

        public static string KeyFor(string displayName, ushort vendorId, ushort productId)
        {
            // The name comes from the device (HID/Bluetooth) and isn't trustworthy — a malformed or
            // malicious controller could advertise an absurdly long name. Truncates before using it as
            // a key, both for robustness and to keep the history file from growing unnecessarily.
            if (displayName.Length > MaxDisplayNameLength)
                displayName = displayName[..MaxDisplayNameLength];
            return $"{displayName}|{vendorId:X4}|{productId:X4}";
        }

        public static void RecordPoint(string key, double percent)
        {
            lock (Lock)
            {
                var data = Load();
                if (!data.TryGetValue(key, out var list))
                {
                    list = new List<BatteryHistoryPoint>();
                    data[key] = list;
                }

                var now = DateTime.UtcNow;
                if (list.Count > 0)
                {
                    var last = list[^1];
                    if (now - last.TimestampUtc < MinIntervalBetweenPoints && Math.Abs(last.Percent - percent) < 1)
                        return;
                }

                list.Add(new BatteryHistoryPoint { TimestampUtc = now, Percent = percent });

                var cutoff = now - MaxAge;
                list.RemoveAll(p => p.TimestampUtc < cutoff);
                if (list.Count > MaxPointsPerController)
                    list.RemoveRange(0, list.Count - MaxPointsPerController);

                Save(data);
            }
        }

        public static IReadOnlyList<BatteryHistoryPoint> GetHistory(string key)
        {
            lock (Lock)
            {
                var data = Load();
                return data.TryGetValue(key, out var list) ? list.ToList() : Array.Empty<BatteryHistoryPoint>();
            }
        }

        /// <summary>
        /// Estimates average usage (%/hour) and time remaining from a recent window of the history,
        /// not the all-time average — so it reflects the current usage pattern.
        /// </summary>
        public static (TimeSpan? Remaining, double? RatePercentPerHour, bool IsCharging) Estimate(
            IReadOnlyList<BatteryHistoryPoint> history, double currentPercent)
        {
            if (history.Count < 2)
                return (null, null, false);

            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(6);
            var recent = history.Where(p => p.TimestampUtc >= cutoff).ToList();
            if (recent.Count < 2)
                recent = history.TakeLast(20).ToList();

            var first = recent[0];
            var last = recent[^1];
            var elapsedHours = (last.TimestampUtc - first.TimestampUtc).TotalHours;

            // Less than ~9 minutes of data doesn't give a reliable rate.
            if (elapsedHours < 0.15)
                return (null, null, false);

            var delta = last.Percent - first.Percent;
            if (delta >= 0)
                return (null, null, true); // charging or steady: "time remaining" doesn't apply

            var ratePerHour = -delta / elapsedHours;
            if (ratePerHour <= 0.01)
                return (null, null, false);

            var remainingHours = currentPercent / ratePerHour;
            return (TimeSpan.FromHours(remainingHours), ratePerHour, false);
        }

        private static Dictionary<string, List<BatteryHistoryPoint>> Load()
        {
            if (_cache != null)
                return _cache;

            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, List<BatteryHistoryPoint>>>(json);
                    if (loaded != null)
                        _cache = Sanitize(loaded);
                }
            }
            catch (Exception ex)
            {
                // Corrupted, tampered with, or unreadable file: starts over instead of crashing the app.
                App.Log("BatteryHistoryStore", $"Load() failed, starting history over from scratch: {ex.Message}");
            }

            _cache ??= new Dictionary<string, List<BatteryHistoryPoint>>();
            return _cache;
        }

        /// <summary>
        /// The file is read back exactly as it came from disk — it may have been hand-edited or
        /// corrupted. Caps the number of controllers and points per controller before accepting the
        /// data, so an absurdly large file can't become an easy way to blow up the app's memory.
        /// </summary>
        private static Dictionary<string, List<BatteryHistoryPoint>> Sanitize(
            Dictionary<string, List<BatteryHistoryPoint>> data)
        {
            return data
                .Take(MaxTrackedControllers)
                .ToDictionary(
                    kv => kv.Key,
                    kv => (kv.Value ?? new List<BatteryHistoryPoint>())
                        .Where(p => p != null)
                        .OrderBy(p => p.TimestampUtc)
                        .TakeLast(MaxPointsPerController)
                        .ToList());
        }

        private static void Save(Dictionary<string, List<BatteryHistoryPoint>> data)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(data);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                // Write failure (disk full, no permission, etc.): only the most recent point is lost.
                App.Log("BatteryHistoryStore", $"Save() failed: {ex.Message}");
            }
        }
    }
}
