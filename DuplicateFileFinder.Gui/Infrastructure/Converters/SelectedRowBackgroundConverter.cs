// DuplicateFileFinder.Gui/Infrastructure/Converters/SelectedRowBackgroundConverter.cs

using System.Globalization;

using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DuplicateFileFinder.Gui.Infrastructure.Converters;

public sealed class SelectedRowBackgroundConverter : IMultiValueConverter
{
    public IBrush SelectedBrush { get; set; } = new SolidColorBrush(Color.Parse("#22FFFFFF"));
    public IBrush UnselectedBrush { get; set; } = Brushes.Transparent;

    // ReSharper disable once ReturnTypeCanBeNotNullable
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2)
            return UnselectedBrush;

        var row = values[0];
        var selected = values[1];

        return ReferenceEquals(row, selected) ? SelectedBrush : UnselectedBrush;
    }
}
