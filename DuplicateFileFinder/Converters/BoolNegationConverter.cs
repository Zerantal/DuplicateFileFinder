using System;
using System.Globalization;
using System.Windows.Data;

namespace DuplicateFileFinder.Converters
{
    [ValueConversion(typeof(bool), typeof(System.Windows.Visibility))]
    public class BoolNegationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (targetType != typeof(bool) || value == null)
                throw new InvalidOperationException("The target must be a boolean and not null");
            
            return !(bool)value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (targetType != typeof(bool) || value == null)
                throw new InvalidOperationException("The target must be a boolean and not null");
            
            return !(bool)value;
        }
    }
}
