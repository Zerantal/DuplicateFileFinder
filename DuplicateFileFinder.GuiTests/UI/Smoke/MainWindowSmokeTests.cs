using System.Threading.Tasks;

using Avalonia.Controls;

using DuplicateFileFinder.Gui.Features.Shell.Views;
using Xunit;

namespace DuplicateFileFinder.GuiTests.Ui.Smoke;

[Collection("AvaloniaUI")]
public sealed class MainWindowSmokeTests(AvaloniaHeadlessFixture ui)
{
    [Fact]
    public async Task MainWindow_HasKeyChildViews_ByName()
    {
        await ui.RunOnUiThreadAsync(() =>
        {
            var win = new MainWindow();
            LayoutTestHelpers.DoLayout(win);

            Assert.NotNull(win.FindControl<Menu>("MainMenu"));
            Assert.NotNull(win.FindControl<Button>("ScanLocationButton"));
            Assert.NotNull(win.FindControl<Control>("DuplicatesView"));
            Assert.NotNull(win.FindControl<Control>("ToastHost"));
        });
    }
}
