// DuplicateFileFinder.Gui/Features/Shell/ViewModels/MainWindowViewModel.cs

using System.Collections.ObjectModel;
using System.Globalization;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicateGroups;
using DuplicateFileFinder.Gui.Infrastructure.Converters;
using DuplicateFileFinder.Gui.Infrastructure.Debug;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Toasts;

using DuplicateFileFinderLib.Repository.Interfaces;

using NLog;

namespace DuplicateFileFinder.Gui.Features.Shell.ViewModels;

public sealed record StatusItem(string Key, string Value);

public partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IRepoHost _repoHost;
    private readonly IDialogService _dialogService;
    private readonly IScanCoordinator _scanCoordinator;
    private readonly IToastService _toasts;

    private static readonly Logger s_log = LogManager.GetCurrentClassLogger();

    public ObservableCollection<StatusItem> StatusItems { get; } = new();

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
        DuplicatesViewModel duplicates)
    {
        _repoHost = host ?? throw new ArgumentNullException(nameof(host));
        _scanCoordinator = scanCoordinator ?? throw new ArgumentNullException(nameof(scanCoordinator));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        ToastHost = toastHost ?? throw new ArgumentNullException(nameof(toastHost));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        Duplicates = duplicates ?? throw new ArgumentNullException(nameof(duplicates));

        Duplicates.DuplicateGroups.Controller.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DuplicateGroupsController.FilesScanned) ||
                e.PropertyName is nameof(DuplicateGroupsController.DuplicatesFound) ||
                e.PropertyName is nameof(DuplicateGroupsController.WastedBytes))
            {
                UpdateStatusItems();
            }
        };

        UpdateStatusItems();

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

    private void UpdateStatusItems()
    {
        StatusItems.Clear();

        var c = Duplicates.DuplicateGroups.Controller;

        var filesScanned = c.FilesScanned.ToString("N0");
        var duplicatesFound = c.DuplicatesFound.ToString("N0");
        var wastedBytes = c.WastedBytes;

        var wastedBytesFormatted =
            (string?)BytesToHumanConverter.Instance.Convert(
                wastedBytes, typeof(string), null, CultureInfo.CurrentUICulture)
            ?? $"{wastedBytes:n0} bytes";

        StatusItems.Add(new StatusItem("Files scanned", filesScanned));
        StatusItems.Add(new StatusItem("Duplicates", duplicatesFound));
        StatusItems.Add(new StatusItem("Space wasted", wastedBytesFormatted));
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

    public async ValueTask DisposeAsync() => await _repoHost.DisposeAsync();
}
