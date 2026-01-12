using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;
using DuplicateFileFinder.Gui.Features.Shell.ViewModels;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Toasts;

using DuplicateFileFinderLib.Repository.Interfaces;

using Microsoft.Extensions.DependencyInjection;

namespace DuplicateFileFinder.Gui.Infrastructure.Bootstrapper;

public static class GuiBootstrapper
{
    public static ServiceProvider BuildServiceProvider(IRepoHost host)
    {
        var services = new ServiceCollection();

        // ---- Repo host ----
        services.AddSingleton(host);

        // ---- Infrastructure services ----
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFileSystemDeleteService, FileSystemDeleteService>();

        // ---- Scan engine + coordinator ----
        services.AddSingleton(_ => new DuplicateFileFinderLib.Core.DuplicateFileFinder(host));
        services.AddSingleton<IScanCoordinator>(sp =>
            new ScanCoordinator(
                host,
                sp.GetRequiredService<DuplicateFileFinderLib.Core.DuplicateFileFinder>(),
                sp.GetRequiredService<IDialogService>()));

        // ---- Toasts ----
        services.AddSingleton<ToastHostViewModel>();
        services.AddSingleton<IToastService>(sp =>
            new ToastService(
                sp.GetRequiredService<ToastHostViewModel>(),
                defaultDuration: TimeSpan.FromSeconds(3),
                maxVisible: 4));

        // ---- Feature VMs ----
        services.AddSingleton<DuplicatesViewModel>();

        // ---- Shell VM ----
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }
}
