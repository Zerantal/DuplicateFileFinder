using Avalonia;
using Avalonia.Headless;

using DuplicateFileFinder.Gui;
using DuplicateFileFinder.GuiTests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace DuplicateFileFinder.GuiTests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
