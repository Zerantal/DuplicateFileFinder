using System.Globalization;

using Avalonia.Data.Converters;

namespace DuplicateFileFinder.Gui.Infrastructure.Converters;

public sealed class OneThirdConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double h and > 0)
            return h / 3.0;

        return double.NaN;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
