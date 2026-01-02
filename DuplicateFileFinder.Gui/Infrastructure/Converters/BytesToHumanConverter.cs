using System.Globalization;

using Avalonia;
using Avalonia.Data.Converters;

namespace DuplicateFileFinder.Gui.Infrastructure.Converters;

public sealed class BytesToHumanConverter : IValueConverter
{
    public static readonly BytesToHumanConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return null;

        if (!TryToLong(value, out var bytes))
            return value;

        if (bytes < 0)
            return "-" + Convert(-bytes, targetType, parameter, culture);

        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024.0;
            unit++;
        }

        var formatted = size.ToString("N1", culture);
        return $"{formatted} {units[unit]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return AvaloniaProperty.UnsetValue;

        var s = value as string ?? value.ToString() ?? string.Empty;
        s = s.Trim();
        if (s.Length == 0)
            return 0L;

        // Split into numeric + optional unit
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return 0L;

        var numberPart = parts[0];
        var unitPart = parts.Length > 1 ? parts[1] : "B";

        if (!double.TryParse(numberPart, NumberStyles.Float | NumberStyles.AllowThousands, culture, out var number))
            return AvaloniaProperty.UnsetValue;

        var power = unitPart.ToUpperInvariant() switch
        {
            "B" or "BYTE" or "BYTES" => 0,
            "KB" or "KIB" => 1,
            "MB" or "MIB" => 2,
            "GB" or "GIB" => 3,
            "TB" or "TIB" => 4,
            "PB" or "PIB" => 5,
            _ => 0 // unknown unit → treat as bytes
        };

        var factor = Math.Pow(1024, power);
        var bytesDouble = number * factor;

        if (double.IsNaN(bytesDouble) || double.IsInfinity(bytesDouble) ||
            bytesDouble < 0 || bytesDouble > long.MaxValue)
            return AvaloniaProperty.UnsetValue;

        var bytes = (long)Math.Round(bytesDouble);

        // Coerce to requested target type if sensible
        if (targetType == typeof(long) || targetType == typeof(object))
            return bytes;
        if (targetType == typeof(int))
            return (int)Math.Clamp(bytes, int.MinValue, int.MaxValue);
        if (targetType == typeof(double))
            return (double)bytes;

        return bytes; // default
    }

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
