using System;
using System.Windows;
using System.Windows.Controls;
using LinuxGate.Helpers;

namespace LinuxGate.Pages
{
    public partial class WarningConfirmation : Page
    {
        public WarningConfirmation()
        {
            InitializeComponent();
        }

        private void ConfirmCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            ConfirmButton.IsEnabled = ConfirmCheckBox.IsChecked == true;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            NavigationHelper.NavigateWithAnimation(
                NavigationService,
                new AccountCreation(),
                TimeSpan.FromSeconds(0.3),
                slideLeft: false);
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            NavigationHelper.NavigateWithAnimation(
                NavigationService,
                new ApplyChanges(),
                TimeSpan.FromSeconds(0.3));
        }
    }
}