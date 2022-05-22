using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DuplicateFileFinder.Converters;

public class LongToByteStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string[] sizes = {"B", "KB", "MB", "GB", "TB"};
        var number = System.Convert.ToInt64(value);
        double len = number;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";

    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return DependencyProperty.UnsetValue;
    }
}