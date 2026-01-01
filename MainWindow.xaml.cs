using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using LinuxGate.Helpers;
using LinuxGate.Pages;

namespace LinuxGate
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Detect Windows language and set as default
            string windowsLang = Localization.GetWindowsLanguageCode();
            int langIndex = 0; // Default to English

            switch (windowsLang)
            {
                case "en": langIndex = 0; break;
                case "fr": langIndex = 1; break;
                case "es": langIndex = 2; break;
                case "ja": langIndex = 3; break;
            }

            LanguageComboBox.SelectedIndex = langIndex;
            Localization.SetLanguage(windowsLang);

/*#if DEBUG
            DebugPanel.Visibility = Visibility.Visible;
#endif*/
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            StateManager.ClearState("ChooseDistro"); // Clear state when starting fresh
            NavigationHelper.NavigateWithAnimationInFrame(
                MainFrame,
                new ChooseDistro(),
                TimeSpan.FromSeconds(0.3));
        }

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LanguageComboBox.SelectedItem is ComboBoxItem item)
            {
                string cultureName = item.Tag.ToString();
                Localization.SetLanguage(cultureName);
            }
        }

        private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item)
            {
                string lang = (string)item.Tag;
                Localization.SetLanguage(lang);
            }
        }

        private void DebugPageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DebugPageComboBox.SelectedItem is ComboBoxItem item)
            {
                string pageName = item.Tag.ToString();
                Page targetPage = null;

                switch (pageName)
                {
                    case "ChooseDistro":
                        targetPage = new ChooseDistro();
                        break;
                    case "ResizeDisk":
                        targetPage = new ResizeDisk();
                        break;
                    case "AccountCreation":
                        targetPage = new AccountCreation();
                        break;
                    case "WarningConfirmation":
                        targetPage = new WarningConfirmation();
                        break;
                    case "ApplyChanges":
                        targetPage = new ApplyChanges();
                        break;
                }

                if (targetPage != null)
                {
                    NavigationHelper.NavigateWithAnimationInFrame(
                        MainFrame,
                        targetPage,
                        TimeSpan.FromSeconds(0.3));
                }
            }
        }
    }
}