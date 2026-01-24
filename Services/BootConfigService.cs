using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace LinuxGate.Services
{
    /// <summary>
    /// Service for boot configuration using bcdedit.
    /// </summary>
    public class BootConfigService
    {
        /// <summary>
        /// Result of a boot entry creation operation.
        /// </summary>
        public class BootEntryResult
        {
            public bool Success { get; set; }
            public string Guid { get; set; }
            public string ErrorMessage { get; set; }
        }

        /// <summary>
        /// Gets the path to bcdedit.exe, handling WOW64 redirection.
        /// </summary>
        private string GetBcdeditPath()
        {
            // Use Sysnative to bypass WOW64 redirection if running as 32-bit on 64-bit Windows
            string bcdeditPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative", "bcdedit.exe");

            if (!File.Exists(bcdeditPath))
            {
                bcdeditPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bcdedit.exe");
            }

            return bcdeditPath;
        }

        /// <summary>
        /// Runs a bcdedit command and returns the output.
        /// </summary>
        private async Task<(int exitCode, string output, string error)> RunBcdeditAsync(string arguments)
        {
            string bcdeditPath = GetBcdeditPath();

            return await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = bcdeditPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    return (process.ExitCode, output, error);
                }
            });
        }

        /// <summary>
        /// Creates a boot entry for a bootsector application.
        /// </summary>
        /// <param name="name">Display name for the boot entry.</param>
        /// <param name="logCallback">Optional callback for logging.</param>
        /// <returns>BootEntryResult with the GUID if successful.</returns>
        public async Task<BootEntryResult> CreateBootEntryAsync(string name, Action<string> logCallback = null)
        {
            try
            {
                logCallback?.Invoke($"Using bcdedit at: {GetBcdeditPath()}");

                // Create the boot entry
                var (exitCode, output, error) = await RunBcdeditAsync($"/create /d \"{name}\" /application bootsector");

                logCallback?.Invoke($"bcdedit create output: {output}");
                if (!string.IsNullOrEmpty(error))
                    logCallback?.Invoke($"bcdedit create error: {error}");

                // Find GUID between { and } in the output
                int startIdx = output.IndexOf('{');
                int endIdx = output.IndexOf('}');
                if (startIdx >= 0 && endIdx > startIdx)
                {
                    string guid = output.Substring(startIdx, endIdx - startIdx + 1);
                    logCallback?.Invoke($"Found GUID: {guid}");

                    return new BootEntryResult
                    {
                        Success = true,
                        Guid = guid
                    };
                }
                else
                {
                    return new BootEntryResult
                    {
                        Success = false,
                        ErrorMessage = "Could not find GUID in output"
                    };
                }
            }
            catch (Exception ex)
            {
                return new BootEntryResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Sets the device partition for a boot entry.
        /// </summary>
        /// <param name="guid">Boot entry GUID.</param>
        /// <param name="partition">Partition (e.g., "C:").</param>
        /// <returns>True if successful.</returns>
        public async Task<bool> SetDevicePartitionAsync(string guid, string partition)
        {
            var (exitCode, _, _) = await RunBcdeditAsync($"/set {guid} device partition={partition}");
            return exitCode == 0;
        }

        /// <summary>
        /// Sets the boot path for a boot entry.
        /// </summary>
        /// <param name="guid">Boot entry GUID.</param>
        /// <param name="path">Boot path (e.g., "\\grldr.mbr").</param>
        /// <returns>True if successful.</returns>
        public async Task<bool> SetBootPathAsync(string guid, string path)
        {
            var (exitCode, _, _) = await RunBcdeditAsync($"/set {guid} path {path}");
            return exitCode == 0;
        }

        /// <summary>
        /// Adds a boot entry to the display order.
        /// </summary>
        /// <param name="guid">Boot entry GUID.</param>
        /// <param name="addLast">If true, adds to end; otherwise adds to beginning.</param>
        /// <returns>True if successful.</returns>
        public async Task<bool> AddToBootMenuAsync(string guid, bool addLast = true)
        {
            string position = addLast ? "/addlast" : "/addfirst";
            var (exitCode, _, _) = await RunBcdeditAsync($"/displayorder {guid} {position}");
            return exitCode == 0;
        }

        /// <summary>
        /// Configures a complete GRUB4DOS boot entry.
        /// </summary>
        /// <param name="name">Display name for the boot entry.</param>
        /// <param name="devicePartition">Device partition (e.g., "C:").</param>
        /// <param name="bootPath">Boot path (e.g., "\\grldr.mbr").</param>
        /// <param name="logCallback">Optional callback for logging.</param>
        /// <returns>True if all steps succeeded.</returns>
        public async Task<bool> ConfigureGrub4DosEntryAsync(
            string name,
            string devicePartition,
            string bootPath,
            Action<string> logCallback = null)
        {
            try
            {
                // Step 1: Create the boot entry
                var createResult = await CreateBootEntryAsync(name, logCallback);
                if (!createResult.Success)
                {
                    logCallback?.Invoke($"ERROR: {createResult.ErrorMessage}");
                    return false;
                }

                string guid = createResult.Guid;

                // Wait before next command
                await Task.Delay(1000);

                // Step 2: Set device partition
                bool deviceSet = await SetDevicePartitionAsync(guid, devicePartition);
                logCallback?.Invoke($"bcdedit /set {guid} device partition={devicePartition}: {(deviceSet ? "OK" : "Failed")}");

                await Task.Delay(1000);

                // Step 3: Set path
                bool pathSet = await SetBootPathAsync(guid, bootPath);
                logCallback?.Invoke($"bcdedit /set {guid} path {bootPath}: {(pathSet ? "OK" : "Failed")}");

                await Task.Delay(1000);

                // Step 4: Add to boot menu
                bool menuAdded = await AddToBootMenuAsync(guid);
                logCallback?.Invoke($"bcdedit /displayorder {guid} /addlast: {(menuAdded ? "OK" : "Failed")}");

                logCallback?.Invoke("Boot entry configured successfully");
                return true;
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"Boot configuration failed: {ex.Message}");
                return false;
            }
        }
    }
}
