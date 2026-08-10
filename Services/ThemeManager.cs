using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace Padlume
{
    public enum ThemePreference
    {
        Auto,
        Light,
        Dark,
    }

    /// <summary>
    /// Applies the app's light/dark resource dictionary (Themes/Dark.xaml or Themes/Light.xaml). In
    /// <see cref="ThemePreference.Auto"/> (the default), follows Windows' own setting (Settings >
    /// Personalization > Colors > "Choose your mode") live, without needing to restart the app; Light or
    /// Dark pins the theme regardless of what Windows is set to.
    /// </summary>
    public static class ThemeManager
    {
        private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string AppsUseLightThemeValue = "AppsUseLightTheme";

        private static readonly string PreferenceFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Padlume", "theme.txt");

        private static bool _watching;

        public static ThemePreference CurrentPreference { get; private set; } = ThemePreference.Auto;

        /// <summary>Loads the saved preference and applies it — call once at startup, before the first window shows.</summary>
        public static void ApplyTheme()
        {
            CurrentPreference = LoadPreference();
            ApplyCurrentPreference();

            if (!_watching)
            {
                _watching = true;
                // UserPreferenceCategory.General is the category Windows uses to signal a light/dark
                // theme change (this old API has no dedicated "Theme" category). Only matters in Auto —
                // Light/Dark is a deliberate pin the user made, Windows switching around it shouldn't
                // silently override that choice.
                SystemEvents.UserPreferenceChanged += (_, e) =>
                {
                    if (e.Category == UserPreferenceCategory.General && CurrentPreference == ThemePreference.Auto)
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(ApplyCurrentPreference);
                };
            }
        }

        /// <summary>Changes and persists the preference, then applies it immediately.</summary>
        public static void SetPreference(ThemePreference preference)
        {
            CurrentPreference = preference;
            SavePreference(preference);
            ApplyCurrentPreference();
        }

        private static void ApplyCurrentPreference()
        {
            bool useLight = CurrentPreference switch
            {
                ThemePreference.Light => true,
                ThemePreference.Dark => false,
                _ => IsSystemLightTheme(),
            };

            // System.Windows.Application, not System.Windows.Forms.Application — fully qualified
            // because the project references both (WinForms is used only for the tray icon) and
            // "Application" alone is ambiguous between them.
            var resources = System.Windows.Application.Current.Resources;
            var uri = new Uri(useLight ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
            var dictionary = new ResourceDictionary { Source = uri };

            // Only swaps the theme dictionary if one was already applied (e.g. re-entering this method
            // after a preference change or a live Windows theme switch) — doesn't touch any other
            // ResourceDictionary that might exist.
            var previous = resources.MergedDictionaries.FirstOrDefault(d => d.Contains("WindowBackgroundBrush"));
            if (previous != null)
                resources.MergedDictionaries.Remove(previous);

            resources.MergedDictionaries.Add(dictionary);
        }

        private static bool IsSystemLightTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
                return key?.GetValue(AppsUseLightThemeValue) is int value && value != 0;
            }
            catch (Exception ex)
            {
                App.Log("ThemeManager", $"IsSystemLightTheme() failed, assuming dark: {ex.Message}");
                return false;
            }
        }

        private static ThemePreference LoadPreference()
        {
            try
            {
                if (File.Exists(PreferenceFilePath))
                {
                    var content = File.ReadAllText(PreferenceFilePath).Trim();
                    if (content.Equals("light", StringComparison.OrdinalIgnoreCase))
                        return ThemePreference.Light;
                    if (content.Equals("dark", StringComparison.OrdinalIgnoreCase))
                        return ThemePreference.Dark;
                }
            }
            catch (Exception ex)
            {
                App.Log("ThemeManager", $"LoadPreference() failed, defaulting to Auto: {ex.Message}");
            }
            return ThemePreference.Auto;
        }

        private static void SavePreference(ThemePreference preference)
        {
            try
            {
                var dir = Path.GetDirectoryName(PreferenceFilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var text = preference switch
                {
                    ThemePreference.Light => "light",
                    ThemePreference.Dark => "dark",
                    _ => "auto",
                };
                File.WriteAllText(PreferenceFilePath, text);
            }
            catch (Exception ex)
            {
                App.Log("ThemeManager", $"SavePreference({preference}) failed: {ex.Message}");
            }
        }
    }
}
