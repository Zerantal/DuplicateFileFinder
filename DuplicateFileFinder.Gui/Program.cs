using Avalonia;
// using Avalonia.Wayland;
// using Avalonia.X11;
using NLog;

namespace DuplicateFileFinder.Gui;

internal static class Program
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    [STAThread]
    public static void Main(string[] args)
    {
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error(e.Exception, "Unobserved Task exception");
            e.SetObserved(); // prevent default crash
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Logger.Fatal(e.ExceptionObject as Exception,
                "Unhandled AppDomain exception");
            // This will still terminate the app, but you get a log first
        };

        // ---- 2. Run Avalonia normally ----
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // .With(new WaylandPlatformOpeions(UseDBusFilePicker = false))
            // .With(new X11PlatformOptions {UseDBusFilePicker = false})
            .WithInterFont()
            .LogToTrace();
}
