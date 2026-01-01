using System;
using System.Globalization;
using System.Windows.Data;

namespace LinuxGate.Pages
{
    public class ResponsiveFontConverter : IMultiValueConverter
    {
        public double SmallScreenSize { get; set; }
        public double DefaultSize { get; set; }

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values[0] is double width)
            {
                return width < 800 ? SmallScreenSize : DefaultSize;
            }
            return DefaultSize;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
