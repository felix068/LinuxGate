using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace LinuxGate
{
    public static class Localization
    {
        public static event EventHandler LanguageChanged;

        // Current language code (en, fr, es, ja)
        public static string CurrentLanguage { get; private set; } = "en";

        // Available languages in the app
        private static readonly string[] AvailableLanguages = { "en", "fr", "es", "ja" };

        // Language to Linux locale mapping
        private static readonly Dictionary<string, string> LinuxLocales = new Dictionary<string, string>
        {
            { "en", "en_US.UTF-8" },
            { "fr", "fr_FR.UTF-8" },
            { "es", "es_ES.UTF-8" },
            { "ja", "ja_JP.UTF-8" }
        };

        // Language to keyboard layout mapping
        private static readonly Dictionary<string, string> KeyboardLayouts = new Dictionary<string, string>
        {
            { "en", "us" },
            { "fr", "fr" },
            { "es", "es" },
            { "ja", "jp" }
        };

        // Language to default timezone mapping (fallback if Windows timezone can't be mapped)
        private static readonly Dictionary<string, string> DefaultTimezones = new Dictionary<string, string>
        {
            { "en", "America/New_York" },
            { "fr", "Europe/Paris" },
            { "es", "Europe/Madrid" },
            { "ja", "Asia/Tokyo" }
        };

        public static void SetLanguage(string cultureName)
        {
            // Store the current language
            CurrentLanguage = cultureName;

            // Find and remove the current language dictionary (if it exists)
            ResourceDictionary oldDict = null;
            foreach (ResourceDictionary dict in Application.Current.Resources.MergedDictionaries)
            {
                if (dict.Source != null && dict.Source.OriginalString.StartsWith("/Resources/Lang/Strings."))
                {
                    oldDict = dict;
                    break;
                }
            }

            if (oldDict != null)
            {
                Application.Current.Resources.MergedDictionaries.Remove(oldDict);
            }

            // Add the new language dictionary
            var newDict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/LinuxGate;component/Resources/Lang/Strings.{cultureName}.xaml", UriKind.Absolute)
            };
            Application.Current.Resources.MergedDictionaries.Add(newDict);

            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Get the Windows system language and return the matching app language code
        /// </summary>
        public static string GetWindowsLanguageCode()
        {
            try
            {
                var culture = CultureInfo.CurrentUICulture;
                string twoLetterCode = culture.TwoLetterISOLanguageName.ToLower();

                // Check if we have this language available
                foreach (var lang in AvailableLanguages)
                {
                    if (lang == twoLetterCode)
                        return lang;
                }

                // Default to English if not found
                return "en";
            }
            catch
            {
                return "en";
            }
        }

        /// <summary>
        /// Get the Windows timezone and convert to Linux timezone format
        /// </summary>
        public static string GetWindowsTimezoneAsLinux()
        {
            try
            {
                var windowsZone = TimeZoneInfo.Local;

                // Common Windows to Linux timezone mappings
                var timezoneMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    // Europe
                    { "Romance Standard Time", "Europe/Paris" },
                    { "W. Europe Standard Time", "Europe/Berlin" },
                    { "Central European Standard Time", "Europe/Budapest" },
                    { "GMT Standard Time", "Europe/London" },
                    { "Central Europe Standard Time", "Europe/Prague" },
                    { "E. Europe Standard Time", "Europe/Bucharest" },
                    { "Russian Standard Time", "Europe/Moscow" },

                    // Americas
                    { "Eastern Standard Time", "America/New_York" },
                    { "Central Standard Time", "America/Chicago" },
                    { "Mountain Standard Time", "America/Denver" },
                    { "Pacific Standard Time", "America/Los_Angeles" },
                    { "Atlantic Standard Time", "America/Halifax" },
                    { "US Eastern Standard Time", "America/Indianapolis" },
                    { "SA Pacific Standard Time", "America/Bogota" },
                    { "SA Eastern Standard Time", "America/Buenos_Aires" },
                    { "E. South America Standard Time", "America/Sao_Paulo" },
                    { "Central Standard Time (Mexico)", "America/Mexico_City" },

                    // Asia
                    { "Tokyo Standard Time", "Asia/Tokyo" },
                    { "China Standard Time", "Asia/Shanghai" },
                    { "Korea Standard Time", "Asia/Seoul" },
                    { "Singapore Standard Time", "Asia/Singapore" },
                    { "India Standard Time", "Asia/Kolkata" },
                    { "SE Asia Standard Time", "Asia/Bangkok" },
                    { "Arabian Standard Time", "Asia/Dubai" },

                    // Oceania
                    { "AUS Eastern Standard Time", "Australia/Sydney" },
                    { "New Zealand Standard Time", "Pacific/Auckland" },

                    // UTC
                    { "UTC", "UTC" },
                    { "Coordinated Universal Time", "UTC" }
                };

                if (timezoneMap.TryGetValue(windowsZone.Id, out string linuxZone))
                {
                    return linuxZone;
                }

                // Fallback: use default based on language
                return DefaultTimezones.TryGetValue(CurrentLanguage, out string defaultZone) ? defaultZone : "UTC";
            }
            catch
            {
                return "UTC";
            }
        }

        /// <summary>
        /// Get Linux locale for current language
        /// </summary>
        public static string GetLinuxLocale()
        {
            return LinuxLocales.TryGetValue(CurrentLanguage, out string locale) ? locale : "en_US.UTF-8";
        }

        /// <summary>
        /// Get keyboard layout for current language
        /// </summary>
        public static string GetKeyboardLayout()
        {
            return KeyboardLayouts.TryGetValue(CurrentLanguage, out string layout) ? layout : "us";
        }
    }
}
