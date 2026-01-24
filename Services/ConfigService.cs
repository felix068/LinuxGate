using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace LinuxGate.Services
{
    /// <summary>
    /// Service for writing configuration files.
    /// </summary>
    public class ConfigService
    {
        /// <summary>
        /// Configuration settings for Linux installation.
        /// </summary>
        public class LinuxConfig
        {
            public string SystemLang { get; set; } = "en_US.UTF-8";
            public string KeyboardLayout { get; set; } = "us";
            public string KeyboardModel { get; set; } = "pc105";
            public string Timezone { get; set; } = "UTC";
            public string Username { get; set; } = "user";
            public string Password { get; set; } = "password";
            public string IsoFilename { get; set; } = "mint.iso";
            public double LinuxSizeGB { get; set; } = 20;
        }

        /// <summary>
        /// Writes the Linux installation configuration to a file.
        /// </summary>
        /// <param name="configPath">Path to the config file.</param>
        /// <param name="config">Configuration settings.</param>
        /// <returns>True if successful.</returns>
        public async Task<bool> WriteConfigAsync(string configPath, LinuxConfig config)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var configLines = new List<string>
                    {
                        $"SYSTEM_LANG=\"{config.SystemLang}\"",
                        $"KEYBOARD_LAYOUT=\"{config.KeyboardLayout}\"",
                        $"KEYBOARD_MODEL=\"{config.KeyboardModel}\"",
                        $"TIMEZONE=\"{config.Timezone}\"",
                        $"USERNAME=\"{config.Username}\"",
                        $"PASSWORD=\"{config.Password}\"",
                        $"ISO_FILENAME=\"{config.IsoFilename}\"",
                        $"LINUX_SIZE_GB=\"{config.LinuxSizeGB:F0}\""
                    };

                    File.WriteAllText(configPath, string.Join("\n", configLines));
                    return true;
                }
                catch
                {
                    return false;
                }
            });
        }

        /// <summary>
        /// Writes the Linux installation configuration to Z:\config.txt.
        /// </summary>
        /// <param name="config">Configuration settings.</param>
        /// <returns>True if successful.</returns>
        public async Task<bool> WriteConfigToFat32Async(LinuxConfig config)
        {
            return await WriteConfigAsync(@"Z:\config.txt", config);
        }

        /// <summary>
        /// Writes custom key-value pairs to a configuration file.
        /// </summary>
        /// <param name="configPath">Path to the config file.</param>
        /// <param name="settings">Dictionary of key-value pairs.</param>
        /// <returns>True if successful.</returns>
        public async Task<bool> WriteCustomConfigAsync(string configPath, Dictionary<string, string> settings)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var configLines = new List<string>();
                    foreach (var kvp in settings)
                    {
                        configLines.Add($"{kvp.Key}=\"{kvp.Value}\"");
                    }

                    File.WriteAllText(configPath, string.Join("\n", configLines));
                    return true;
                }
                catch
                {
                    return false;
                }
            });
        }
    }
}
