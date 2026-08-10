using System;

namespace Padlume
{
    /// <summary>
    /// Reads the DualShock 4's battery directly from Sony's proprietary HID report — the same mechanism
    /// Steam uses. Necessary because neither RawGameController nor the paired Bluetooth device's battery
    /// property are reliable for this controller (over USB Windows exposes no battery at all; over
    /// Bluetooth the cached property doesn't always update).
    /// </summary>
    public static class DualShock4BatteryReader
    {
        private const ushort SonyVendorId = 0x054C;
        private static readonly ushort[] ProductIds = { 0x05C4, 0x09CC }; // v1 (CUH-ZCT1x) and v2 (CUH-ZCT2x)

        // USB, report ID 0x01: byte 30 carries the battery level in the low 4 bits and the charging
        // state in bit 4. Confirmed live (level=7, charging=true produced byte 0x17).
        private const int UsbBatteryByteOffset = 30;

        // Bluetooth, report ID 0x11 (the "extended" report, with touchpad/gyro/battery — the DS4 sends
        // this by default as soon as something reads the calibration report, see
        // RawUsbHidReport.TryReadFromPath): same bit layout as USB, just two bytes further in.
        // Confirmed live (byte stable at 0x03 across two reads, while surrounding gyro/timestamp data
        // changed — level 3, discharging, consistent with the controller running on battery alone
        // during the test).
        private const int BluetoothBatteryByteOffset = 32;
        private const byte BluetoothReportId = 0x11;

        public static bool IsDualShock4(int vendorId, int productId) =>
            vendorId == SonyVendorId && Array.IndexOf(ProductIds, unchecked((ushort)productId)) >= 0;

        /// <summary>
        /// Tries to read the DS4's battery/charging state, first over USB and then over Bluetooth.
        /// <paramref name="isBluetooth"/> reports which of the two actually answered — the only reliable
        /// way to know the real transport, since detection by paired-Bluetooth-device name
        /// (<see cref="MainWindow.TryGetBluetoothInfoAsync"/>) can fail due to a name mismatch and
        /// mislabel a Bluetooth-connected DS4 as "USB".
        /// </summary>
        public static bool TryReadBattery(out int percent, out bool isCharging, out bool isBluetooth)
        {
            if (TryReadUsbBattery(out percent, out isCharging))
            {
                isBluetooth = false;
                return true;
            }

            isBluetooth = true;
            return TryReadBluetoothBattery(out percent, out isCharging);
        }

        public static bool TryReadUsbBattery(out int percent, out bool isCharging)
        {
            percent = 0;
            isCharging = false;

            if (!RawUsbHidReport.TryReadReport(SonyVendorId, ProductIds, bluetooth: false, out var report) || report.Length <= UsbBatteryByteOffset)
                return false;

            ParseBatteryByte(report[UsbBatteryByteOffset], out percent, out isCharging);
            return true;
        }

        public static bool TryReadBluetoothBattery(out int percent, out bool isCharging)
        {
            percent = 0;
            isCharging = false;

            if (!RawUsbHidReport.TryReadReport(SonyVendorId, ProductIds, bluetooth: true, out var report) ||
                report.Length <= BluetoothBatteryByteOffset || report[0] != BluetoothReportId)
                return false;

            ParseBatteryByte(report[BluetoothBatteryByteOffset], out percent, out isCharging);
            return true;
        }

        private static void ParseBatteryByte(byte batteryByte, out int percent, out bool isCharging)
        {
            int level = batteryByte & 0x0F;
            isCharging = (batteryByte & 0x10) != 0;

            // Same formula whether charging or not (level*10+5), matching the Linux kernel's hid-sony
            // driver that Steam uses as a reference. An earlier version added +1 to the level while
            // charging — it looked right in an isolated test, but compared side by side with Steam
            // (level 0, charging: Steam showed 5%, the old formula gave 10%) it became clear that was
            // wrong. Level > 10 (11-15) only happens while charging and means "fully charged".
            percent = level > 10 ? 100 : Math.Min(100, level * 10 + 5);
        }
    }
}
