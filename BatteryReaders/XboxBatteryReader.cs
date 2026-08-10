using System;
using System.Runtime.InteropServices;

namespace Padlume
{
    /// <summary>
    /// Reads an Xbox controller's battery via XInputGetBatteryInformation — Microsoft's official API,
    /// which works over both USB and Bluetooth without touching the raw HID report. Unlike
    /// <see cref="DualShock4BatteryReader"/>, this does NOT try to replicate the exact % Steam shows:
    /// Xbox controllers speak a proprietary protocol (GIP) behind a compatibility driver (xinputhid.sys)
    /// that doesn't allow a reliable raw-report read without swapping the interface driver — which would
    /// break the controller in games while Padlume was running. XInput only reports 4 levels
    /// (empty/low/medium/full), not an exact percentage.
    /// </summary>
    internal static class XboxBatteryReader
    {
        private const ushort MicrosoftVendorId = 0x045E;
        private const byte BatteryDevTypeGamepad = 0x00;
        private const byte BatteryTypeDisconnected = 0x00;
        private const int ErrorSuccess = 0;
        private const int UserSlotCount = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_BATTERY_INFORMATION
        {
            public byte BatteryType;
            public byte BatteryLevel;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern int XInputGetBatteryInformation(int dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION pBatteryInformation);

        public static bool IsXboxController(int vendorId) => vendorId == MicrosoftVendorId;

        /// <summary>
        /// Walks the 4 XInput user slots and returns the battery of the first connected gamepad found.
        /// Only reliable when a single Xbox controller is connected — XInput doesn't expose a VID/PID or
        /// device path, so there's no way to reliably correlate a specific slot to a RawGameController
        /// when more than one Xbox controller is connected at the same time.
        /// </summary>
        public static bool TryReadBattery(out int percent, out bool isCharging)
        {
            percent = 0;
            isCharging = false;

            for (int userIndex = 0; userIndex < UserSlotCount; userIndex++)
            {
                if (XInputGetBatteryInformation(userIndex, BatteryDevTypeGamepad, out var info) != ErrorSuccess)
                    continue;

                if (info.BatteryType == BatteryTypeDisconnected)
                    continue;

                // BatteryLevel: 0=empty, 1=low, 2=medium, 3=full — no exact percentage.
                percent = info.BatteryLevel switch
                {
                    0 => 5,
                    1 => 33,
                    2 => 67,
                    _ => 100,
                };
                isCharging = false; // XInput doesn't report charging state.
                return true;
            }

            return false;
        }
    }
}
