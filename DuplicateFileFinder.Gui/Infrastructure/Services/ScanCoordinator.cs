using Avalonia.Threading;

using DuplicateFileFinder.Gui.Features.Scanning.ViewModels;
using DuplicateFileFinder.Gui.Features.Scanning.Views;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;

using NLog;

using Dff = DuplicateFileFinderLib.Core;

namespace DuplicateFileFinder.Gui.Infrastructure.Services;

internal sealed record ScanRunSpec(
    object Arg,
    Func<IProgress<Dff.DuplicateFileFinderProgressReport>, CancellationToken, Task<ScanCompletionInfo>> RunAsync,
    Action StartLog,
    Action CancelLog,
    Action<Exception> FailLog,
    Func<DirectoryNotFoundException, CancellationToken, Task<MissingPathResult>>? TryHandleMissingPathAsync = null,
    string? MissingPathWorkingText = null);

internal sealed record MissingPathResult(bool Success, long? Generation, int? ScanRootId)
{
    public static readonly MissingPathResult Failed = new(false, null, null);
}

public sealed class ScanCoordinator(
    IRepoHost host,
    Dff.DuplicateFileFinder finder,
    IDialogService dialogService)
    : IScanCoordinator
{
    private static readonly Logger s_log = LogManager.GetCurrentClassLogger();

    private readonly IDialogService _dialogService =
        dialogService ?? throw new ArgumentNullException(nameof(dialogService));

    private readonly IRepoHost _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly Dff.DuplicateFileFinder _finder = finder ?? throw new ArgumentNullException(nameof(finder));

    private CancellationTokenSource? _cts;

    public event EventHandler<Dff.DuplicateFileFinderProgressReport>? ProgressChanged;
    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;
    public event EventHandler<ScanIndexedEventArgs>? ScanIndexed;

    public bool IsScanning { get; private set; }

    public Task SetScanRootDisplayName(ScanRootId scanRootId, string? displayName)
        => _host.Repo.SetScanRootDisplayNameAsync(scanRootId, displayName);

    public Task RunScanNewLocationWithDialogAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path is required.", nameof(rootPath));

        return RunScanWithDialogCoreAsync(
            new ScanRunSpec(
                Arg: rootPath,
                RunAsync: (progress, ct) => _finder.FullScanAsync(rootPath, progress, ct),
                StartLog: () => s_log.Info("Starting scan of {root}", rootPath),
                CancelLog: () => s_log.Info("Scan cancelled for {root}", rootPath),
                FailLog: ex => s_log.Error(ex, "Scan failed for {root}", rootPath)),
            cancellationToken);
    }

    public Task RunRescanLocationWithDialogAsync(ScanRootId scanRootId, CancellationToken cancellationToken = default)
    {
        if (scanRootId < 0)
            throw new ArgumentException("Valid ScanRootId is required.", nameof(scanRootId));

        return RunScanWithDialogCoreAsync(
            new ScanRunSpec(
                Arg: scanRootId,
                RunAsync: (progress, ct) => _finder.FullScanAsync(scanRootId, progress, ct),
                StartLog: () => s_log.Info("Starting rescan of location {root}", scanRootId),
                CancelLog: () => s_log.Info("Location rescan cancelled for {root}", scanRootId),
                FailLog: ex => s_log.Error(ex, "Location rescan failed for {root}", scanRootId),
                TryHandleMissingPathAsync: (_, ct) => HandleMissingScanRootAsync(scanRootId, ct),
                MissingPathWorkingText: "Location no longer exists. Removing it from the repo..."),
            cancellationToken);
    }

    public Task RunFolderRescanWithDialogAsync(DirHandle startDir, CancellationToken cancellationToken = default)
    {
        if (!startDir.IsValid)
            throw new ArgumentException("DirHandle is not valid.", nameof(startDir));

        return RunScanWithDialogCoreAsync(
            new ScanRunSpec(
                Arg: startDir,
                RunAsync: (progress, ct) => _finder.FullScanAsync(startDir, progress, ct),
                StartLog: () => s_log.Info("Starting folder rescan of {dir}", startDir),
                CancelLog: () => s_log.Info("Folder rescan cancelled for {dir}", startDir),
                FailLog: ex => s_log.Error(ex, "Folder rescan failed for {dir}", startDir),
                TryHandleMissingPathAsync: (_, ct) => HandleMissingFolderAsync(startDir, ct),
                MissingPathWorkingText: "Folder no longer exists. Removing it from the repo..."),
                cancellationToken);
    }

    internal async Task RunScanWithDialogCoreAsync(ScanRunSpec spec, CancellationToken cancellationToken)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => RunScanWithDialogCoreAsync(spec, cancellationToken));
            return;
        }

        if (IsScanning)
            return;

        IsScanning = true;
        spec.StartLog();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var dialogScope = CreateDialogScope();
        var token = _cts.Token;

        ScanExecutionOutcome outcome = ScanExecutionOutcome.CancelledOutcome;
        try
        {
            outcome = await ExecuteScanWorkflowAsync(spec, dialogScope, token).ConfigureAwait(false);
            await FinalizeSuccessfulOutcomeAsync(spec.Arg, outcome, dialogScope, token).ConfigureAwait(false);
        }
        finally
        {
            await CleanupAfterRunAsync(spec.Arg, dialogScope, outcome).ConfigureAwait(false);

            var cts = _cts;
            _cts = null;
            cts?.Dispose();
        }

        if (outcome.Error is not null)
            throw outcome.Error;

        if (outcome.Cancelled)
            throw new OperationCanceledException(token);
    }

    private ScanDialogScope CreateDialogScope()
    {
        var progressVm = new ScanProgressViewModel(this);
        var dialog = new ScanProgressWindow { DataContext = progressVm };

        void HandleProgress(Dff.DuplicateFileFinderProgressReport report)
        {
            ProgressChanged?.Invoke(this, report);
            progressVm.Update(report);
        }

        void DismissHandler(object? _, EventArgs __)
        {
            try
            {
                if (dialog.IsVisible)
                    dialog.Close();
            }
            catch
            {
                /* ignore */
            }
        }

        progressVm.RequestDismiss += DismissHandler;

        var progress = new Progress<Dff.DuplicateFileFinderProgressReport>(HandleProgress);
        var owner = _dialogService.GetOwnerWindow();
        var dialogTask = dialog.ShowDialog(owner);

        return new ScanDialogScope(progressVm, dialog, progress, dialogTask, DismissHandler);
    }

    private async Task<ScanExecutionOutcome> ExecuteScanWorkflowAsync(
        ScanRunSpec spec,
        ScanDialogScope dialogScope,
        CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            try
            {
                var completion = await spec.RunAsync(dialogScope.Progress, token).ConfigureAwait(false);
                return ScanExecutionOutcome.FromCompletion(completion);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                spec.CancelLog();
                return ScanExecutionOutcome.CancelledOutcome;
            }
            catch (DirectoryNotFoundException ex) when (!token.IsCancellationRequested &&
                                                        spec.TryHandleMissingPathAsync is not null)
            {
                return await TryHandleMissingPathAsync(spec, dialogScope, ex, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                spec.FailLog(ex);
                return ScanExecutionOutcome.FromError(ex);
            }
        }, token).ConfigureAwait(false);
    }

    private async Task<ScanExecutionOutcome> TryHandleMissingPathAsync(
        ScanRunSpec spec,
        ScanDialogScope dialogScope,
        DirectoryNotFoundException ex,
        CancellationToken token)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                EnterFinalizing(
                    dialogScope.Dialog,
                    dialogScope.ProgressViewModel,
                    "Finalizing (updating repo)...",
                    spec.MissingPathWorkingText ?? "Path no longer exists. Updating repo..."));

            var missingPathResult = await spec.TryHandleMissingPathAsync!(ex, token).ConfigureAwait(false);
            if (!missingPathResult.Success)
            {
                spec.FailLog(ex);
                return ScanExecutionOutcome.FromError(ex);
            }

            return ScanExecutionOutcome.FromMissingPath(missingPathResult.Generation, missingPathResult.ScanRootId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            spec.CancelLog();
            return ScanExecutionOutcome.CancelledOutcome;
        }
        catch (Exception exception)
        {
            spec.FailLog(exception);
            return ScanExecutionOutcome.FromError(exception);
        }
    }

    private async Task FinalizeSuccessfulOutcomeAsync(
        object arg,
        ScanExecutionOutcome outcome,
        ScanDialogScope dialogScope,
        CancellationToken token)
    {
        if (outcome.Cancelled || outcome.Error is not null)
            return;

        if (outcome.Completion is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                EnterFinalizing(
                    dialogScope.Dialog,
                    dialogScope.ProgressViewModel,
                    "Finalizing (updating indexes)..."));

            await _host.WhenIndexesRebuiltAsync(outcome.Completion.Value.Generation, token).ConfigureAwait(false);

            await PublishScanIndexedAsync(
                arg,
                outcome.Completion.Value.ScanRootId,
                outcome.Completion.Value.Generation).ConfigureAwait(false);

            return;
        }

        if (outcome is { MissingPathHandled: true, MissingPathGeneration: { } generation })
        {
            await PublishScanIndexedAsync(arg, outcome.MissingPathScanRootId ?? -1, generation).ConfigureAwait(false);
        }
    }

    private async Task PublishScanIndexedAsync(object arg, int scanRootId, long generation)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
            ScanIndexed?.Invoke(
                this,
                new ScanIndexedEventArgs(arg, scanRootId, generation)));
    }

    private async Task CleanupAfterRunAsync(
        object arg,
        ScanDialogScope dialogScope,
        ScanExecutionOutcome outcome)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                if (dialogScope.Dialog.IsVisible)
                    dialogScope.Dialog.Close();
            }
            catch
            {
                // ignore close errors
            }
            finally
            {
                dialogScope.ProgressViewModel.RequestDismiss -= dialogScope.DismissHandler;
            }

            IsScanning = false;
            ScanCompleted?.Invoke(this, new ScanCompletedEventArgs(arg, outcome.Cancelled, outcome.Error));
        });

        try
        {
            await dialogScope.DialogTask.ConfigureAwait(false);
        }
        catch
        {
            /* ignore dialog close errors */
        }
    }

    public async Task RemoveScanRoot(ScanRootId scanRootId) =>
        await _host.Repo.DeleteScanRootAsync(scanRootId).ConfigureAwait(false);

    public void CancelScan()
    {
        if (!IsScanning)
            return;

        var cts = _cts;

        try
        {
            cts?.Cancel();
        }
        catch
        {
            /* best-effort */
        }
    }

    private async Task<MissingPathResult> HandleMissingScanRootAsync(ScanRootId scanRootId, CancellationToken ct)
    {
        var generation = await _host.Repo.DeleteScanRootAsync(scanRootId, ct).ConfigureAwait(false);
        await _host.WhenIndexesRebuiltAsync(generation, ct).ConfigureAwait(false);
        return new MissingPathResult(true, generation, scanRootId);
    }

    private async Task<MissingPathResult> HandleMissingFolderAsync(DirHandle startDir, CancellationToken ct)
    {
        var result = await _host.Repo.DeleteDirAsync(startDir, ct).ConfigureAwait(false);
        if (!result.Success)
            return MissingPathResult.Failed;

        await _host.WhenIndexesRebuiltAsync(result.Generation, ct).ConfigureAwait(false);
        return new MissingPathResult(true, result.Generation, startDir.ScanRootId);
    }

    private static void EnterFinalizing(
        ScanProgressWindow dialog,
        ScanProgressViewModel progressVm,
        string title,
        string? statusMessage = null)
    {
        try
        {
            dialog.Title = title;
        }
        catch
        {
            /* ignore */
        }

        progressVm.EnterFinalizing();

        if (!string.IsNullOrWhiteSpace(statusMessage))
            progressVm.StatusMessage = statusMessage;
    }

    private sealed record ScanExecutionOutcome(
        ScanCompletionInfo? Completion,
        bool Cancelled,
        Exception? Error,
        bool MissingPathHandled,
        long? MissingPathGeneration,
        int? MissingPathScanRootId)
    {
        public static readonly ScanExecutionOutcome CancelledOutcome =
            new(null, true, null, false, null, null);

        public static ScanExecutionOutcome FromCompletion(ScanCompletionInfo completion) =>
            new(completion, false, null, false, null, null);

        public static ScanExecutionOutcome FromError(Exception error) =>
            new(null, false, error, false, null, null);

        public static ScanExecutionOutcome FromMissingPath(long? generation, int? scanRootId) =>
            new(null, false, null, true, generation, scanRootId);
    }

    private sealed class ScanDialogScope(
        ScanProgressViewModel progressViewModel,
        ScanProgressWindow dialog,
        IProgress<Dff.DuplicateFileFinderProgressReport> progress,
        Task dialogTask,
        EventHandler dismissHandler)
    {
        public ScanProgressViewModel ProgressViewModel { get; } = progressViewModel;
        public ScanProgressWindow Dialog { get; } = dialog;
        public IProgress<Dff.DuplicateFileFinderProgressReport> Progress { get; } = progress;
        public Task DialogTask { get; } = dialogTask;
        public EventHandler DismissHandler { get; } = dismissHandler;
    }
}
