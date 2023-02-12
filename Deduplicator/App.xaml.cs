using System.Windows;
using DuplicateFileFinder.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deduplicator;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private readonly IHost _host;
    public App()
    {
        _host = new HostBuilder()
            .ConfigureServices((hostContext, services) =>
            {
                services.AddSingleton<MainWindow>();
            }).Build();
    }

    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        var mainWindow = _host.Services.GetService<MainWindow>();
        mainWindow?.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        using (_host)
        {
            await _host.StopAsync();
        }

        base.OnExit(e);
    }
}