// DuplicateFileFinder.Gui/Features/Shell/ViewModels/MainWindowViewModel.cs

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Infrastructure.Debug;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Toasts;

using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Interfaces;

using NLog;

using DuplicatesViewModel = DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicatesViewModel;

namespace DuplicateFileFinder.Gui.Features.Shell.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IRepoHost _repoHost;

    private static readonly Logger s_log = LogManager.GetCurrentClassLogger();

    private readonly IDialogService _dialogService;
    private readonly IScanCoordinator _scanCoordinator;
    private readonly IToastService _toasts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanLocationCommand))]
    private bool _isScanning;

    public ToastHostViewModel ToastHost { get; }

    /// <inheritdoc/>
    private MainWindowViewModel(IRepoHost host,
        IScanCoordinator scanCoordinator,
        IDialogService dialogService,
        ToastHostViewModel toastHost,
        IToastService toasts)
    {
        _repoHost = host;
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _scanCoordinator = scanCoordinator ?? throw new ArgumentNullException(nameof(scanCoordinator));
        ToastHost = toastHost ?? throw new ArgumentNullException(nameof(toastHost));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));

        Duplicates = new DuplicatesViewModel(host, scanCoordinator, dialogService);

        _scanCoordinator.ScanCompleted += (_, e) =>
        {
            // The scan body has finished; indexes may still be rebuilding asynchronously.
            IsScanning = false;

            if (e.Error is not null)
                _toasts.Show($"Scan failed: {e.Error.Message}", ToastKind.Error, TimeSpan.FromSeconds(6));
            else if (e.Cancelled)
                _toasts.Show("Scan cancelled.", ToastKind.Warning);
            else
                _toasts.Show("Scan completed.", ToastKind.Success);
        };

        host.IndexesRebuilt += (_, _) =>
        {
            // Only reload once the index plugins have processed the corresponding generation.
            if (Dispatcher.UIThread.CheckAccess())
                Duplicates.LoadFromRepo();
            else
                Dispatcher.UIThread.InvokeAsync(() => Duplicates.LoadFromRepo());
        };
    }

    public DuplicatesViewModel Duplicates { get; set; }

    public bool CanStartScan => !IsScanning && !(_scanCoordinator is { IsScanning: true });

    public bool IsDebugBuild
    {
        get
        {
#if DEBUG
            return true;
#else
        return false;
#endif
        }
    }

    // ---------------- Commands ----------------

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task ScanLocationAsync()
    {
        var path = await _dialogService.ShowFolderPickerDialogAsync("Scan location...");
        if (string.IsNullOrWhiteSpace(path))
            return;

        await StartScan(path);
    }

    [RelayCommand]
    private async Task DumpAllRepoTreesAsync()
    {
        var path = await RepoTreeDumper.DumpAsync(_repoHost, false, CancellationToken.None);

        _toasts.Show($"Repo tree dumped: {path}.", ToastKind.Success);
    }

    [RelayCommand]
    private async Task DumpLiveRepoTreesAsync()
    {
        var path = await RepoTreeDumper.DumpAsync(_repoHost, true, CancellationToken.None);

        _toasts.Show($"Repo tree dumped: {path}.", ToastKind.Success);
    }

    // ---------------- Scan orchestration ----------------

    private async Task StartScan(string path)
    {
        if (IsScanning || _scanCoordinator.IsScanning)
            return;

        s_log.Info("Initialising scan of {path}", path);
        IsScanning = true;

        _toasts.Show($"Scanning: {path}");

        await _scanCoordinator.RunScanNewLocationWithDialogAsync(path);
    }

    public static async Task<MainWindowViewModel?> CreateMainWindowAsync(string repoDir)
    {
        try
        {
            var host = await RepoHost.OpenAsync(repoDir);

            var dialogService = new DialogService();
            var scanEngine = new DuplicateFileFinderLib.Core.DuplicateFileFinder(host);
            var scanCoordinator = new ScanCoordinator(host, scanEngine, dialogService);

            // Toasts: create host VM + service here (composition root)
            var toastHost = new ToastHostViewModel();
            var toastService = new ToastService(toastHost, defaultDuration: TimeSpan.FromSeconds(3), maxVisible: 4);

            return new MainWindowViewModel(host, scanCoordinator, dialogService, toastHost, toastService);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            throw;
        }
    }

    public async ValueTask DisposeAsync() => await _repoHost.DisposeAsync();
}
