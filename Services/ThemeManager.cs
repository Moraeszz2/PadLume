using System;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace Padlume
{
    /// <summary>
    /// Detects Windows' light/dark theme (Settings > Personalization > Colors > "Choose your mode" or
    /// equivalent) and applies the matching resource dictionary (Themes/Dark.xaml or Themes/Light.xaml)
    /// — reacts to changes live, without needing to restart the app.
    /// </summary>
    public static class ThemeManager
    {
        private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string AppsUseLightThemeValue = "AppsUseLightTheme";

        private static bool _watching;

        public static void ApplyCurrentSystemTheme()
        {
            // System.Windows.Application, not System.Windows.Forms.Application — fully qualified
            // because the project references both (WinForms is used only for the tray icon) and
            // "Application" alone is ambiguous between them.
            var resources = System.Windows.Application.Current.Resources;
            var uri = new Uri(IsLightTheme() ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
            var dictionary = new ResourceDictionary { Source = uri };

            // Only swaps the theme dictionary if one was already applied (e.g. Windows switched theme
            // while the app was open) — doesn't touch any other ResourceDictionary that might exist.
            var previous = resources.MergedDictionaries.FirstOrDefault(d => d.Contains("WindowBackgroundBrush"));
            if (previous != null)
                resources.MergedDictionaries.Remove(previous);

            resources.MergedDictionaries.Add(dictionary);

            if (!_watching)
            {
                _watching = true;
                // UserPreferenceCategory.General is the category Windows uses to signal a light/dark
                // theme change (this old API has no dedicated "Theme" category).
                SystemEvents.UserPreferenceChanged += (_, e) =>
                {
                    if (e.Category == UserPreferenceCategory.General)
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(ApplyCurrentSystemTheme);
                };
            }
        }

        private static bool IsLightTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
                return key?.GetValue(AppsUseLightThemeValue) is int value && value != 0;
            }
            catch (Exception ex)
            {
                App.Log("ThemeManager", $"IsLightTheme() failed, assuming dark: {ex.Message}");
                return false;
            }
        }
    }
}
