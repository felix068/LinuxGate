using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
namespace LinuxGate.Converters
{
    public class VisibilityToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility visibility && visibility == Visibility.Visible;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool boolValue && boolValue
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }
}