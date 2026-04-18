using Avalonia.Controls;
using Avalonia.Headless.XUnit;

using DuplicateFileFinder.Gui.Features.Shell.Views;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Ui.Smoke;

public sealed class MainWindowSmokeTests
{
    [AvaloniaFact]
    public void MainWindow_HasKeyChildViews_ByName()
    {
        var win = new MainWindow();
        LayoutTestHelpers.DoLayout(win);

        Assert.NotNull(win.FindControl<Menu>("MainMenu"));
        Assert.NotNull(win.FindControl<Button>("ScanLocationButton"));
        Assert.NotNull(win.FindControl<Control>("DuplicatesView"));
        Assert.NotNull(win.FindControl<Control>("ToastHost"));
    }
}
