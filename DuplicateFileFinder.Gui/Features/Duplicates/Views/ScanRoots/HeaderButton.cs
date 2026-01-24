using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Views.ScanRoots;

public static class HeaderButton
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, string?>("Text");

    public static readonly AttachedProperty<string?> ArrowProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, string?>("Arrow");

    public static readonly AttachedProperty<TextAlignment> TextAlignmentProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, TextAlignment>(
            "TextAlignment", TextAlignment.Left);

    public static void SetText(Control element, string? value) =>
        element.SetValue(TextProperty, value);

    public static string? GetText(Control element) =>
        element.GetValue(TextProperty);

    public static void SetArrow(Control element, string? value) =>
        element.SetValue(ArrowProperty, value);

    public static string? GetArrow(Control element) =>
        element.GetValue(ArrowProperty);

    public static void SetTextAlignment(Control element, TextAlignment value) =>
        element.SetValue(TextAlignmentProperty, value);

    public static TextAlignment GetTextAlignment(Control element) =>
        element.GetValue(TextAlignmentProperty);
}
