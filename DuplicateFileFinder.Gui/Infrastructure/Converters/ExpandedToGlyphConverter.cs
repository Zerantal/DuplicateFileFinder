using System.Globalization;

using Avalonia.Data.Converters;
// ReSharper disable ReturnTypeCanBeNotNullable

namespace DuplicateFileFinder.Gui.Infrastructure.Converters;

public sealed class ExpandedToGlyphConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "▾" : "▸";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
