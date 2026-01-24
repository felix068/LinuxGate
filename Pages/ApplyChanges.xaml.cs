using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LinuxGate.Helpers;
using LinuxGate.Models;
using LinuxGate.Services;

namespace LinuxGate.Pages
{
    public partial class ApplyChanges : Page
    {
        private double _linuxSizeGB;
        private const double FAT32_SIZE_GB = 2.0;
        private bool _isRunning = false;

        private readonly DiskService _diskService;
        private readonly DownloadService _downloadService;
        private readonly IsoService _isoService;
        private readonly BootConfigService _bootConfigService;
        private readonly ConfigService _configService;

        public ApplyChanges()
        {
            InitializeComponent();

            _diskService = new DiskService();
            _downloadService = new DownloadService();
            _isoService = new IsoService();
            _bootConfigService = new BootConfigService();
            _configService = new ConfigService();

            LoadSummary();
            Loaded += ApplyChanges_Loaded;
        }

        private async void ApplyChanges_Loaded(object sender, RoutedEventArgs e)
        {
            await StartInstallationAsync();
        }

        private void LoadSummary()
        {
            var stateKey = $"ResizeDisk_{(App.Current.Properties["SelectedDistro"] as DistroInfo)?.Name}";
            var state = StateManager.GetState(stateKey);
            if (state?.State is System.Collections.Generic.Dictionary<string, double> savedState)
            {
                _linuxSizeGB = savedState["LinuxSize"];
            }
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
            double maxShrinkMB = await _diskService.QueryShrinkSpaceAsync();
            Log($"Maximum shrinkable space: {maxShrinkMB / 1024:N1}GB ({maxShrinkMB:N0}MB)");

            double minRequiredMB = (FAT32_SIZE_GB + 5) * 1024;
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

            // Step 1: Shrink Windows by ONLY 2GB (for FAT32)
            UpdateProgress(10, Application.Current.Resources["ApplyChangesStep1"] as string ?? "Shrinking Windows partition...");
            Log("Step 1: Shrinking Windows by 2GB for FAT32 partition...");

            bool step1Success = await _diskService.ShrinkPartitionAsync(2048);
            if (!step1Success)
            {
                Log("ERROR: Failed to shrink Windows partition (step 1)");
                BackButton.IsEnabled = true;
                _isRunning = false;
                return;
            }

            Log("Waiting for disk to update...");
            await Task.Delay(3000);

            // Step 2: Create FAT32 partition
            UpdateProgress(30, Application.Current.Resources["ApplyChangesStep2"] as string ?? "Creating FAT32 boot partition (Z:)...");
            Log("Step 2: Creating FAT32 partition (will be placed right after Windows)...");

            bool step2Success = await _diskService.CreateFat32PartitionAsync();
            if (!step2Success)
            {
                Log("ERROR: Failed to create FAT32 partition");
                BackButton.IsEnabled = true;
                _isRunning = false;
                return;
            }

            Log("Waiting for disk to update...");
            await Task.Delay(3000);

            // Step 3: Shrink Windows by the MAXIMUM available for Linux
            Log("Checking remaining shrink space...");
            double remainingShrinkMB = await _diskService.QueryShrinkSpaceAsync();
            Log($"Remaining shrinkable space: {remainingShrinkMB / 1024:N1}GB ({remainingShrinkMB:N0}MB)");

            double requestedLinuxMB = _linuxSizeGB * 1024;

            if (remainingShrinkMB > 1024)
            {
                UpdateProgress(45, "Creating free space for Linux...");
                double shrinkAmountMB = Math.Min(remainingShrinkMB - 512, requestedLinuxMB);
                Log($"Step 3: Shrinking Windows by {shrinkAmountMB / 1024:N1}GB for Linux (user requested {_linuxSizeGB:N0}GB)...");

                bool step3Success = await _diskService.ShrinkPartitionAsync(shrinkAmountMB);
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
            bool downloadSuccess = await _downloadService.DownloadIsoAsync(isoUrl, tempIsoPath, progress =>
            {
                Dispatcher.Invoke(() =>
                {
                    var overallProgress = 60 + (progress.PercentComplete * 20 / 100);
                    UpdateProgress(overallProgress, $"Downloading... {progress.DownloadedMB:N0}/{progress.TotalMB:N0} MB ({progress.PercentComplete}%)");
                });
            });

            if (!downloadSuccess)
            {
                Log("ERROR: Failed to download ISO");
                UpdateProgress(0, Application.Current.Resources["ApplyChangesError"] as string ?? "Error occurred");
                BackButton.IsEnabled = true;
                _isRunning = false;
                return;
            }

            Log("ISO download completed");

            // Step 5: Mount ISO and copy contents to Z:
            UpdateProgress(80, "Copying ISO contents to Z:...");
            Log("Step 5: Mounting ISO and copying contents to Z:...");

            bool copySuccess = await _isoService.MountCopyAndDismountAsync(tempIsoPath, @"Z:\", msg => Log(msg));
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
                bool installerDownloadSuccess = await _downloadService.DownloadInstallerIsoAsync(
                    selectedDistro.IsoInstaller,
                    installerPath,
                    progress =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            var overallProgress = 85 + (progress.PercentComplete * 10 / 100);
                            UpdateProgress(overallProgress, $"Downloading Linux ISO... {progress.DownloadedMB:N0}/{progress.TotalMB:N0} MB ({progress.PercentComplete}%)");
                        });
                    });

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

            // Step 7: Write config.txt AFTER ISO copy
            UpdateProgress(95, "Writing configuration...");
            Log("Step 7: Writing configuration to Z:\\config.txt...");

            var config = CreateConfigFromSettings();
            bool configSuccess = await _configService.WriteConfigToFat32Async(config);
            if (!configSuccess)
            {
                Log("WARNING: Failed to write config.txt, will use defaults");
            }
            else
            {
                Log($"Config written to Z:\\config.txt:");
                Log($"  SYSTEM_LANG={config.SystemLang}");
                Log($"  KEYBOARD_LAYOUT={config.KeyboardLayout}");
                Log($"  TIMEZONE={config.Timezone}");
                Log($"  USERNAME={config.Username}");
                Log($"  LINUX_SIZE_GB={config.LinuxSizeGB:F0}");
            }

            // Step 8: Download GRUB4DOS files to C:\
            UpdateProgress(96, "Downloading bootloader files...");
            Log("Step 8: Downloading GRUB4DOS files to C:\\...");

            string[] grubFiles = { "grldr", "grldr.mbr", "menu.lst" };
            foreach (var file in grubFiles)
            {
                string url = $"https://tpm28.com/filepool/{file}";
                string destPath = Path.Combine(@"C:\", file);
                bool downloaded = await _downloadService.DownloadFileAsync(url, destPath);
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

            bool bootConfigured = await _bootConfigService.ConfigureGrub4DosEntryAsync(
                "Install Linux",
                "C:",
                @"\grldr.mbr",
                msg => Log(msg));

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

        private ConfigService.LinuxConfig CreateConfigFromSettings()
        {
            var config = new ConfigService.LinuxConfig
            {
                LinuxSizeGB = _linuxSizeGB
            };

            // Get locale settings
            Dispatcher.Invoke(() =>
            {
                config.SystemLang = Localization.GetLinuxLocale();
                config.KeyboardLayout = Localization.GetKeyboardLayout();
                config.Timezone = Localization.GetWindowsTimezoneAsLinux();
            });

            // Get account info
            if (App.Current.Properties["AccountInfo"] is AccountInfo account)
            {
                config.Username = account.Username;
                config.Password = account.Password;
            }

            // Get distro info
            if (App.Current.Properties["SelectedDistro"] is DistroInfo distro && !string.IsNullOrEmpty(distro.IsoInstallerFileName))
            {
                config.IsoFilename = distro.IsoInstallerFileName;
            }

            return config;
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
