// DuplicateFileFinder.Gui/Infrastructure/Services/ScanCoordinator.cs

using Avalonia.Threading;

using DuplicateFileFinder.Gui.Features.Scanning.Views;

using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;

using NLog;

using Dff = DuplicateFileFinderLib.Core;
using ScanProgressViewModel = DuplicateFileFinder.Gui.Features.Scanning.ViewModels.ScanProgressViewModel;

namespace DuplicateFileFinder.Gui.Infrastructure.Services;

public sealed class ScanCoordinator(
    IRepoHost host,
    Dff.DuplicateFileFinder finder,
    IDialogService dialogService)
    : IScanCoordinator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IDialogService _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    private readonly IRepoHost _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly Dff.DuplicateFileFinder _finder = finder ?? throw new ArgumentNullException(nameof(finder));

    private CancellationTokenSource? _cts;

    public event EventHandler<Dff.DuplicateFileFinderProgressReport>? ProgressChanged;
    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;

    public bool IsScanning { get; private set; }

    public Task SetScanRootDisplayName(long scanRootId, string? displayName)
        => _host.Repo.SetScanRootDisplayNameAsync(scanRootId, displayName);

    public Task RunScanNewLocationWithDialogAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path is required.", nameof(rootPath));

        return RunScanWithDialogCoreAsync(
            arg: rootPath,
            cancellationToken,
            runAsync: (progress, ct) => _finder.FullScanAsync(rootPath, progress, ct),
            logStart: () => Log.Info("Starting scan of {root}", rootPath),
            logCancel: () => Log.Info("Scan cancelled for {root}", rootPath),
            logFail: ex => Log.Error(ex, "Scan failed for {root}", rootPath));
    }

    public Task RunRescanLocationWithDialogAsync(long scanRootId, CancellationToken cancellationToken = default)
    {
        if (scanRootId < 0)
            throw new ArgumentException("Valid ScanRootId is required.", nameof(scanRootId));

        return RunScanWithDialogCoreAsync(
            arg: scanRootId,
            cancellationToken,
            runAsync: (progress, ct) => _finder.FullScanAsync(scanRootId, progress, ct),
            logStart: () => Log.Info("Starting rescan of location {root}", scanRootId),
            logCancel: () => Log.Info("Location rescan cancelled for {root}", scanRootId),
            logFail: ex => Log.Error(ex, "Location rescan failed for {root}", scanRootId));
    }

    public Task RunFolderRescanWithDialogAsync(DirHandle startDir, CancellationToken cancellationToken = default)
    {
        if (!startDir.IsValid)
            throw new ArgumentException("DirHandle is not valid.", nameof(startDir));

        return RunScanWithDialogCoreAsync(
            arg: startDir,
            cancellationToken,
            runAsync: (progress, ct) => _finder.FullScanAsync(startDir, progress, ct),
            logStart: () => Log.Info("Starting folder rescan of {dir}", startDir),
            logCancel: () => Log.Info("Folder rescan cancelled for {dir}", startDir),
            logFail: ex => Log.Error(ex, "Folder rescan failed for {dir}", startDir));
    }

    private async Task RunScanWithDialogCoreAsync(
        object arg,
        CancellationToken cancellationToken,
        Func<IProgress<Dff.DuplicateFileFinderProgressReport>, CancellationToken, Task> runAsync,
        Action logStart,
        Action logCancel,
        Action<Exception> logFail)
    {
        // Ensure we run the UI bits on UI thread exactly once.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                RunScanWithDialogCoreAsync(arg, cancellationToken, runAsync, logStart, logCancel, logFail));
            return;
        }

        if (IsScanning)
            return;

        IsScanning = true;
        logStart();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cts = cts;

        var progressVm = new ScanProgressViewModel(this);
        var dialog = new ScanProgressWindow { DataContext = progressVm };

        void HandleProgress(Dff.DuplicateFileFinderProgressReport report)
        {
            ProgressChanged?.Invoke(this, report);
            progressVm.Update(report);
        }

        // Constructed on UI thread => progress callbacks marshal to UI thread
        var progress = new Progress<Dff.DuplicateFileFinderProgressReport>(HandleProgress);

        var owner = _dialogService.GetOwnerWindow();
        var dialogTask = dialog.ShowDialog(owner);

        Exception? error = null;
        var cancelled = false;

        try
        {
            // Off-UI execution
            await Task.Run(async () =>
            {
                try
                {
                    await runAsync(progress, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    logCancel();
                }
                catch (Exception ex)
                {
                    error = ex;
                    logFail(ex);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Always close dialog + reset state on UI thread
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
                ScanCompleted?.Invoke(this, new ScanCompletedEventArgs(arg, cancelled, error));
            });

            // Ensure the dialog has actually finished closing
            try { await dialogTask; } catch { /* ignore dialog close errors */ }

            cts.Dispose();
            if (ReferenceEquals(_cts, cts))
                _cts = null;
        }

        if (error is not null) throw error;
        if (cancelled) throw new OperationCanceledException();
    }

    public Task RemoveScanRoot(long scanRootId)
        => _host.Repo.DeleteScanRootAsync(scanRootId);

    public void CancelScan()
    {
        if (!IsScanning)
            return;

        var cts = _cts;
        if (cts is null)
            return;

        try { cts.Cancel(); } catch { /* best-effort */ }
    }
}
