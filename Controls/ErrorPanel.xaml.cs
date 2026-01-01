using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace LinuxGate.Controls
{
    public partial class ErrorPanel : UserControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(ErrorPanel), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty MessageProperty =
            DependencyProperty.Register("Message", typeof(string), typeof(ErrorPanel), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty DetailsProperty =
            DependencyProperty.Register("Details", typeof(string), typeof(ErrorPanel), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty AdditionalDetailsProperty =
            DependencyProperty.Register("AdditionalDetails", typeof(string), typeof(ErrorPanel), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty ActionButtonTextProperty =
            DependencyProperty.Register("ActionButtonText", typeof(string), typeof(ErrorPanel), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty ActionCommandProperty =
            DependencyProperty.Register("ActionCommand", typeof(ICommand), typeof(ErrorPanel), new PropertyMetadata(null));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public string Message
        {
            get => (string)GetValue(MessageProperty);
            set => SetValue(MessageProperty, value);
        }

        public string Details
        {
            get => (string)GetValue(DetailsProperty);
            set => SetValue(DetailsProperty, value);
        }

        public string AdditionalDetails
        {
            get => (string)GetValue(AdditionalDetailsProperty);
            set => SetValue(AdditionalDetailsProperty, value);
        }

        public string ActionButtonText
        {
            get => (string)GetValue(ActionButtonTextProperty);
            set => SetValue(ActionButtonTextProperty, value);
        }

        public ICommand ActionCommand
        {
            get => (ICommand)GetValue(ActionCommandProperty);
            set => SetValue(ActionCommandProperty, value);
        }

        public ErrorPanel()
        {
            InitializeComponent();
        }

        private void Expander_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is Expander expander)
            {
                expander.BringIntoView();
            }
        }
    }
}
