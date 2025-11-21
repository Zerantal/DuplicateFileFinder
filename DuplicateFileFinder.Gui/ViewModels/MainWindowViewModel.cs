using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuplicateFileFinder.Gui.Services;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository;
using NLog;
using Dff = DuplicateFileFinderLib.Core;

namespace DuplicateFileFinder.Gui.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly Dff.DuplicateFileFinder _engine;

    private readonly DialogService _dialogService;

    // guard to prevent file scanning updating UI after it's finished
    private bool _finalized;
    [ObservableProperty] private bool _isIndeterminateProgressPhase;

    // Observable properties
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopScanCommand))]
    private bool _isScanning;

    [ObservableProperty] private string _operation = string.Empty;

    // cancellation source for the current scan (null when idle)
    private CancellationTokenSource? _scanCts;

    [ObservableProperty] private int _scanProgress;
    
    public MainWindowViewModel(Repo repo, DialogService dialogService)
    {
        Duplicates = new DuplicatesViewModel(repo);
        _engine = new Dff.DuplicateFileFinder(repo);

        _dialogService = dialogService;

        IsScanning = false;
    }

    public DuplicatesViewModel Duplicates { get; }

    public bool CanStartScan => !IsScanning;

    // ---------------- Commands ----------------

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task ScanLocation()
    {
        var path = await _dialogService.ShowOpenFolderDialogAsync("Scan location...");
        if (string.IsNullOrWhiteSpace(path)) return;

        using (ScanLog.BeginScanScope(path))
        {
            await StartScan(path);
        }
    }

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void StopScan()
    {
        _scanCts?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task OptimizeRepo()
    {
        try
        {
            await Duplicates.OptimizeRepoAsync();
            await _dialogService.ShowInfoAsync("Repository optimized", "The repository has been compacted.");

        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Failed to optimize repository", ex.Message);
        }
    }

    // ---------------- Private helper methods ----------------

    private async Task StartScan(string path)
    {
        var scanInterrupted = false;

        Log.Info("Initialising scan of {path}", path);

        if (IsScanning) return;

        _finalized = false;

        // Reset UI state            
        ScanProgress = 0;
        Operation = "Preparing scan...";
        IsScanning = true;

        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        // Progress from the library → UI
        var progress = new Progress<Dff.DuplicateFileFinderProgressReport>(report =>
        {
            if (_finalized) return;

            if (!string.IsNullOrWhiteSpace(report.StatusMessage))
                Operation = report.StatusMessage;

            IsIndeterminateProgressPhase = report.IsIndeterminate;

            if (!report.IsIndeterminate)
                ScanProgress = (int)Math.Clamp(report.PercentComplete * 100.0, 0, 100);
        });

        try
        {
            await Task.Run(async () =>
            {
                await _engine.ScanLocation(path, progress, token)
                    .ConfigureAwait(false);
            }, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Operation = "Scan cancelled.";
            scanInterrupted = true;
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;

            _finalized = true;
            IsScanning = false;
            ScanProgress = 0;
            IsIndeterminateProgressPhase = false;
            if (!scanInterrupted) Operation = "Finished scanning";
        }
    }
}