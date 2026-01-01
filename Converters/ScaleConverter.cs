using System;
using System.Globalization;
using System.Windows.Data;

namespace LinuxGate.Converters
{
    public class ScaleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double number && parameter is string factor)
            {
                if (double.TryParse(factor, out double scaleFactor))
                {
                    return number * scaleFactor;
                }
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
