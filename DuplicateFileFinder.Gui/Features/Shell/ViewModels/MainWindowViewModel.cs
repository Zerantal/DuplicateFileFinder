// DuplicateFileFinder.Gui/Features/Shell/ViewModels/MainWindowViewModel.cs

using System.Collections.ObjectModel;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;
using DuplicateFileFinder.Gui.Infrastructure.Debug;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Status;
using DuplicateFileFinder.Gui.Infrastructure.Toasts;
using DuplicateFileFinder.Gui.Infrastructure.Util;

using DuplicateFileFinderLib.Repository.Interfaces;

using NLog;

namespace DuplicateFileFinder.Gui.Features.Shell.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly Logger s_log = LogManager.GetCurrentClassLogger();

    private readonly IRepoHost _repoHost;
    private readonly IDialogService _dialogService;
    private readonly IScanCoordinator _scanCoordinator;
    private readonly IToastService _toasts;

    private readonly ObservableCollection<StatusItem> _statusItems = new();
    public ReadOnlyObservableCollection<StatusItem> StatusItems { get; }

    private readonly List<IStatusProvider> _statusProviders = new();
    private readonly DisposableManager _disposer;

    public ToastHostViewModel ToastHost { get; }

    public DuplicatesViewModel Duplicates { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanLocationCommand))]
    private bool _isScanning;

    public MainWindowViewModel(
        IRepoHost host,
        IScanCoordinator scanCoordinator,
        IDialogService dialogService,
        ToastHostViewModel toastHost,
        IToastService toasts,
        DuplicatesViewModel duplicates,
        DisposableManager disposer)
    {
        _repoHost = host ?? throw new ArgumentNullException(nameof(host));
        _scanCoordinator = scanCoordinator ?? throw new ArgumentNullException(nameof(scanCoordinator));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        ToastHost = toastHost ?? throw new ArgumentNullException(nameof(toastHost));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        Duplicates = duplicates ?? throw new ArgumentNullException(nameof(duplicates));
        _disposer =  disposer ?? throw new ArgumentNullException(nameof(disposer));

        // Scan completed -> toast + IsScanning
        EventHandler<ScanCompletedEventArgs> scanCompleted = (_, e) =>
        {
            IsScanning = false;

            if (e.Error is not null)
                _toasts.Show($"Scan failed: {e.Error.Message}", ToastKind.Error, TimeSpan.FromSeconds(6));
            else if (e.Cancelled)
                _toasts.Show("Scan cancelled.", ToastKind.Warning);
            else
                _toasts.Show("Scan completed.", ToastKind.Success);
        };
        _scanCoordinator.ScanCompleted += scanCompleted;
        _disposer.Add(() => _scanCoordinator.ScanCompleted -= scanCompleted);

        // Indexes rebuilt -> reload duplicates
        EventHandler<RepoIndexesRebuiltEventArgs> indexesRebuilt = (_, _) =>
        {
            if (Dispatcher.UIThread.CheckAccess())
                Duplicates.LoadFromRepo();
            else
                Dispatcher.UIThread.InvokeAsync(() => Duplicates.LoadFromRepo());
        };
        _repoHost.IndexesRebuilt += indexesRebuilt;
        _disposer.Add(() => _repoHost.IndexesRebuilt -= indexesRebuilt);

        StatusItems = new ReadOnlyObservableCollection<StatusItem>(_statusItems);
        RegisterStatusProvider(duplicates.DuplicateGroups);
        RebuildStatusItems();
    }

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

    // ----------------------------- helper methods --------------
    private void RegisterStatusProvider(IStatusProvider provider)
    {
        _statusProviders.Add(provider);

        EventHandler handler = (_, _) => RebuildStatusItems();
        provider.StatusChanged += handler;

        _disposer.Add(() => provider.StatusChanged -= handler);
    }

    private void RebuildStatusItems()
    {
        _statusItems.Clear();

        foreach (var p in _statusProviders)
        {
            foreach (var item in p.GetStatusItems())
                _statusItems.Add(item);
        }
    }

    // Dispose

    public async ValueTask DisposeAsync()
    {
        _disposer.Dispose();
        await _repoHost.DisposeAsync();
    }
}
