using System.Globalization;

using Avalonia.Data.Converters;

namespace DuplicateFileFinder.Gui.Infrastructure.Converters;

public sealed class EnumEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return false;

        var valueType = value.GetType();
        if (!valueType.IsEnum)
            return false;

        try
        {
            var parameterText = parameter.ToString();
            if (string.IsNullOrWhiteSpace(parameterText))
                return false;

            var parsed = Enum.Parse(valueType, parameterText, ignoreCase: true);
            return Equals(value, parsed);
        }
        catch
        {
            return false;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
