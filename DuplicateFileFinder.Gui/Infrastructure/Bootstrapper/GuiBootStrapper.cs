using DuplicateFileFinder.Gui.Features.Duplicates.Application;
using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicateGroups;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinder.Gui.Features.Shell.ViewModels;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Toasts;
using DuplicateFileFinder.Gui.Infrastructure.Util;

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
        services.AddSingleton<DisposableManager>();

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

        // Application
        services.AddSingleton<IDuplicateFileDeletionService, DuplicateFileDeletionService>();

        // Duplicate groups
        services.AddSingleton<DuplicateGroupsViewModel>();
        services.AddSingleton<DuplicateGroupsController>();

        // Scan roots tree + builder
        services.AddSingleton<FolderNodeViewModelFactory>();
        services.AddSingleton<IScanRootsTreeNodeActions, ScanRootsTreeNodeActions>();
        services.AddSingleton<ScanRootsTreeBuilder>();
        services.AddSingleton<ScanRootsTreeViewModel>();

        // Tree map + actions (+ builder if you have one)
        services.AddSingleton<TreeMapActionsViewModel>();
        services.AddSingleton<TreeMapController>(sp =>
        {
            var h = sp.GetRequiredService<IRepoHost>();
            var tm = new TreeMapController(h)
            {
                Options = new TreeMapBuildOptions { MaxDepth = 8 }
            };
            return tm;
        });

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
