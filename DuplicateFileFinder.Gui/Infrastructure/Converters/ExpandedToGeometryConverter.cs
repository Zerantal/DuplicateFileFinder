using System.Globalization;

using Avalonia.Data.Converters;
using Avalonia.Media;
// ReSharper disable ReturnTypeCanBeNotNullable

namespace DuplicateFileFinder.Gui.Infrastructure.Converters;

public sealed class ExpandedToGeometryConverter : IValueConverter
{
    public static readonly ExpandedToGeometryConverter Instance = new();

    private static readonly Geometry CollapsedGeometry =
        Geometry.Parse("M 2 1 L 6 5 L 2 9");

    private static readonly Geometry ExpandedGeometry =
        Geometry.Parse("M 1 2 L 5 6 L 9 2");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? ExpandedGeometry : CollapsedGeometry;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
