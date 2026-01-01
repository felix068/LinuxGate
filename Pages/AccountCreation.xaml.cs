using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinuxGate.Helpers;
using LinuxGate.Models;

namespace LinuxGate.Pages
{
    public partial class AccountCreation : Page
    {
        private const string STATE_KEY = "AccountCreation";
        private readonly Regex usernameRegex = new Regex("^[a-z][a-z0-9-]*$");
        private readonly Regex hostnameRegex = new Regex("^[a-z][a-z0-9-]*$");

        public AccountCreation()
        {
            InitializeComponent();
            UpdateDefaultValues();
            LoadState();
        }

        private void UpdateDefaultValues()
        {
            // Get current Windows username and convert to lowercase
            string windowsUsername = Environment.UserName.ToLower();

            // Remove any characters that don't match our regex
            string sanitizedUsername = Regex.Replace(windowsUsername, "[^a-z0-9-]", "");

            // Ensure it starts with a letter
            if (!string.IsNullOrEmpty(sanitizedUsername) && char.IsLetter(sanitizedUsername[0]))
            {
                UsernameBox.Text = sanitizedUsername;
            }

            // Get Windows computer name and create Linux hostname
            string windowsHostname = Environment.MachineName.ToLower();
            string sanitizedHostname = Regex.Replace(windowsHostname, "[^a-z0-9-]", "");

            // Ensure it starts with a letter
            if (!string.IsNullOrEmpty(sanitizedHostname) && char.IsLetter(sanitizedHostname[0]))
            {
                HostnameBox.Text = sanitizedHostname + "-linux";
            }
            else
            {
                HostnameBox.Text = "linux-pc";
            }

            // Validate the default values
            ValidateInput(null, null);
        }

        private void SaveState()
        {
            var state = new PageState
            {
                PageType = typeof(AccountCreation),
                StateKey = STATE_KEY,
                State = new AccountInfo
                {
                    Username = UsernameBox.Text,
                    ComputerName = HostnameBox.Text
                    // Don't save password for security
                }
            };
            StateManager.SaveState(STATE_KEY, state);
        }

        private void LoadState()
        {
            var state = StateManager.GetState(STATE_KEY);
            if (state?.State is AccountInfo info)
            {
                UsernameBox.Text = info.Username;
                HostnameBox.Text = info.ComputerName;
                ValidateInput(null, null);
            }
        }

        private void ValidateInput(object sender, RoutedEventArgs e)
        {
            bool isValid = true;
            
            // Validate username
            if (string.IsNullOrEmpty(UsernameBox.Text))
            {
                UsernameError.Text = "Username is required";
                isValid = false;
            }
            else if (!usernameRegex.IsMatch(UsernameBox.Text))
            {
                UsernameError.Text = "Username must start with a letter and contain only lowercase letters, numbers, or hyphens";
                isValid = false;
            }
            else
            {
                UsernameError.Text = "";
            }

            // Validate password (min 4 characters)
            if (string.IsNullOrEmpty(PasswordBox.Password))
            {
                PasswordError.Text = Application.Current.Resources["PasswordRequired"] as string ?? "Password is required";
                isValid = false;
            }
            else if (PasswordBox.Password.Length < 4)
            {
                PasswordError.Text = Application.Current.Resources["PasswordTooShort"] as string ?? "Password must be at least 4 characters";
                isValid = false;
            }
            else
            {
                PasswordError.Text = "";
            }

            // Validate confirm password
            if (string.IsNullOrEmpty(ConfirmPasswordBox.Password))
            {
                ConfirmPasswordError.Text = "Please confirm your password";
                isValid = false;
            }
            else if (PasswordBox.Password != ConfirmPasswordBox.Password)
            {
                ConfirmPasswordError.Text = "Passwords do not match";
                isValid = false;
            }
            else
            {
                ConfirmPasswordError.Text = "";
            }

            // Validate hostname
            if (string.IsNullOrEmpty(HostnameBox.Text))
            {
                HostnameError.Text = "Computer name is required";
                isValid = false;
            }
            else if (!hostnameRegex.IsMatch(HostnameBox.Text))
            {
                HostnameError.Text = "Computer name must start with a letter and contain only lowercase letters, numbers, or hyphens";
                isValid = false;
            }
            else
            {
                HostnameError.Text = "";
            }

            NextButton.IsEnabled = isValid;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            SaveState();
            NavigationHelper.NavigateWithAnimation(
                NavigationService,
                new ResizeDisk(),
                TimeSpan.FromSeconds(0.3),
                slideLeft: false);
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            var accountInfo = new AccountInfo
            {
                Username = UsernameBox.Text,
                Password = PasswordBox.Password,
                ComputerName = HostnameBox.Text
            };

            App.Current.Properties["AccountInfo"] = accountInfo;
            SaveState();
            NavigationHelper.NavigateWithAnimation(NavigationService, new WarningConfirmation(), TimeSpan.FromSeconds(0.3));
        }

        private void PasswordBox_PreviewExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            // Block paste and copy commands on password boxes
            if (e.Command == ApplicationCommands.Paste ||
                e.Command == ApplicationCommands.Copy ||
                e.Command == ApplicationCommands.Cut)
            {
                e.Handled = true;
            }
        }
    }
}