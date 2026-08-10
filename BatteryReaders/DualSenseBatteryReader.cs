using System;

namespace Padlume
{
    /// <summary>
    /// Reads the DualSense (PS5) battery directly from Sony's proprietary HID report — same idea as
    /// <see cref="DualShock4BatteryReader"/>, different format. Based on the Linux kernel's
    /// hid-playstation driver, the most commonly used public reference for this format.
    /// </summary>
    /// <remarks>
    /// Unlike DualShock4BatteryReader, this was NOT validated against real hardware (no DualSense was
    /// available to test with) — only the DS4 was confirmed live. If the percentage/charging state comes
    /// out wrong on a real DualSense, the offset/format below is the first place to review.
    /// </remarks>
    public static class DualSenseBatteryReader
    {
        private const ushort SonyVendorId = 0x054C;
        private static readonly ushort[] ProductIds = { 0x0CE6, 0x0DF2 }; // standard and Edge

        // Report ID 0x01: byte 53 ("status") carries the battery level in the low 4 bits and the
        // charging state in the high 4 bits (0=discharging, 1=charging, 2=full,
        // 0xA/0xB/0xF=error) — a different layout from the DS4, which uses just a single
        // charging-or-not bit.
        private const int StatusByteOffset = 53;

        private const byte ChargingStateDischarging = 0x0;
        private const byte ChargingStateCharging = 0x1;
        private const byte ChargingStateFull = 0x2;

        public static bool IsDualSense(int vendorId, int productId) =>
            vendorId == SonyVendorId && Array.IndexOf(ProductIds, unchecked((ushort)productId)) >= 0;

        /// <summary>Tries to read battery/charging state from a USB-connected DualSense. False if not found, not a DualSense, or the status reports an error.</summary>
        public static bool TryReadUsbBattery(out int percent, out bool isCharging)
        {
            percent = 0;
            isCharging = false;

            if (!RawUsbHidReport.TryReadReport(SonyVendorId, ProductIds, bluetooth: false, out var report) || report.Length <= StatusByteOffset)
                return false;

            byte status = report[StatusByteOffset];
            int level = status & 0x0F;
            int chargingState = (status >> 4) & 0x0F;

            switch (chargingState)
            {
                case ChargingStateFull:
                    percent = 100;
                    isCharging = false;
                    return true;
                case ChargingStateCharging:
                    isCharging = true;
                    percent = Math.Min(100, level * 10 + 5);
                    return true;
                case ChargingStateDischarging:
                    isCharging = false;
                    percent = Math.Min(100, level * 10 + 5);
                    return true;
                default:
                    // Error states (voltage/temperature out of range) — the level can't be trusted.
                    return false;
            }
        }
    }
}
