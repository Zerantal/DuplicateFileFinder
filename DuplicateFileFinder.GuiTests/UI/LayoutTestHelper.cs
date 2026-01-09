using Avalonia;
using Avalonia.Controls;

namespace DuplicateFileFinder.GuiTests.Ui;

internal static class LayoutTestHelpers
{
    public static void DoLayout(Control control, int width = 1200, int height = 800)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();
    }
}
