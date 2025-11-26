// Gui/Services/ScanCoordinator.cs

using Avalonia.Threading;
using DuplicateFileFinder.Gui.ViewModels;
using DuplicateFileFinder.Gui.Views;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using NLog;
using Dff = DuplicateFileFinderLib.Core;

namespace DuplicateFileFinder.Gui.Services;

public sealed class ScanCoordinator : IScanCoordinator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly IDialogService _dialogService;
    private readonly Dff.DuplicateFileFinder _finder;

    private readonly Repo _repo;

    private CancellationTokenSource? _cts;

    public ScanCoordinator(
        Repo repo,
        Dff.DuplicateFileFinder finder,
        IDialogService dialogService)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _finder = finder ?? throw new ArgumentNullException(nameof(finder));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    public event EventHandler<Dff.DuplicateFileFinderProgressReport>? ProgressChanged;
    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;

    public bool IsScanning { get; private set; }

    public async Task RunScanWithDialogAsync(
        string rootPath,
        ScanMode mode = ScanMode.Full,
        CancellationToken cancellationToken = default)
    {
        // Ensure we start from UI thread; if not, hop there once.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => RunScanWithDialogAsync(rootPath, mode, cancellationToken));
            return;
        }

        if (IsScanning)
            return;

        IsScanning = true;
        Log.Info("Starting {mode} scan of {root}", mode, rootPath);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // ViewModel + dialog
        var progressVm = new ScanProgressViewModel(this);
        var dialog = new ScanProgressWindow
        {
            DataContext = progressVm
        };

        // Progress created on UI thread → callbacks marshalled back to UI thread.
        void HandleProgress(Dff.DuplicateFileFinderProgressReport report)
        {
            ProgressChanged?.Invoke(this, report);
            progressVm.Update(report);
        }

        var progress = new Progress<Dff.DuplicateFileFinderProgressReport>(HandleProgress);

        var owner = _dialogService.GetOwnerWindow();
        var dialogTask = dialog.ShowDialog(owner); // modal; returns Task that completes when closed

        Exception? error = null;
        var cancelled = false;

        // Run the scan body completely off the UI thread.
        var scanTask = Task.Run(async () =>
        {
            try
            {
                await _finder.ScanLocationAsync(
                        rootPath,
                        mode,
                        progress,
                        _cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                Log.Info("Scan cancelled for {root}", rootPath);
            }
            catch (Exception ex)
            {
                error = ex;
                Log.Error(ex, "Scan failed for {root}", rootPath);
            }
        }, _cts.Token);

        // Wait for scan to finish (still on background thread for scan body)
        await scanTask.ConfigureAwait(false);

        // Back to UI thread for dialog + events
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                if (dialog.IsVisible)
                    dialog.Close();
            }
            catch
            {
                // ignore close errors
            }

            IsScanning = false;

            ScanCompleted?.Invoke(
                this,
                new ScanCompletedEventArgs(
                    rootPath,
                    cancelled,
                    error));
        });

        // Ensure the dialog has actually finished closing
        await dialogTask;

        // Propagate error / cancellation to caller if they care
        if (error is not null)
            throw error;
        if (cancelled)
            throw new OperationCanceledException();
    }

    public Task RemoveScanRootAsync(string rootPath)
    {
        _repo.RemoveScanRoot(rootPath);

        // Raise completion on UI thread so handlers can touch UI safely.
        if (Dispatcher.UIThread.CheckAccess())
            ScanCompleted?.Invoke(
                this,
                new ScanCompletedEventArgs(rootPath, false, null));
        else
            Dispatcher.UIThread.Post(() =>
            {
                ScanCompleted?.Invoke(
                    this,
                    new ScanCompletedEventArgs(rootPath, false, null));
            });

        return Task.CompletedTask;
    }

    public void CancelScan()
    {
        if (!IsScanning)
            return;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // best-effort
        }
    }
}