using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DuplicateFileFinder.Gui.Features.Scanning.ViewModels;
using DuplicateFileFinder.Gui.Features.Scanning.Views;
using DuplicateFileFinder.Gui.Features.Shell.Views;
using NLog;
using MainWindowViewModel = DuplicateFileFinder.Gui.Features.Shell.ViewModels.MainWindowViewModel;

namespace DuplicateFileFinder.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
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
            var splashVm  = new SplashViewModel();
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
            // Let the splash actually render first
            await Task.Yield();

            splashVm.Message    = "Opening repository…";
            splashVm.SubMessage = "Please wait while we load data and run integrity checks.";

            var appName = "DuplicateFileFinder";
            var appDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                appName);
            var repoDir = Path.Combine(appDir, "repo");
            
            mainVm = await MainWindowViewModel.CreateMainWindowAsync(repoDir);
            if (mainVm == null)
                throw new InvalidOperationException("Failed to create MainWindowViewModel.");
        }
        catch (Exception ex)
        {
            // Show a friendly error on the splash window; you can add Retry/Exit buttons if you like
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                splashVm.Message    = "Failed to open repository";
                splashVm.SubMessage = ex.Message;
            });

            // Don’t proceed to main window
            return;
        }

        // Once repo is ready, create and show MainWindow, then close splash
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