// DuplicateFileFinder.Gui/Features/Shell/ViewModels/MainWindowViewModel.cs

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Interfaces;
using NLog;
using DuplicatesViewModel = DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicatesViewModel;

namespace DuplicateFileFinder.Gui.Features.Shell.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IRepoHost? _repoHost;

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IDialogService _dialogService;

    private readonly IScanCoordinator _scanCoordinator;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanLocationCommand))]
    private bool _isScanning;

    /// <inheritdoc/>
    private MainWindowViewModel(IRepoHost host,
        IScanCoordinator scanCoordinator,
        IDialogService dialogService)
    {
        _repoHost = host;
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _scanCoordinator = scanCoordinator ?? throw new ArgumentNullException(nameof(scanCoordinator));
        Duplicates = new DuplicatesViewModel(host, scanCoordinator);

        _scanCoordinator.ScanCompleted += (_, _) =>
        {
            // The scan body has finished; indexes may still be rebuilding asynchronously.
            IsScanning = false;
        };

        host.IndexesRebuilt += (_, _) =>
        {
            // Only reload once the index plugins have processed the corresponding generation.
            if (Dispatcher.UIThread.CheckAccess())
                Duplicates.LoadFromRepo();
            else
            {
                Dispatcher.UIThread.InvokeAsync(() => Duplicates.LoadFromRepo());
            }
        };
    }

    public DuplicatesViewModel? Duplicates { get; set; }

    public bool CanStartScan => !IsScanning && !(_scanCoordinator is { IsScanning: true });

    // ---------------- Commands ----------------

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task ScanLocationAsync()
    {
        var path = await _dialogService.ShowFolderPickerDialogAsync("Scan location...");
        if (string.IsNullOrWhiteSpace(path))
            return;

        await StartScan(path);
    }

    // ---------------- Scan orchestration ----------------

    private async Task StartScan(string path)
    {
        if (IsScanning || _scanCoordinator.IsScanning)
            return;

        Log.Info("Initialising scan of {path}", path);
        IsScanning = true;

        await _scanCoordinator.RunScanWithDialogAsync(path);
    }

    public static async Task<MainWindowViewModel?> CreateMainWindowAsync(string repoDir)
    {
        MainWindowViewModel? mainWindowVm;
        try
        {
            var host = await RepoHost.OpenAsync(repoDir);

            var dialogService = new DialogService();
            var scanEngine = new DuplicateFileFinderLib.Core.DuplicateFileFinder(host);
            var scanCoordinator = new ScanCoordinator(host, scanEngine, dialogService);

            mainWindowVm = new MainWindowViewModel(host, scanCoordinator, dialogService);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            throw;
        }

        // mainWindowVm responsible for disposing of RepoHost (called via MainWindow.OnClosed)
        return mainWindowVm;
    }

    public async ValueTask DisposeAsync()
    {
        if (_repoHost != null) await _repoHost.DisposeAsync();
    }
}
