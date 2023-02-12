using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Util;

namespace Deduplicator.Converters;

public class LongToByteStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var number = System.Convert.ToInt64(value);

        return StringUtil.FileSizeToString(number);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return DependencyProperty.UnsetValue;
    }
}