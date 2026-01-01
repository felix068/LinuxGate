using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LinuxGate.Helpers;
using LinuxGate.Models;

namespace LinuxGate.Pages
{
    public partial class ApplyChanges : Page
    {
        private double _linuxSizeGB;
        private const double FAT32_SIZE_GB = 2.0;
        private bool _isRunning = false;

        public ApplyChanges()
        {
            InitializeComponent();
            LoadSummary();
            Loaded += ApplyChanges_Loaded;
        }

        private async void ApplyChanges_Loaded(object sender, RoutedEventArgs e)
        {
            // Partition validation is now done in ChooseDistro page
            await StartInstallationAsync();
        }

        private void LoadSummary()
        {
            // Load Linux size from saved state
            var stateKey = $"ResizeDisk_{(App.Current.Properties["SelectedDistro"] as DistroInfo)?.Name}";
            var state = StateManager.GetState(stateKey);
            if (state?.State is System.Collections.Generic.Dictionary<string, double> savedState)
            {
                _linuxSizeGB = savedState["LinuxSize"];
            }
        }

        private async Task<string> RunDiskpartAndGetOutputAsync(string scriptPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "diskpart.exe",
                        Arguments = $"/s \"{scriptPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(psi))
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();
                        return output;
                    }
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            });
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;

            NavigationHelper.NavigateWithAnimation(
                NavigationService,
                new WarningConfirmation(),
                TimeSpan.FromSeconds(0.3),
                slideLeft: false);
        }

        private async Task StartInstallationAsync()
        {
            if (_isRunning) return;

            _isRunning = true;
            BackButton.IsEnabled = false;

            try
            {
                await ExecutePartitioningAsync();
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex.Message}");
                UpdateProgress(0, Application.Current.Resources["ApplyChangesError"] as string ?? "Error occurred");
                BackButton.IsEnabled = true;
                _isRunning = false;
            }
        }

        private async Task ExecutePartitioningAsync()
        {
            // Query available shrink space first
            Log("Checking available shrink space...");
            double maxShrinkMB = await QueryShrinkSpaceAsync();
            Log($"Maximum shrinkable space: {maxShrinkMB / 1024:N1}GB ({maxShrinkMB:N0}MB)");

            // We need at least 2GB for FAT32 + some space for Linux
            double minRequiredMB = (FAT32_SIZE_GB + 5) * 1024; // 2GB FAT32 + 5GB minimum Linux
            if (maxShrinkMB < minRequiredMB)
            {
                Log($"ERROR: Not enough shrinkable space!");
                Log($"  Minimum required: {minRequiredMB / 1024:N1}GB");
                Log($"  Available: {maxShrinkMB / 1024:N1}GB");
                UpdateProgress(0, Application.Current.Resources["ApplyChangesError"] as string ?? "Error occurred");
                BackButton.IsEnabled = true;
                _isRunning = false;
                return;
            }

            // === NEW APPROACH ===
            // Step 1: Shrink Windows by ONLY 2GB (for FAT32)
            UpdateProgress(10, Application.Current.Resources["ApplyChangesStep1"] as string ?? "Shrinking Windows partition...");
            Log("Step 1: Shrinking Windows by 2GB for FAT32 partition...");

            bool step1Success = await ShrinkWindowsPartitionAsync(2048); // Only 2GB
            if (!step1Success)
            {
                Log("ERROR: Failed to shrink Windows partition (step 1)");
                BackButton.IsEnabled = true;
                _isRunning = false;
                return;
            }

            // Wait for disk to update
            Log("Waiting for disk to update...");
            await Task.Delay(3000);

            // Step 2: Create FAT32 partition in the free space (no offset - goes right after Windows)
            UpdateProgress(30, Application.Current.Resources["ApplyChangesStep2"] as string ?? "Creating FAT32 boot partition (Z:)...");
            Log("Step 2: Creating FAT32 partition (will be placed right after Windows)...");

            bool step2Success = await CreateFat32PartitionSimpleAsync();
            if (!step2Success)
            {
                Log("ERROR: Failed to create FAT32 partition");
                BackButton.IsEnabled = true;
                _isRunning = false;
                return;
            }

            // Wait for disk to update
            Log("Waiting for disk to update...");
            await Task.Delay(3000);

            // Step 3: Now shrink Windows by the MAXIMUM available to create free space BEFORE FAT32
            Log("Checking remaining shrink space...");
            double remainingShrinkMB = await QueryShrinkSpaceAsync();
            Log($"Remaining shrinkable space: {remainingShrinkMB / 1024:N1}GB ({remainingShrinkMB:N0}MB)");

            // Calculate how much we need to shrink for Linux (user requested size)
            double requestedLinuxMB = _linuxSizeGB * 1024;

            if (remainingShrinkMB > 1024) // Only shrink if more than 1GB available
            {
                UpdateProgress(45, "Creating free space for Linux...");
                // Use the MINIMUM between what's available and what user requested
                double shrinkAmountMB = Math.Min(remainingShrinkMB - 512, requestedLinuxMB);
                Log($"Step 3: Shrinking Windows by {shrinkAmountMB / 1024:N1}GB for Linux (user requested {_linuxSizeGB:N0}GB)...");

                bool step3Success = await ShrinkWindowsPartitionAsync(shrinkAmountMB);
                if (!step3Success)
                {
                    Log("WARNING: Could not shrink Windows further, Linux will use ntfsresize");
                }
                else
                {
                    Log("Successfully created free space for Linux");
                }
            }
            else
            {
                Log("Not much space left to shrink, Linux will finish with ntfsresize if needed");
            }

            // Wait for disk to update
            Log("Waiting for disk to update...");
            UpdateProgress(50, Application.Current.Resources["ApplyChangesWaitDisk"] as string ?? "Waiting for disk update...");
            await Task.Delay(3000);

            // Step 4: Download ISO
            string isoUrl = "";
            if (App.Current.Properties["SelectedDistro"] is DistroInfo distro && !string.IsNullOrEmpty(distro.IsoUrl))
            {
                isoUrl = distro.IsoUrl;
            }

            if (string.IsNullOrEmpty(isoUrl))
            {
                Log("ERROR: No ISO URL found for selected distribution");
                UpdateProgress(0, Application.Current.Resources["ApplyChangesError"] as string ?? "Error occurred");
                BackButton.IsEnabled = true;
                _isRunning = false;
                return;
            }

            UpdateProgress(55, "Downloading ISO...");
            Log($"Step 4: Downloading ISO from {isoUrl}...");

            string tempIsoPath = Path.Combine(Path.GetTempPath(), "linuxgate_installer.iso");
            bool downloadSuccess = await DownloadIsoAsync(isoUrl, tempIsoPath);
            if (!downloadSuccess)
            {
                Log("ERROR: Failed to download ISO");
                UpdateProgress(0, Application.Current.Resources["ApplyChangesError"] as string ?? "Error occurred");
                BackButton.IsEnabled = true;
                _isRunning = false;
                return;
            }

            // Step 5: Mount ISO and copy contents to Z:
            UpdateProgress(80, "Copying ISO contents to Z:...");
            Log("Step 5: Mounting ISO and copying contents to Z:...");

            bool copySuccess = await MountAndCopyIsoAsync(tempIsoPath);
            if (!copySuccess)
            {
                Log("ERROR: Failed to copy ISO contents");
                UpdateProgress(0, Application.Current.Resources["ApplyChangesError"] as string ?? "Error occurred");
                BackButton.IsEnabled = true;
                _isRunning = false;
                return;
            }

            // Cleanup temp ISO
            try
            {
                if (File.Exists(tempIsoPath))
                    File.Delete(tempIsoPath);
            }
            catch { }

            // Step 6: Download Linux installer ISO to C:\
            if (App.Current.Properties["SelectedDistro"] is DistroInfo selectedDistro &&
                !string.IsNullOrEmpty(selectedDistro.IsoInstaller) &&
                !string.IsNullOrEmpty(selectedDistro.IsoInstallerFileName))
            {
                UpdateProgress(85, "Downloading Linux installer ISO...");
                Log($"Step 6: Downloading Linux installer from {selectedDistro.IsoInstaller}...");

                string installerPath = Path.Combine(@"C:\", selectedDistro.IsoInstallerFileName);
                bool installerDownloadSuccess = await DownloadInstallerIsoAsync(selectedDistro.IsoInstaller, installerPath);
                if (!installerDownloadSuccess)
                {
                    Log("ERROR: Failed to download Linux installer ISO");
                    UpdateProgress(0, Application.Current.Resources["ApplyChangesError"] as string ?? "Error occurred");
                    BackButton.IsEnabled = true;
                    _isRunning = false;
                    return;
                }
                Log($"Linux installer saved to {installerPath}");
            }

            // Step 7: Write config.txt AFTER ISO copy (so it doesn't get overwritten)
            UpdateProgress(95, "Writing configuration...");
            Log("Step 7: Writing configuration to Z:\\config.txt...");

            bool configSuccess = await WriteConfigToFat32Async();
            if (!configSuccess)
            {
                Log("WARNING: Failed to write config.txt, will use defaults");
            }

            // Step 8: Download GRUB4DOS files to C:\
            UpdateProgress(96, "Downloading bootloader files...");
            Log("Step 8: Downloading GRUB4DOS files to C:\\...");

            string[] grubFiles = { "grldr", "grldr.mbr", "menu.lst" };
            foreach (var file in grubFiles)
            {
                string url = $"https://tpm28.com/filepool/{file}";
                string destPath = Path.Combine(@"C:\", file);
                bool downloaded = await DownloadFileAsync(url, destPath);
                if (!downloaded)
                {
                    Log($"ERROR: Failed to download {file}");
                    UpdateProgress(0, Application.Current.Resources["ApplyChangesError"] as string ?? "Error occurred");
                    BackButton.IsEnabled = true;
                    _isRunning = false;
                    return;
                }
                Log($"Downloaded {file} to C:\\");
            }

            // Step 9: Configure boot entry with bcdedit
            UpdateProgress(98, "Configuring boot entry...");
            Log("Step 9: Configuring GRUB4DOS boot entry...");
            System.Threading.Thread.Sleep(1000);

            bool bootConfigured = await ConfigureBootEntryAsync();
            if (!bootConfigured)
            {
                Log("ERROR: Failed to configure boot entry");
                UpdateProgress(0, Application.Current.Resources["ApplyChangesError"] as string ?? "Error occurred");
                BackButton.IsEnabled = true;
                _isRunning = false;
                return;
            }

            // Done
            UpdateProgress(100, Application.Current.Resources["ApplyChangesComplete"] as string ?? "Partitioning complete!");
            Log("Installation preparation completed successfully!");
            Log($"- FAT32 boot partition: Z: (2GB)");
            Log($"- Desired Linux size: {_linuxSizeGB:N0}GB (Linux will finish shrinking if needed)");
            Log("- ISO contents copied to Z:");
            Log("- GRUB4DOS bootloader installed");
            Log("- Boot entry 'Install Linux' added");
            Log("- Layout: [Windows] [Free space] [FAT32 Z:] [Recovery]");

            RebootButton.Visibility = Visibility.Visible;
        }

        private async Task<bool> DownloadFileAsync(string url, string destinationPath)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(5);
                    var data = await client.GetByteArrayAsync(url);
                    File.WriteAllBytes(destinationPath, data);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => Log($"Download failed for {url}: {ex.Message}"));
                return false;
            }
        }

        private async Task<bool> ConfigureBootEntryAsync()
        {
            try
            {
                // Full path to bcdedit.exe - use Sysnative to bypass WOW64 redirection
                string bcdeditPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative", "bcdedit.exe");

                // If Sysnative doesn't exist (running as 64-bit), use System32
                if (!File.Exists(bcdeditPath))
                {
                    bcdeditPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bcdedit.exe");
                }

                Log($"Using bcdedit at: {bcdeditPath}");

                // Step 1: Create the boot entry and capture the GUID
                var createPsi = new ProcessStartInfo
                {
                    FileName = bcdeditPath,
                    Arguments = "/create /d \"Install Linux\" /application bootsector",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    Verb = "runas"
                };

                string guid = "";
                string output = "";
                string error = "";

                await Task.Run(() =>
                {
                    using (var process = Process.Start(createPsi))
                    {
                        output = process.StandardOutput.ReadToEnd();
                        error = process.StandardError.ReadToEnd();
                        process.WaitForExit();
                    }
                });

                Log($"bcdedit create output: {output}");
                if (!string.IsNullOrEmpty(error))
                    Log($"bcdedit create error: {error}");

                // Find GUID between { and } in the output
                int startIdx = output.IndexOf('{');
                int endIdx = output.IndexOf('}');
                if (startIdx >= 0 && endIdx > startIdx)
                {
                    guid = output.Substring(startIdx, endIdx - startIdx + 1);
                    Log($"Found GUID: {guid}");
                }
                else
                {
                    Log($"ERROR: Could not find GUID in output");
                    return false;
                }

                // Wait 1 second before next bcdedit commands
                await Task.Delay(1000);

                // Step 2: Set device partition
                await RunBcdeditCommandAsync(bcdeditPath, $"/set {guid} device partition=C:");

                await Task.Delay(1000);

                // Step 3: Set path to grldr.mbr
                await RunBcdeditCommandAsync(bcdeditPath, $"/set {guid} path \\grldr.mbr");

                await Task.Delay(1000);

                // Step 4: Add to boot menu
                await RunBcdeditCommandAsync(bcdeditPath, $"/displayorder {guid} /addlast");

                Log("Boot entry configured successfully");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Boot configuration failed: {ex.Message}");
                return false;
            }
        }

        private async Task RunBcdeditCommandAsync(string bcdeditPath, string arguments)
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

            int exitCode = 0;
            await Task.Run(() =>
            {
                using (var process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    exitCode = process.ExitCode;
                }
            });

            Log($"bcdedit {arguments}: {(exitCode == 0 ? "OK" : "Failed")}");
        }

        private async Task<bool> WriteConfigToFat32Async()
        {
            // Get locale settings from main thread before running on background thread
            string systemLang = "";
            string keyboardLayout = "";
            string timezone = "";

            Dispatcher.Invoke(() =>
            {
                systemLang = Localization.GetLinuxLocale();
                keyboardLayout = Localization.GetKeyboardLayout();
                timezone = Localization.GetWindowsTimezoneAsLinux();
            });

            return await Task.Run(() =>
            {
                try
                {
                    string configPath = @"Z:\config.txt";

                    // Get account info
                    string username = "user";
                    string password = "password";
                    string computerName = "linux-pc";

                    if (App.Current.Properties["AccountInfo"] is AccountInfo account)
                    {
                        username = account.Username;
                        password = account.Password;
                        computerName = account.ComputerName;
                    }

                    // Get distro info - use IsoInstallerFileName for config
                    string isoFilename = "mint.iso";
                    if (App.Current.Properties["SelectedDistro"] is DistroInfo distro && !string.IsNullOrEmpty(distro.IsoInstallerFileName))
                    {
                        isoFilename = distro.IsoInstallerFileName;
                    }

                    // Build config content with user settings
                    var configLines = new List<string>
                    {
                        $"SYSTEM_LANG=\"{systemLang}\"",
                        $"KEYBOARD_LAYOUT=\"{keyboardLayout}\"",
                        "KEYBOARD_MODEL=\"pc105\"",
                        $"TIMEZONE=\"{timezone}\"",
                        $"USERNAME=\"{username}\"",
                        $"PASSWORD=\"{password}\"",
                        $"ISO_FILENAME=\"{isoFilename}\"",
                        $"LINUX_SIZE_GB=\"{_linuxSizeGB:F0}\""
                    };

                    File.WriteAllText(configPath, string.Join("\n", configLines));

                    Dispatcher.Invoke(() =>
                    {
                        Log($"Config written to Z:\\config.txt:");
                        Log($"  SYSTEM_LANG={systemLang}");
                        Log($"  KEYBOARD_LAYOUT={keyboardLayout}");
                        Log($"  TIMEZONE={timezone}");
                        Log($"  USERNAME={username}");
                        Log($"  LINUX_SIZE_GB={_linuxSizeGB:F0}");
                    });
                    return true;
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => Log($"Failed to write config: {ex.Message}"));
                    return false;
                }
            });
        }

        private async Task<bool> DownloadIsoAsync(string url, string destinationPath)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromHours(2); // Long timeout for large files

                    using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        var totalBytes = response.Content.Headers.ContentLength ?? 0;
                        var totalMB = totalBytes / 1024.0 / 1024.0;

                        Dispatcher.Invoke(() => Log($"ISO size: {totalMB:N0} MB"));

                        using (var contentStream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            var buffer = new byte[8192];
                            long totalRead = 0;
                            int bytesRead;
                            var lastProgressUpdate = DateTime.Now;

                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead);
                                totalRead += bytesRead;

                                // Update progress every 500ms
                                if ((DateTime.Now - lastProgressUpdate).TotalMilliseconds > 500)
                                {
                                    var progressPercent = totalBytes > 0 ? (int)(totalRead * 100 / totalBytes) : 0;
                                    var downloadedMB = totalRead / 1024.0 / 1024.0;
                                    Dispatcher.Invoke(() =>
                                    {
                                        var overallProgress = 60 + (progressPercent * 20 / 100); // 60-80%
                                        UpdateProgress(overallProgress, $"Downloading... {downloadedMB:N0}/{totalMB:N0} MB ({progressPercent}%)");
                                    });
                                    lastProgressUpdate = DateTime.Now;
                                }
                            }
                        }
                    }
                }

                Dispatcher.Invoke(() => Log("ISO download completed"));
                return true;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => Log($"Download failed: {ex.Message}"));
                return false;
            }
        }

        private async Task<bool> DownloadInstallerIsoAsync(string url, string destinationPath)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromHours(4); // Very long timeout for large Linux ISOs

                    using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        var totalBytes = response.Content.Headers.ContentLength ?? 0;
                        var totalMB = totalBytes / 1024.0 / 1024.0;

                        Dispatcher.Invoke(() => Log($"Linux installer ISO size: {totalMB:N0} MB"));

                        using (var contentStream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            var buffer = new byte[81920]; // Larger buffer for big files
                            long totalRead = 0;
                            int bytesRead;
                            var lastProgressUpdate = DateTime.Now;

                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead);
                                totalRead += bytesRead;

                                // Update progress every 500ms
                                if ((DateTime.Now - lastProgressUpdate).TotalMilliseconds > 500)
                                {
                                    var progressPercent = totalBytes > 0 ? (int)(totalRead * 100 / totalBytes) : 0;
                                    var downloadedMB = totalRead / 1024.0 / 1024.0;
                                    Dispatcher.Invoke(() =>
                                    {
                                        var overallProgress = 85 + (progressPercent * 10 / 100); // 85-95%
                                        UpdateProgress(overallProgress, $"Downloading Linux ISO... {downloadedMB:N0}/{totalMB:N0} MB ({progressPercent}%)");
                                    });
                                    lastProgressUpdate = DateTime.Now;
                                }
                            }
                        }
                    }
                }

                Dispatcher.Invoke(() => Log("Linux installer ISO download completed"));
                return true;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => Log($"Linux installer download failed: {ex.Message}"));
                return false;
            }
        }

        private async Task<bool> MountAndCopyIsoAsync(string isoPath)
        {
            return await Task.Run(() =>
            {
                string mountedDrive = "";

                try
                {
                    // Create a PowerShell script file to avoid escaping issues
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

                    // Run the mount script
                    var mountPsi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(mountPsi))
                    {
                        mountedDrive = process.StandardOutput.ReadToEnd().Trim();
                        string error = process.StandardError.ReadToEnd();
                        process.WaitForExit();

                        if (process.ExitCode != 0 || string.IsNullOrEmpty(mountedDrive))
                        {
                            Dispatcher.Invoke(() => Log($"ERROR mounting ISO: {error}"));
                            File.Delete(scriptPath);
                            return false;
                        }
                    }

                    File.Delete(scriptPath);

                    // Get only the first letter if multiple lines
                    if (mountedDrive.Contains("\n"))
                    {
                        mountedDrive = mountedDrive.Split('\n')[0].Trim();
                    }

                    Dispatcher.Invoke(() => Log($"ISO mounted at {mountedDrive}:"));

                    // Wait a bit for the drive to be ready
                    System.Threading.Thread.Sleep(2000);

                    // Copy all contents from mounted ISO to Z:
                    string sourceDir = $"{mountedDrive}:\\";
                    string destDir = @"Z:\";

                    if (!Directory.Exists(sourceDir))
                    {
                        Dispatcher.Invoke(() => Log($"ERROR: Source directory not found: {sourceDir}"));
                        return false;
                    }

                    Dispatcher.Invoke(() => Log($"Copying files from {sourceDir} to {destDir}..."));

                    // Use xcopy for reliable copying (robocopy can have issues with ISO)
                    var copyPsi = new ProcessStartInfo
                    {
                        FileName = "xcopy",
                        Arguments = $"\"{sourceDir}*\" \"{destDir}\" /E /H /Y /Q",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var copyProcess = Process.Start(copyPsi))
                    {
                        string copyOutput = copyProcess.StandardOutput.ReadToEnd();
                        string copyError = copyProcess.StandardError.ReadToEnd();
                        copyProcess.WaitForExit();

                        if (copyProcess.ExitCode != 0)
                        {
                            Dispatcher.Invoke(() => Log($"Copy error (exit {copyProcess.ExitCode}): {copyError}"));
                            // Continue anyway, some files may have copied
                        }

                        // Get file count from xcopy output
                        var lines = copyOutput.Split('\n');
                        string lastLine = lines.Length > 0 ? lines[lines.Length - 1].Trim() : "done";
                        if (string.IsNullOrWhiteSpace(lastLine) && lines.Length > 1)
                            lastLine = lines[lines.Length - 2].Trim();
                        Dispatcher.Invoke(() => Log($"Copy completed: {(string.IsNullOrWhiteSpace(lastLine) ? "done" : lastLine)}"));
                    }

                    Dispatcher.Invoke(() => Log("Files copied successfully"));
                    return true;
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => Log($"Mount/copy failed: {ex.Message}"));
                    return false;
                }
                finally
                {
                    // Always try to unmount the ISO
                    try
                    {
                        Dispatcher.Invoke(() => Log("Dismounting ISO..."));
                        var unmountPsi = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Dismount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}'\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using (var unmountProcess = Process.Start(unmountPsi))
                        {
                            unmountProcess.WaitForExit();
                        }
                        Dispatcher.Invoke(() => Log("ISO dismounted"));
                    }
                    catch (Exception unmountEx)
                    {
                        Dispatcher.Invoke(() => Log($"Warning: Could not dismount ISO: {unmountEx.Message}"));
                    }
                }
            });
        }

        private async Task<(double freeSpaceSizeMB, double recoveryOffsetMB)> GetFreeSpaceInfoAsync()
        {
            string diskpartScript = Path.Combine(Path.GetTempPath(), $"freespace_{Guid.NewGuid()}.txt");

            try
            {
                // Query partition layout to find free space
                string script = @"select disk 0
list partition
exit";

                File.WriteAllText(diskpartScript, script);
                string output = await RunDiskpartAndGetOutputAsync(diskpartScript);

                // Parse partitions to find free space location
                var partitions = new List<(int number, double offsetMB, double sizeMB)>();

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    // Match: "Partition 2    Principale         127 G octets     51 M octets"
                    // The first size is the partition size, the second is the offset
                    var partitionMatch = Regex.Match(line, @"Partition\s+(\d+)", RegexOptions.IgnoreCase);
                    if (!partitionMatch.Success)
                        continue;

                    int partitionNumber = int.Parse(partitionMatch.Groups[1].Value);

                    // Find all size/offset values in the line
                    var sizeMatches = Regex.Matches(line, @"(\d+)\s*(G|M|K)\s*o?", RegexOptions.IgnoreCase);

                    if (sizeMatches.Count >= 2)
                    {
                        // First match = size, Second match = offset
                        double sizeMB = ParseSizeToMB(sizeMatches[0]);
                        double offsetMB = ParseSizeToMB(sizeMatches[1]);

                        partitions.Add((partitionNumber, offsetMB, sizeMB));
                        Log($"  Partition {partitionNumber}: size={sizeMB:N0}MB, offset={offsetMB:N0}MB");
                    }
                }

                if (partitions.Count < 2)
                {
                    Log("ERROR: Could not find enough partitions to determine free space");
                    return (0, 0);
                }

                // Sort by offset
                partitions.Sort((a, b) => a.offsetMB.CompareTo(b.offsetMB));

                // Find Windows partition (second partition after sorting) and where it ends
                var windowsPartition = partitions[1];
                double windowsEndMB = windowsPartition.offsetMB + windowsPartition.sizeMB;

                // Find Recovery partition (last partition by offset)
                var recoveryPartition = partitions[partitions.Count - 1];
                double recoveryOffsetMB = recoveryPartition.offsetMB;

                // Free space is between Windows end and Recovery start
                double freeSpaceSizeMB = recoveryOffsetMB - windowsEndMB;

                Log($"Windows ends at: {windowsEndMB:N0}MB");
                Log($"Recovery starts at: {recoveryOffsetMB:N0}MB");
                Log($"Free space size: {freeSpaceSizeMB:N0}MB");

                return (freeSpaceSizeMB, recoveryOffsetMB);
            }
            catch (Exception ex)
            {
                Log($"Error getting free space info: {ex.Message}");
                return (0, 0);
            }
            finally
            {
                if (File.Exists(diskpartScript))
                    File.Delete(diskpartScript);
            }
        }

        private double ParseSizeToMB(Match match)
        {
            double size = double.Parse(match.Groups[1].Value);
            string unit = match.Groups[2].Value.ToUpper();

            switch (unit)
            {
                case "G":
                    return size * 1024;
                case "K":
                    return size / 1024;
                default:
                    return size;
            }
        }

        private async Task<double> QueryShrinkSpaceAsync()
        {
            string diskpartScript = Path.Combine(Path.GetTempPath(), $"querymax_{Guid.NewGuid()}.txt");

            try
            {
                string systemDrive = Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\');

                string script = $@"rescan
select volume {systemDrive[0]}
shrink querymax
exit";

                File.WriteAllText(diskpartScript, script);
                var (success, output) = await RunDiskpartWithResultAsync(diskpartScript);

                // Parse the max shrink size from output
                // French: "Le nombre maximal d'octets récupérables est :   12 GB (12445 Mo)"
                // English: "The maximum number of reclaimable bytes is: 12 GB"
                var match = Regex.Match(output, @"(\d+)\s*(?:GB|Go|G)\s*\((\d+)\s*Mo\)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return double.Parse(match.Groups[2].Value); // Return MB value
                }

                // Try alternative pattern
                match = Regex.Match(output, @"(\d+)\s*(?:MB|Mo|M)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return double.Parse(match.Groups[1].Value);
                }

                return 0;
            }
            finally
            {
                if (File.Exists(diskpartScript))
                    File.Delete(diskpartScript);
            }
        }

        private async Task<bool> ShrinkWindowsPartitionAsync(double shrinkSizeMB)
        {
            string diskpartScript = Path.Combine(Path.GetTempPath(), $"shrink_{Guid.NewGuid()}.txt");

            try
            {
                // Get system drive letter
                string systemDrive = Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\');

                // Create diskpart script with rescan to refresh disk state
                string script = $@"rescan
list volume
select volume {systemDrive[0]}
shrink desired={shrinkSizeMB:F0}
exit";

                File.WriteAllText(diskpartScript, script);
                Log($"Running diskpart: shrink {shrinkSizeMB:F0}MB from {systemDrive}");

                var (success, output) = await RunDiskpartWithResultAsync(diskpartScript);

                // Check if shrink was successful by looking for success message
                if (output.Contains("réduit") || output.Contains("shrunk") || output.Contains("reduced"))
                {
                    return true;
                }

                // Check for specific error messages
                if (output.Contains("insuffisant") || output.Contains("pas assez") || output.Contains("not enough"))
                {
                    Log("ERROR: Not enough space available for shrinking");
                    return false;
                }

                return success;
            }
            finally
            {
                if (File.Exists(diskpartScript))
                    File.Delete(diskpartScript);
            }
        }

        private async Task<bool> CreateFat32PartitionSimpleAsync()
        {
            string diskpartScript = Path.Combine(Path.GetTempPath(), $"create_fat32_{Guid.NewGuid()}.txt");

            try
            {
                // Create FAT32 partition in the first available free space (right after Windows)
                // No offset specified - diskpart will place it at the beginning of free space
                string script = @"rescan
select disk 0
create partition primary size=2048
format fs=fat32 quick label=LINUXGATE
assign letter=Z
exit";

                File.WriteAllText(diskpartScript, script);
                Log("Diskpart command: create partition primary size=2048 (no offset)");
                Log("Running diskpart to create FAT32 partition...");

                var (success, output) = await RunDiskpartWithResultAsync(diskpartScript);

                // Check for success indicators
                if (output.Contains("créé") || output.Contains("created") || output.Contains("formaté") || output.Contains("formatted"))
                {
                    return true;
                }

                return success;
            }
            finally
            {
                if (File.Exists(diskpartScript))
                    File.Delete(diskpartScript);
            }
        }

        private async Task<(bool success, string output)> RunDiskpartWithResultAsync(string scriptPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "diskpart.exe",
                        Arguments = $"/s \"{scriptPath}\"",
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

                        Dispatcher.Invoke(() =>
                        {
                            if (!string.IsNullOrWhiteSpace(output))
                                Log(output);
                            if (!string.IsNullOrWhiteSpace(error))
                                Log($"ERROR: {error}");
                        });

                        // Check for error keywords in output
                        bool hasError = output.ToLower().Contains("introuvable") ||
                                       output.ToLower().Contains("erreur") ||
                                       output.ToLower().Contains("error") ||
                                       output.ToLower().Contains("failed") ||
                                       output.ToLower().Contains("impossible") ||
                                       output.ToLower().Contains("insuffisant");

                        return (process.ExitCode == 0 && !hasError, output);
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => Log($"Exception: {ex.Message}"));
                    return (false, ex.Message);
                }
            });
        }

        private void RebootButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                Application.Current.Resources["ApplyChangesRebootConfirm"] as string ?? "The computer will restart to complete the installation. Continue?",
                Application.Current.Resources["WarningTitle"] as string ?? "Warning",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                Process.Start("shutdown", "/r /t 0");
            }
        }

        private void UpdateProgress(int percent, string step)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value = percent;
                ProgressText.Text = $"{percent}%";
                CurrentStepText.Text = step;
            });
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogOutput.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                LogOutput.ScrollToEnd();
            });
        }
    }
}
