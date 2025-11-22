using Avalonia;
using Avalonia.Controls;

namespace DuplicateFileFinder.Gui.Controls;

public class SelectableBorder : Border
{
    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<SelectableBorder, bool>(nameof(IsSelected));

    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }
}