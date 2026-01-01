using System;
using System.Windows.Data;
namespace LinuxGate.Converters
{
    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is bool boolValue ? !boolValue : true;
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is bool boolValue ? !boolValue : true;
        }
    }
}