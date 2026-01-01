using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinuxGate.Helpers;
using LinuxGate.Models;
using LinuxGate.Pages;
using System.ComponentModel;
using System.Windows.Media.Animation;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace LinuxGate
{
    public partial class ChooseDistro : Page, INotifyPropertyChanged
    {
        private const string STATE_KEY = "ChooseDistro";
        private const string DISTROS_URL = "https://tpm28.com/filepool/distros.json";
        private ObservableCollection<DistroInfo> _distros;
        private DistroInfo _selectedDistro;
        private bool _isDistroSelected;
        private bool _partitionConfigValid = true;
        private bool _partitionWarningAcknowledged = false;

        public bool IsDistroSelected
        {
            get => _isDistroSelected;
            set
            {
                _isDistroSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDistroSelected)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ChooseDistro()
        {
            InitializeComponent();
            _distros = new ObservableCollection<DistroInfo>();
            LoadDistrosAsync();
            LoadState();
            DataContext = this;
            IsDistroSelected = false;
            CheckPartitionConfigurationAsync();
        }

        private async void LoadDistrosAsync()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    var json = await client.GetStringAsync(DISTROS_URL);
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };
                    var distroList = JsonSerializer.Deserialize<List<DistroInfoJson>>(json, options);
                    
                    _distros.Clear();
                    foreach (var distroJson in distroList)
                    {
                        _distros.Add(new DistroInfo
                        {
                            Name = distroJson.Name,
                            Description = distroJson.Description ?? "No description available",
                            ImageUrl = distroJson.ImageUrl,
                            IsoUrl = distroJson.IsoUrl,
                            IsoInstaller = distroJson.IsoInstaller,
                            IsoInstallerFileName = distroJson.IsoInstallerFileName
                        });
                    }
                }
                DistrosItemsControl.ItemsSource = _distros;
            }
            catch (Exception)
            {
                MessageBox.Show(
                    Application.Current.Resources["DistroLoadError"] as string ?? "Failed to load distributions",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void SaveState()
        {
            if (_selectedDistro != null)
            {
                var state = new PageState
                {
                    PageType = typeof(ChooseDistro),
                    StateKey = STATE_KEY,
                    State = _selectedDistro.Name // Save just the name of the selected distro
                };
                StateManager.SaveState(STATE_KEY, state);
            }
        }

        private void LoadState()
        {
            var state = StateManager.GetState(STATE_KEY);
            if (state?.State is string selectedDistroName)
            {
                // Find and select the previously selected distro
                foreach (var distro in _distros)
                {
                    if (distro.Name == selectedDistroName)
                    {
                        SelectDistro(distro);
                        break;
                    }
                }
            }
        }

        private void SelectDistro(DistroInfo distro)
        {
            // Deselect previous selection
            if (_selectedDistro != null)
            {
                _selectedDistro.IsSelected = false;
            }

            // Select new distro
            _selectedDistro = distro;
            _selectedDistro.IsSelected = true;

            // Update next button state (considers partition validation)
            UpdateNextButtonState();
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is DistroInfo distro)
            {
                if (_selectedDistro != distro)
                {
                    StateManager.ClearDependentStates("ResizeDisk");
                }
                SelectDistro(distro);
            }
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDistro != null)
            {
                SaveState();
                App.Current.Properties["SelectedDistro"] = _selectedDistro;
                NavigationHelper.NavigateWithAnimation(NavigationService, new ResizeDisk(), TimeSpan.FromSeconds(0.3));
            }
        }

        private void NavigateWithAnimation(Page nextPage)
        {
            var fadeOut = new DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = TimeSpan.FromSeconds(0.3)
            };

            var slideOut = new ThicknessAnimation
            {
                From = new Thickness(0),
                To = new Thickness(-100, 0, 0, 0),
                Duration = TimeSpan.FromSeconds(0.3)
            };

            fadeOut.Completed += (s, _) =>
            {
                var currentBackground = ((Grid)this.Content).Background;
                NavigationService.Navigate(nextPage);
                ((Grid)nextPage.Content).Background = currentBackground;

                var fadeIn = new DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = TimeSpan.FromSeconds(0.3)
                };

                var slideIn = new ThicknessAnimation
                {
                    From = new Thickness(100, 0, 0, 0),
                    To = new Thickness(0),
                    Duration = TimeSpan.FromSeconds(0.3)
                };

                nextPage.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                nextPage.BeginAnimation(FrameworkElement.MarginProperty, slideIn);
            };

            this.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            this.BeginAnimation(FrameworkElement.MarginProperty, slideOut);
        }

        #region Partition Validation

        private async void CheckPartitionConfigurationAsync()
        {
            var (isValid, warnings) = await ValidatePartitionLayoutAsync();

            _partitionConfigValid = isValid;

            if (!isValid)
            {
                string warningMessage = string.Join("\n", warnings);
                PartitionWarningText.Text = warningMessage;
                PartitionWarningPanel.Visibility = Visibility.Visible;
            }

            UpdateNextButtonState();
        }

        private void PartitionWarningCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            _partitionWarningAcknowledged = PartitionWarningCheckbox.IsChecked == true;
            UpdateNextButtonState();
        }

        private void UpdateNextButtonState()
        {
            // Button is enabled if:
            // 1. A distro is selected AND
            // 2. Either partition config is valid OR user acknowledged the warning
            bool canProceed = _selectedDistro != null &&
                              (_partitionConfigValid || _partitionWarningAcknowledged);
            NextButton.IsEnabled = canProceed;
        }

        private async Task<(bool isValid, List<string> warnings)> ValidatePartitionLayoutAsync()
        {
            var warnings = new List<string>();

            string diskpartScript = Path.Combine(Path.GetTempPath(), $"check_partitions_{Guid.NewGuid()}.txt");

            try
            {
                string script = @"select disk 0
list partition
exit";

                File.WriteAllText(diskpartScript, script);
                string output = await RunDiskpartAndGetOutputAsync(diskpartScript);

                var partitions = ParsePartitionList(output);

                // Check 1: Should have exactly 3 partitions
                if (partitions.Count > 3)
                {
                    warnings.Add($"Expected 3 partitions, found {partitions.Count}");
                }
                else if (partitions.Count < 3)
                {
                    warnings.Add($"Expected 3 partitions, found only {partitions.Count}");
                }

                // Check 2: First partition should be between 40-150MB (EFI/System)
                if (partitions.Count > 0)
                {
                    var firstPartition = partitions[0];
                    if (firstPartition.SizeMB < 40 || firstPartition.SizeMB > 150)
                    {
                        warnings.Add($"First partition size is {firstPartition.SizeMB:F0}MB (expected 40-150MB for System)");
                    }
                }

                // Check 3: Last partition should be between 400-700MB (Recovery)
                if (partitions.Count > 0)
                {
                    var lastPartition = partitions[partitions.Count - 1];
                    if (lastPartition.SizeMB < 400 || lastPartition.SizeMB > 700)
                    {
                        warnings.Add($"Last partition size is {lastPartition.SizeMB:F0}MB (expected 400-700MB for Recovery)");
                    }
                }

                return (warnings.Count == 0, warnings);
            }
            catch (Exception ex)
            {
                warnings.Add($"Error checking partitions: {ex.Message}");
                return (false, warnings);
            }
            finally
            {
                if (File.Exists(diskpartScript))
                    File.Delete(diskpartScript);
            }
        }

        private class PartitionInfo
        {
            public int Number { get; set; }
            public string Type { get; set; }
            public double SizeMB { get; set; }
        }

        private List<PartitionInfo> ParsePartitionList(string output)
        {
            var partitions = new List<PartitionInfo>();
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var partitionMatch = Regex.Match(line, @"Partition\s+(\d+)", RegexOptions.IgnoreCase);
                if (!partitionMatch.Success)
                    continue;

                int partitionNumber = int.Parse(partitionMatch.Groups[1].Value);

                var sizeMatches = Regex.Matches(line, @"(\d+)\s*(G|M|K)\s*o?", RegexOptions.IgnoreCase);

                if (sizeMatches.Count > 0)
                {
                    var sizeMatch = sizeMatches[0];
                    double size = double.Parse(sizeMatch.Groups[1].Value);
                    string unit = sizeMatch.Groups[2].Value.ToUpper();

                    double sizeMB;
                    switch (unit)
                    {
                        case "G":
                            sizeMB = size * 1024;
                            break;
                        case "K":
                            sizeMB = size / 1024;
                            break;
                        default:
                            sizeMB = size;
                            break;
                    }

                    string type = "Unknown";
                    var typeMatch = Regex.Match(line, @"Partition\s+\d+\s+(\w+)", RegexOptions.IgnoreCase);
                    if (typeMatch.Success)
                    {
                        type = typeMatch.Groups[1].Value;
                    }

                    partitions.Add(new PartitionInfo
                    {
                        Number = partitionNumber,
                        Type = type,
                        SizeMB = sizeMB
                    });
                }
            }

            return partitions;
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

        #endregion
    }
}