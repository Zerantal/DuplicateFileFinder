using System.Globalization;

using Avalonia.Data.Converters;

namespace DuplicateFileFinder.Gui.Infrastructure.Converters;

public sealed class DepthToIndentWidthConverter : IValueConverter
{
    public double IndentSize { get; set; } = 14;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int d ? d * IndentSize : 0;

    // ReSharper disable once ReturnTypeCanBeNotNullable
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
