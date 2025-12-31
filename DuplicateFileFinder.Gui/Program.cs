using Avalonia;
using DuplicateFileFinderLib.Logging;
// using Avalonia.Wayland;
// using Avalonia.X11;
using NLog;

namespace DuplicateFileFinder.Gui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        LoggingSetup.Configure();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogManager.GetCurrentClassLogger().Fatal(e.ExceptionObject as Exception, "AppDomain crash");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogManager.GetCurrentClassLogger().Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        // ---- 2. Run Avalonia normally ----
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    private static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // .With(new WaylandPlatformOpeions(UseDBusFilePicker = false))
            // .With(new X11PlatformOptions {UseDBusFilePicker = false})
            .WithInterFont()
            .LogToTrace();
    }
}
