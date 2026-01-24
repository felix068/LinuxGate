using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinuxGate.Helpers;
using LinuxGate.Models;
using LinuxGate.Pages;
using LinuxGate.Services;
using System.ComponentModel;
using System.Windows.Media.Animation;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

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

        private readonly DiskService _diskService;

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
            _diskService = new DiskService();
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
                    State = _selectedDistro.Name
                };
                StateManager.SaveState(STATE_KEY, state);
            }
        }

        private void LoadState()
        {
            var state = StateManager.GetState(STATE_KEY);
            if (state?.State is string selectedDistroName)
            {
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
            if (_selectedDistro != null)
            {
                _selectedDistro.IsSelected = false;
            }

            _selectedDistro = distro;
            _selectedDistro.IsSelected = true;

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
            var (isValid, warnings) = await _diskService.ValidatePartitionLayoutAsync();

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
            bool canProceed = _selectedDistro != null &&
                              (_partitionConfigValid || _partitionWarningAcknowledged);
            NextButton.IsEnabled = canProceed;
        }

        #endregion
    }
}
