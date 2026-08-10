using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Padlume
{
    /// <summary>
    /// Enables/disables, via SetupAPI/pnputil, the HID device for a specific controller — the same
    /// effect as "Disable device" in Device Manager. This is how controller exclusivity is enforced:
    /// Windows stops delivering input from that controller to any app (games included) while it's
    /// disabled. Requires an elevated process (Administrator).
    /// </summary>
    public static class ControllerDeviceLock
    {
        private static readonly Guid GUID_DEVINTERFACE_HID = new("4D1E55B2-F16F-11CF-88CB-001111000030");

        private const int DIGCF_PRESENT = 0x02;
        private const int DIGCF_DEVICEINTERFACE = 0x10;
        private const uint SPDRP_HARDWAREID = 0x01;

        // The reliable way to identify "this is a game controller" is this compatible ID that Windows
        // itself synthesizes (from the HID report descriptor) and exposes as metadata — no need to open
        // the device. An earlier attempt opened a live handle and called HidD_GetAttributes/
        // HidP_GetCaps, but that query intermittently fails with ERROR_DEVICE_NOT_CONNECTED (1167) for
        // Xbox/XInput controllers when something else (the app's own Windows.Gaming.Input) is already
        // reading the same controller through another channel at the same time — a known quirk of the
        // xinputhid.sys driver. Reading HardwareIds avoids any device I/O.
        private const string GamepadCompatibleId = "HID_DEVICE_SYSTEM_GAME";

        // VID/PID show up in the instance ID in two formats, depending on the bus:
        //   USB / generic:   HID\VID_045E&PID_02FF&IG_00\...
        //   Bluetooth (SDP): HID\{guid}_VID&0002054C_PID&05C4\...  (VID has a 4-hex "source" prefix)
        private static readonly Regex DirectVidPidPattern = new(@"VID_([0-9A-Fa-f]{4}).*?PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);
        private static readonly Regex BluetoothVidPidPattern = new(@"VID&[0-9A-Fa-f]{4}([0-9A-Fa-f]{4}).*?PID&([0-9A-Fa-f]{4})", RegexOptions.Compiled);

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

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, int Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, int MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, int DeviceInterfaceDetailDataSize, ref int RequiredSize, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, StringBuilder DeviceInstanceId, int DeviceInstanceIdSize, out int RequiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        // CharSet must be explicit here: without it the P/Invoke defaults to the ANSI variant
        // (SetupDiGetDeviceRegistryPropertyA), which returns the REG_MULTI_SZ as 1 byte per character —
        // but the code decodes the buffer as UTF-16 (Encoding.Unicode), which scrambles everything and
        // makes the HardwareIds check never match anything.
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property, out uint PropertyRegDataType, byte[]? PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize);

        /// <summary>
        /// Looks, among the HID devices present in the system, for the one matching a game controller
        /// with the given VID/PID. <paramref name="excludeInstanceIds"/> avoids re-finding a device
        /// already assigned to another <see cref="ControllerItem"/> when two identical controllers
        /// (same VID/PID) are plugged in at the same time — in that case the assignment is best-effort
        /// (by enumeration order), since there's no reliable way to tell the two apart.
        /// </summary>
        public static string? TryFindDeviceInstanceId(int vendorId, int productId, ISet<string> excludeInstanceIds)
        {
            var candidates = ScanGamepadCandidates();

            var exact = candidates.FirstOrDefault(c =>
                c.VendorId == vendorId && c.ProductId == productId && !excludeInstanceIds.Contains(c.InstanceId));
            if (exact.InstanceId != null)
            {
                App.Log("DeviceLock", $"TryFindDeviceInstanceId VID={vendorId:X4} PID={productId:X4}: resolved (exact) to {exact.InstanceId}.");
                return exact.InstanceId;
            }

            // Not every controller reports to Windows.Gaming.Input the same Product ID that shows up on
            // the real HID device (Xbox/XInput controllers are the most common case, but not the only
            // one — any manufacturer's compatibility driver can remap this). Without an exact match,
            // falls back to correlating by vendor alone among the not-yet-assigned gamepads.
            var byVendor = candidates.FirstOrDefault(c => c.VendorId == vendorId && !excludeInstanceIds.Contains(c.InstanceId));
            if (byVendor.InstanceId != null)
            {
                App.Log("DeviceLock", $"TryFindDeviceInstanceId VID={vendorId:X4} PID={productId:X4}: resolved (vendor fallback; " +
                    $"device's real PID={byVendor.ProductId:X4}) to {byVendor.InstanceId}.");
                return byVendor.InstanceId;
            }

            App.Log("DeviceLock", $"TryFindDeviceInstanceId VID={vendorId:X4} PID={productId:X4}: NOT resolved " +
                $"(gamepads present: {string.Join(", ", candidates.Select(c => $"{c.VendorId:X4}:{c.ProductId:X4}"))}).");
            return null;
        }

        private readonly struct GamepadCandidate
        {
            public GamepadCandidate(string instanceId, ushort vendorId, ushort productId)
            {
                InstanceId = instanceId;
                VendorId = vendorId;
                ProductId = productId;
            }

            public string InstanceId { get; }
            public ushort VendorId { get; }
            public ushort ProductId { get; }
        }

        /// <summary>
        /// Enumerates every present HID device that's actually a game controller — only reads metadata
        /// already cached by Windows (instance ID + HardwareIds via the registry), without opening a
        /// single handle to the device.
        /// </summary>
        private static List<GamepadCandidate> ScanGamepadCandidates()
        {
            var result = new List<GamepadCandidate>();

            var guid = GUID_DEVINTERFACE_HID;
            IntPtr deviceInfoSet = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (deviceInfoSet == IntPtr.Zero || deviceInfoSet.ToInt64() == -1)
            {
                App.Log("DeviceLock", $"ScanGamepadCandidates: SetupDiGetClassDevs failed (Win32={Marshal.GetLastWin32Error()}).");
                return result;
            }

            try
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                int index = 0;

                while (SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref guid, index, ref interfaceData))
                {
                    index++;

                    if (!TryGetDeviceInfoData(deviceInfoSet, ref interfaceData, out var devInfoData))
                        continue;

                    if (!IsGamepadDevice(deviceInfoSet, ref devInfoData))
                        continue;

                    var instanceId = GetDeviceInstanceId(deviceInfoSet, ref devInfoData);
                    if (instanceId == null || !TryParseVidPid(instanceId, out ushort vid, out ushort pid))
                        continue;

                    result.Add(new GamepadCandidate(instanceId, vid, pid));
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return result;
        }

        private static bool TryGetDeviceInfoData(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData, out SP_DEVINFO_DATA devInfoData)
        {
            devInfoData = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            int requiredSize = 0;

            // The first call, with a null buffer, only exists to find out the detail buffer's size —
            // it always "fails" (returns false) by design in that case, and isn't reliable for
            // populating devInfoData. It's the SECOND call, with a real allocated buffer, that actually
            // populates devInfoData (even though we don't care about the buffer's own contents).
            SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, ref requiredSize, ref devInfoData);
            if (requiredSize == 0)
                return false;

            IntPtr buffer = Marshal.AllocHGlobal(requiredSize);
            try
            {
                // The usual quirk: the variable buffer's cbSize needs to be 8 (x64) or 6 (x86).
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                return SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, buffer, requiredSize, ref requiredSize, ref devInfoData);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string? GetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfoData)
        {
            var sb = new StringBuilder(512);
            return SetupDiGetDeviceInstanceId(deviceInfoSet, ref devInfoData, sb, sb.Capacity, out _) ? sb.ToString() : null;
        }

        private static bool IsGamepadDevice(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfoData)
        {
            var hardwareIds = GetMultiStringProperty(deviceInfoSet, ref devInfoData, SPDRP_HARDWAREID);
            return hardwareIds.Any(id => id.Equals(GamepadCompatibleId, StringComparison.OrdinalIgnoreCase));
        }

        private static string[] GetMultiStringProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfoData, uint property)
        {
            SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devInfoData, property, out _, null, 0, out uint requiredSize);
            if (requiredSize == 0)
                return Array.Empty<string>();

            var buffer = new byte[requiredSize];
            if (!SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devInfoData, property, out _, buffer, requiredSize, out _))
                return Array.Empty<string>();

            // REG_MULTI_SZ: several \0-terminated UTF-16 strings, the whole list ends with \0\0.
            return Encoding.Unicode.GetString(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool TryParseVidPid(string instanceId, out ushort vendorId, out ushort productId)
        {
            var match = DirectVidPidPattern.Match(instanceId);
            if (!match.Success)
                match = BluetoothVidPidPattern.Match(instanceId);

            if (!match.Success)
            {
                vendorId = 0;
                productId = 0;
                return false;
            }

            vendorId = ushort.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            productId = ushort.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
        /// Enables or disables the device by calling pnputil.exe (Windows' own native utility for this)
        /// — more reliable than hand-assembling a SetupDiCallClassInstaller call, which failed with
        /// ERROR_INVALID_DATA (13) for child devices of an XInput composite device.
        /// Returns false on failure (e.g. no Administrator privilege).
        /// </summary>
        public static bool SetEnabled(string deviceInstanceId, bool enabled)
        {
            string action = enabled ? "enable" : "disable";

            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add($"/{action}-device");
            psi.ArgumentList.Add(deviceInstanceId);

            try
            {
                using var process = Process.Start(psi);
                if (process == null)
                {
                    App.Log("DeviceLock", $"SetEnabled({action}, {deviceInstanceId}): couldn't start pnputil.exe.");
                    return false;
                }

                // Starts reading both streams asynchronously BEFORE waiting for the process to exit —
                // reading one to completion synchronously while the other pipe fills up can deadlock
                // both sides forever (the classic .NET Process deadlock when stdout and stderr are both
                // redirected). Combined with the timeout below, this keeps SetEnabled from hanging the
                // UI forever if pnputil.exe never returns — which actually happened another way during
                // this project's development (a DS4 was left with its device disabled when the Padlume
                // process was force-killed during a test, skipping the normal re-enable-on-exit path).
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                bool exited = process.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds);
                if (!exited)
                {
                    App.Log("DeviceLock", $"SetEnabled({action}, {deviceInstanceId}): pnputil.exe didn't respond within 10s, terminating.");
                    try { process.Kill(entireProcessTree: true); } catch { /* may have already exited on its own */ }
                    return false;
                }

                string stdout = stdoutTask.Result;
                string stderr = stderrTask.Result;

                // 0 = success; 3010 = success but a restart is needed (shouldn't happen here, but isn't
                // a failure). Any other code (e.g. 5 = access denied) is a real failure.
                bool ok = process.ExitCode == 0 || process.ExitCode == 3010;
                if (!ok)
                    App.Log("DeviceLock", $"SetEnabled({action}, {deviceInstanceId}): pnputil returned {process.ExitCode}. {stdout}{stderr}".Trim());
                return ok;
            }
            catch (Exception ex)
            {
                App.Log("DeviceLock", $"SetEnabled({action}, {deviceInstanceId}): exception running pnputil.exe: {ex.Message}");
                return false;
            }
        }
    }
}
