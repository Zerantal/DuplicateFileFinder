using System.Globalization;

using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace DuplicateFileFinder.Gui.Infrastructure.Converters;

public sealed class PercentToGridLengthConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            _ => 0d
        };

        percent = Math.Clamp(percent, 0d, 100d);
        var useRemaining = string.Equals(parameter?.ToString(), "remaining", StringComparison.OrdinalIgnoreCase);
        var starValue = useRemaining ? 100d - percent : percent;

        return new GridLength(starValue, GridUnitType.Star);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
