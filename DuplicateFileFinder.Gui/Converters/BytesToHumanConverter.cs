using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace DuplicateFileFinder.Gui.Converters;

public sealed class BytesToHumanConverter : IValueConverter
{
    // Uses 1024 base: KB, MB, GB, TB. Change unit labels if you prefer KiB/MiB…
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return null;

        if (!TryToLong(value, out long bytes)) return value;

        if (bytes < 0) return "-" + Convert(-bytes, targetType, parameter, culture);

        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024.0;
            unit++;
        }

        // Default format: 0, 1 or 2 decimals depending on magnitude
        string formatted = size >= 100 ? size.ToString("N0", culture)
            : size >= 10 ? size.ToString("N1", culture)
            : size.ToString("N2", culture);

        return $"{formatted} {units[unit]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AvaloniaProperty.UnsetValue;

    private static bool TryToLong(object v, out long result)
    {
        switch (v)
        {
            case long l:
                result = l;
                return true;
            case int i:
                result = i;
                return true;
            case double d:
                result = (long)d;
                return true;
            case float f:
                result = (long)f;
                return true;
            case string s when long.TryParse(s, out var p):
                result = p;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}