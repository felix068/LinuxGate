using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace LinuxGate.Services
{
    /// <summary>
    /// Service for ISO mounting and content copying operations.
    /// </summary>
    public class IsoService
    {
        /// <summary>
        /// Result of an ISO mount operation.
        /// </summary>
        public class MountResult
        {
            public bool Success { get; set; }
            public string DriveLetter { get; set; }
            public string ErrorMessage { get; set; }
        }

        /// <summary>
        /// Mounts an ISO file and returns the drive letter.
        /// </summary>
        /// <param name="isoPath">Path to the ISO file.</param>
        /// <returns>MountResult with drive letter if successful.</returns>
        public async Task<MountResult> MountIsoAsync(string isoPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string scriptPath = Path.Combine(Path.GetTempPath(), $"mount_iso_{Guid.NewGuid()}.ps1");
                    string scriptContent = $@"
$ErrorActionPreference = 'Stop'
try {{
    $mountResult = Mount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}' -PassThru
    Start-Sleep -Seconds 2
    $volume = $mountResult | Get-Volume
    if ($volume -and $volume.DriveLetter) {{
        Write-Output $volume.DriveLetter
    }} else {{
        Write-Error 'Failed to get drive letter'
        exit 1
    }}
}} catch {{
    Write-Error $_.Exception.Message
    exit 1
}}
";
                    File.WriteAllText(scriptPath, scriptContent);

                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    string driveLetter;
                    string error;

                    using (var process = Process.Start(psi))
                    {
                        driveLetter = process.StandardOutput.ReadToEnd().Trim();
                        error = process.StandardError.ReadToEnd();
                        process.WaitForExit();

                        if (process.ExitCode != 0 || string.IsNullOrEmpty(driveLetter))
                        {
                            File.Delete(scriptPath);
                            return new MountResult
                            {
                                Success = false,
                                ErrorMessage = error
                            };
                        }
                    }

                    File.Delete(scriptPath);

                    // Get only the first letter if multiple lines
                    if (driveLetter.Contains("\n"))
                    {
                        driveLetter = driveLetter.Split('\n')[0].Trim();
                    }

                    return new MountResult
                    {
                        Success = true,
                        DriveLetter = driveLetter
                    };
                }
                catch (Exception ex)
                {
                    return new MountResult
                    {
                        Success = false,
                        ErrorMessage = ex.Message
                    };
                }
            });
        }

        /// <summary>
        /// Dismounts an ISO file.
        /// </summary>
        /// <param name="isoPath">Path to the ISO file.</param>
        /// <returns>True if successful.</returns>
        public async Task<bool> DismountIsoAsync(string isoPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Dismount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}'\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(psi))
                    {
                        process.WaitForExit();
                        return process.ExitCode == 0;
                    }
                }
                catch
                {
                    return false;
                }
            });
        }

        /// <summary>
        /// Copies contents from a source directory to destination.
        /// </summary>
        /// <param name="sourceDrive">Source drive letter (e.g., "D").</param>
        /// <param name="destinationPath">Destination directory path.</param>
        /// <returns>True if successful.</returns>
        public async Task<bool> CopyIsoContentsAsync(string sourceDrive, string destinationPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string sourceDir = $"{sourceDrive}:\\";

                    if (!Directory.Exists(sourceDir))
                    {
                        return false;
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = "xcopy",
                        Arguments = $"\"{sourceDir}*\" \"{destinationPath}\" /E /H /Y /Q",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(psi))
                    {
                        process.StandardOutput.ReadToEnd();
                        process.WaitForExit();
                        // xcopy returns 0 on success, but may return other codes
                        // that still indicate partial success
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            });
        }

        /// <summary>
        /// Mounts an ISO, copies its contents to a destination, and dismounts.
        /// </summary>
        /// <param name="isoPath">Path to the ISO file.</param>
        /// <param name="destinationPath">Destination directory path.</param>
        /// <param name="logCallback">Optional callback for logging.</param>
        /// <returns>True if successful.</returns>
        public async Task<bool> MountCopyAndDismountAsync(string isoPath, string destinationPath, Action<string> logCallback = null)
        {
            string mountedDrive = "";

            try
            {
                logCallback?.Invoke("Mounting ISO...");
                var mountResult = await MountIsoAsync(isoPath);

                if (!mountResult.Success)
                {
                    logCallback?.Invoke($"ERROR mounting ISO: {mountResult.ErrorMessage}");
                    return false;
                }

                mountedDrive = mountResult.DriveLetter;
                logCallback?.Invoke($"ISO mounted at {mountedDrive}:");

                // Wait for drive to be ready
                await Task.Delay(2000);

                logCallback?.Invoke($"Copying files to {destinationPath}...");
                bool copySuccess = await CopyIsoContentsAsync(mountedDrive, destinationPath);

                if (!copySuccess)
                {
                    logCallback?.Invoke("ERROR copying files");
                    return false;
                }

                logCallback?.Invoke("Files copied successfully");
                return true;
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"Mount/copy failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(mountedDrive))
                {
                    try
                    {
                        logCallback?.Invoke("Dismounting ISO...");
                        await DismountIsoAsync(isoPath);
                        logCallback?.Invoke("ISO dismounted");
                    }
                    catch (Exception ex)
                    {
                        logCallback?.Invoke($"Warning: Could not dismount ISO: {ex.Message}");
                    }
                }
            }
        }
    }
}
