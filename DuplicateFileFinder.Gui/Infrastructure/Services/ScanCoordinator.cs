// DuplicateFileFinder.Gui/Infrastructure/Services/ScanCoordinator.cs

using Avalonia.Threading;

using DuplicateFileFinder.Gui.Features.Scanning.Views;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;

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
    private static readonly Logger s_log = LogManager.GetCurrentClassLogger();

    private readonly IDialogService _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    private readonly IRepoHost _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly Dff.DuplicateFileFinder _finder = finder ?? throw new ArgumentNullException(nameof(finder));

    private CancellationTokenSource? _cts;

    public event EventHandler<Dff.DuplicateFileFinderProgressReport>? ProgressChanged;
    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;

    public bool IsScanning { get; private set; }

    public Task SetScanRootDisplayName(ScanRootId scanRootId, string? displayName)
        => _host.Repo.SetScanRootDisplayNameAsync(scanRootId, displayName);

    public Task RunScanNewLocationWithDialogAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path is required.", nameof(rootPath));

        return RunScanWithDialogCoreAsync(
            arg: rootPath,
            cancellationToken,
            runAsync: (progress, ct) => _finder.FullScanAsync(rootPath, progress, ct),
            logStart: () => s_log.Info("Starting scan of {root}", rootPath),
            logCancel: () => s_log.Info("Scan cancelled for {root}", rootPath),
            logFail: ex => s_log.Error(ex, "Scan failed for {root}", rootPath));
    }

    public Task RunRescanLocationWithDialogAsync(ScanRootId scanRootId, CancellationToken cancellationToken = default)
    {
        if (scanRootId < 0)
            throw new ArgumentException("Valid ScanRootId is required.", nameof(scanRootId));

        return RunScanWithDialogCoreAsync(
            arg: scanRootId,
            cancellationToken,
            runAsync: (progress, ct) => _finder.FullScanAsync(scanRootId, progress, ct),
            logStart: () => s_log.Info("Starting rescan of location {root}", scanRootId),
            logCancel: () => s_log.Info("Location rescan cancelled for {root}", scanRootId),
            logFail: ex => s_log.Error(ex, "Location rescan failed for {root}", scanRootId));
    }

    public Task RunFolderRescanWithDialogAsync(DirHandle startDir, CancellationToken cancellationToken = default)
    {
        if (!startDir.IsValid)
            throw new ArgumentException("DirHandle is not valid.", nameof(startDir));

        return RunScanWithDialogCoreAsync(
            arg: startDir,
            cancellationToken,
            runAsync: (progress, ct) => _finder.FullScanAsync(startDir, progress, ct),
            logStart: () => s_log.Info("Starting folder rescan of {dir}", startDir),
            logCancel: () => s_log.Info("Folder rescan cancelled for {dir}", startDir),
            logFail: ex => s_log.Error(ex, "Folder rescan failed for {dir}", startDir));
    }

    private async Task RunScanWithDialogCoreAsync(
        object arg,
        CancellationToken cancellationToken,
        Func<IProgress<Dff.DuplicateFileFinderProgressReport>, CancellationToken, Task<ScanCompletionInfo>> runAsync,
        Action logStart,
        Action logCancel,
        Action<Exception> logFail)
    {
        // Ensure we run the UI bits on UI thread exactly once.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
                await RunScanWithDialogCoreAsync(arg, cancellationToken, runAsync, logStart, logCancel, logFail));
            return;
        }

        if (IsScanning)
            return;

        IsScanning = true;
        logStart();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var progressVm = new ScanProgressViewModel(this);
        var dialog = new ScanProgressWindow { DataContext = progressVm };

        void HandleProgress(Dff.DuplicateFileFinderProgressReport report)
        {
            ProgressChanged?.Invoke(this, report);
            progressVm.Update(report);
        }

        void DismissHandler(object? _, EventArgs __)
        {
            try { if (dialog.IsVisible) dialog.Close(); }
            catch { /* ignore */ }
        }
        progressVm.RequestDismiss += DismissHandler;


        // Constructed on UI thread => progress callbacks marshal to UI thread
        var progress = new Progress<Dff.DuplicateFileFinderProgressReport>(HandleProgress);

        var owner = _dialogService.GetOwnerWindow();
        var dialogTask = dialog.ShowDialog(owner);

        Exception? error = null;
        var cancelled = false;
        ScanCompletionInfo? completion = null;

        var token = _cts.Token;
        try
        {

            // Off-UI execution
            await Task.Run(async () =>
            {
                try
                {
                    completion = await runAsync(progress, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled = true;
                    logCancel();
                }
                catch (Exception ex)
                {
                    error = ex;
                    logFail(ex);
                }
            }, token).ConfigureAwait(false);

            // Successful scan => wait for indexes to be coherent for generation
            if (!cancelled && error is null && completion is not null)
            {
                // Optional: give a subtle UI hint that we're in the "finalizing" phase.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try { dialog.Title = "Finalizing (updating indexes)..."; }
                    catch { /* ignore */ }
                    progressVm.EnterFinalizing();
                });

                await WaitForIndexesAsync(completion.Value, token).ConfigureAwait(false);
            }
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
                finally
                {
                    progressVm.RequestDismiss -= DismissHandler;
                }

                IsScanning = false;
                ScanCompleted?.Invoke(this, new ScanCompletedEventArgs(arg, cancelled, error));
            });

            // Ensure the dialog has actually finished closing
            try { await dialogTask; } catch { /* ignore dialog close errors */ }

            var cts = _cts;
            _cts = null;
            cts?.Dispose();
        }

        if (error is not null) throw error;
        if (cancelled) throw new OperationCanceledException(token);
    }

    private Task WaitForIndexesAsync(ScanCompletionInfo completion, CancellationToken ct)
    {
        // Check if indexes already rebuilt (fixes small-folder race)
        if (_host.LastIndexedGeneration >= completion.Generation)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, RepoIndexesRebuiltEventArgs e)
        {
            if (e.Generation >= completion.Generation)
                tcs.TrySetResult();
        }

        _host.IndexesRebuilt += Handler;

        // Close the subscribe race window
        if (_host.LastIndexedGeneration >= completion.Generation)
            tcs.TrySetResult();

        var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        return tcs.Task.ContinueWith(t =>
        {
            _host.IndexesRebuilt -= Handler;
            reg.Dispose();
            return t;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
    }

    public Task RemoveScanRoot(ScanRootId scanRootId)
        => _host.Repo.DeleteScanRootAsync(scanRootId);

    public void CancelScan()
    {
        if (!IsScanning)
            return;

        var cts = _cts;

        try { cts?.Cancel(); } catch { /* best-effort */ }
    }
}
