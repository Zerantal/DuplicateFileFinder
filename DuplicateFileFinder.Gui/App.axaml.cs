using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using DuplicateFileFinder.Gui.Features.Shell.Views;
using DuplicateFileFinder.Gui.Features.Splash.ViewModels;
using DuplicateFileFinder.Gui.Features.Splash.Views;
using DuplicateFileFinder.Gui.Infrastructure.Bootstrapper;

using DuplicateFileFinderLib.Repository.Core;

using Microsoft.Extensions.DependencyInjection;

using NLog;

using MainWindowViewModel = DuplicateFileFinder.Gui.Features.Shell.ViewModels.MainWindowViewModel;

namespace DuplicateFileFinder.Gui;

public partial class App : Application
{
    public static readonly string AppDir;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    static App()
    {
        var appName = "DuplicateFileFinder";
        AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appName);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            LogManager.GetCurrentClassLogger().Error(e.Exception, "UI thread exception");
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splashVm = new SplashViewModel();
            var splashWin = new SplashWindow { DataContext = splashVm };

            desktop.MainWindow = splashWin;
            splashWin.Show();

            // fire-and-forget, but UI-safe
            _ = LoadRepoAndShowMainWindowAsync(desktop, splashWin, splashVm);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task LoadRepoAndShowMainWindowAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window splashWindow,
        SplashViewModel splashVm)
    {
        MainWindowViewModel? mainVm;

        try
        {
            await Task.Yield();

            splashVm.Message = "Opening repository…";
            splashVm.SubMessage = "Please wait while we load data and run integrity checks.";

            var repoDir = Path.Combine(AppDir, "repo");

            // Open repo (async), then build container and resolve shell VM.
            var host = await RepoHost.OpenAsync(repoDir);
            var sp = GuiBootstrapper.BuildServiceProvider(host);

            mainVm = sp.GetRequiredService<MainWindowViewModel>();
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                splashVm.Message = "Failed to open repository";
                splashVm.SubMessage = ex.Message;
            });
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var mainWindow = new MainWindow
            {
                DataContext = mainVm
            };

            desktop.MainWindow = mainWindow;
            mainWindow.Show();

            splashWindow.Close();
        });
    }
}
