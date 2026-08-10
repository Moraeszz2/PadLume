using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace Padlume
{
    /// <summary>
    /// Picks the controller's PNG photo (Assets folder, next to the .exe) by brand/model detected via
    /// VID/PID or by the name Windows reports. No file is required: if the expected PNG doesn't exist,
    /// returns null and the caller falls back to drawing the generic vector icon.
    /// </summary>
    public static class ControllerImageResolver
    {
        private const ushort VendorMicrosoft = 0x045E;
        private const ushort VendorSony = 0x054C;
        private const ushort VendorNintendo = 0x057E;
        private const ushort ProductDualSense = 0x0CE6;

        public static BitmapImage? Resolve(string displayName, ushort vendorId, ushort productId)
        {
            var fileName = PickFileName(displayName, vendorId, productId);
            return LoadIfExists(fileName) ?? LoadIfExists("generic.png");
        }

        private static string PickFileName(string displayName, ushort vendorId, ushort productId)
        {
            var name = displayName.ToLowerInvariant();

            if (vendorId == VendorMicrosoft || name.Contains("xbox"))
                return "xbox.png";

            if (vendorId == VendorSony || name.Contains("dualsense") || name.Contains("dualshock") || name.Contains("playstation"))
                return name.Contains("dualsense") || productId == ProductDualSense ? "dualsense.png" : "dualshock.png";

            if (vendorId == VendorNintendo || name.Contains("nintendo") || name.Contains("joy-con") || name.Contains("pro controller"))
                return "nintendo.png";

            return "generic.png";
        }

        private static BitmapImage? LoadIfExists(string fileName)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
                if (!File.Exists(path))
                    return null;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                // Invalid/corrupted PNG: falls back to the vector icon instead of crashing the app.
                App.Log("ControllerImageResolver", $"Failed loading {fileName}: {ex.Message}");
                return null;
            }
        }
    }
}
