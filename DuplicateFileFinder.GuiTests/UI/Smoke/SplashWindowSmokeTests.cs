using System.Threading.Tasks;

using Avalonia.Controls;

using DuplicateFileFinder.Gui.Features.Splash.ViewModels;
using DuplicateFileFinder.Gui.Features.Splash.Views;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Ui.Smoke;

[Collection("AvaloniaUI")]
public sealed class SplashWindowSmokeTests(AvaloniaHeadlessFixture ui)
{
    [Fact]
    public async Task SplashWindow_BindsMessageAndSubMessage()
    {
        await ui.RunOnUiThreadAsync(() =>
        {
            var vm = new SplashViewModel
            {
                Message = "Loading X",
                SubMessage = "Doing Y"
            };

            var win = new SplashWindow
            {
                DataContext = vm
            };

            LayoutTestHelpers.DoLayout(win, 500, 300);

            Assert.Equal("Loading X", win.FindControl<TextBlock>("MessageText")!.Text);
            Assert.Equal("Doing Y", win.FindControl<TextBlock>("SubMessageText")!.Text);
            Assert.NotNull(win.FindControl<ProgressBar>("LoadingBar"));
        });
    }
}
