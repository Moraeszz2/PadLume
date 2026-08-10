using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Padlume
{
    /// <summary>
    /// Shared plumbing for finding an HID device (USB or Bluetooth) by VID/PID and reading its raw input
    /// report (via ReadFile, not HidD_GetInputReport — many controllers only push data through the
    /// interrupt endpoint and time out on an "on demand" request). Used by the proprietary battery
    /// readers (<see cref="DualShock4BatteryReader"/>, <see cref="DualSenseBatteryReader"/>) that need to
    /// parse the report by hand because Windows doesn't expose this information through generic means.
    /// </summary>
    internal static class RawUsbHidReport
    {
        private static readonly Guid GUID_DEVINTERFACE_HID = new("4D1E55B2-F16F-11CF-88CB-001111000030");
        private const int DIGCF_PRESENT = 0x02;
        private const int DIGCF_DEVICEINTERFACE = 0x10;
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        private const int HIDP_STATUS_SUCCESS = 0x00110000;

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, int Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, int MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, int DeviceInterfaceDetailDataSize, ref int RequiredSize, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

        /// <summary>
        /// Finds the first HID device (USB or Bluetooth, per <paramref name="bluetooth"/>) with the
        /// given VID and a PID among <paramref name="productIds"/>, and reads the next input report it
        /// sends. The buffer size is discovered dynamically (via HidP_GetCaps) — ReadFile requires the
        /// exact size the driver expects, not just a "big enough" value.
        /// </summary>
        public static bool TryReadReport(ushort vendorId, ushort[] productIds, bool bluetooth, out byte[] report)
        {
            report = Array.Empty<byte>();

            var guid = GUID_DEVINTERFACE_HID;
            IntPtr deviceInfoSet = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (deviceInfoSet == IntPtr.Zero || deviceInfoSet.ToInt64() == -1)
                return false;

            try
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                int index = 0;

                while (SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref guid, index, ref interfaceData))
                {
                    index++;

                    string? path = GetDeviceInterfacePath(deviceInfoSet, ref interfaceData);
                    if (path == null || IsBluetoothStylePath(path) != bluetooth)
                        continue;

                    if (TryReadFromPath(path, vendorId, productIds, bluetooth, out report))
                        return true;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return false;
        }

        /// <summary>
        /// Every HID interface path ends with the interface class GUID in braces (same for USB and
        /// Bluetooth) — that can't be used to tell them apart. What differs is the FIRST segment
        /// (between the 1st and 2nd '#'): on Bluetooth it includes the SDP service GUID in braces; on
        /// USB it's plain (vid_xxxx&pid_yyyy&...).
        /// </summary>
        private static bool IsBluetoothStylePath(string path)
        {
            int firstHash = path.IndexOf('#');
            int secondHash = firstHash >= 0 ? path.IndexOf('#', firstHash + 1) : -1;
            if (firstHash < 0 || secondHash < 0)
                return false;
            return path.IndexOf('{', firstHash, secondHash - firstHash) >= 0;
        }

        private static bool TryReadFromPath(string devicePath, ushort vendorId, ushort[] productIds, bool bluetooth, out byte[] report)
        {
            report = Array.Empty<byte>();

            uint access = bluetooth ? (GENERIC_READ | 0x40000000u /* GENERIC_WRITE, for HidD_GetFeature */) : GENERIC_READ;
            using var handle = CreateFile(devicePath, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                return false;

            var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(handle, ref attributes) || attributes.VendorID != vendorId || Array.IndexOf(productIds, attributes.ProductID) < 0)
                return false;

            if (!HidD_GetPreparsedData(handle, out var preparsedData))
                return false;

            int inputReportLength, featureReportLength;
            try
            {
                if (HidP_GetCaps(preparsedData, out var caps) != HIDP_STATUS_SUCCESS || caps.InputReportByteLength == 0)
                    return false;

                inputReportLength = caps.InputReportByteLength;
                featureReportLength = caps.FeatureReportByteLength;
            }
            finally
            {
                HidD_FreePreparsedData(preparsedData);
            }

            if (bluetooth && featureReportLength > 0)
            {
                // Some controllers only start sending the "extended" input report (with
                // battery/touchpad/gyro) after the host reads this calibration report — a side effect
                // documented by several third-party implementations, with no official "switch mode"
                // command. Ignores failure: in many cases the device is already in extended mode anyway
                // (Windows has likely already made this same request before, for another reason).
                var featureBuffer = new byte[featureReportLength];
                featureBuffer[0] = 0x02;
                HidD_GetFeature(handle, featureBuffer, featureBuffer.Length);
            }

            // Many controllers only push reports through the interrupt endpoint and don't respond to an
            // "on demand" request (HidD_GetInputReport times out on them, confirmed on the DS4) —
            // ReadFile grabs the next report the device sends, almost instantly.
            var buffer = new byte[inputReportLength];
            if (!ReadFile(handle, buffer, buffer.Length, out _, IntPtr.Zero))
                return false;

            report = buffer;
            return true;
        }

        private static string? GetDeviceInterfacePath(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData)
        {
            var devInfoData = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            int requiredSize = 0;
            SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, ref requiredSize, ref devInfoData);
            if (requiredSize == 0)
                return null;

            IntPtr buffer = Marshal.AllocHGlobal(requiredSize);
            try
            {
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, buffer, requiredSize, ref requiredSize, ref devInfoData))
                    return null;

                return Marshal.PtrToStringAuto(buffer + 4);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
