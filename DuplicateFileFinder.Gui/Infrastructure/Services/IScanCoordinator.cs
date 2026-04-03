// Gui/Services/IScanCoordinator.cs

using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.Gui.Infrastructure.Services;

public sealed class ScanCompletedEventArgs(object arg, bool cancelled, Exception? error) : EventArgs
{
    public object Arg { get; } = arg;
    public bool Cancelled { get; } = cancelled;
    public Exception? Error { get; } = error;
}

public sealed class ScanIndexedEventArgs(object arg, ScanRootId scanRootId, long generation) : EventArgs
{
    public object Arg { get; } = arg;
    public ScanRootId ScanRootId { get; } = scanRootId;
    public long Generation { get; } = generation;
}

public interface IScanCoordinator
{
    bool IsScanning { get; }
    public event EventHandler<DuplicateFileFinderProgressReport>? ProgressChanged;

#pragma warning disable 0067
    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;
    public event EventHandler<ScanIndexedEventArgs>? ScanIndexed;
#pragma warning restore 0067

    public Task RunScanNewLocationWithDialogAsync(string rootPath, CancellationToken cancellationToken = default);

    public Task RunRescanLocationWithDialogAsync(ScanRootId scanRootId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rescans a specific directory within a scan root (including the root itself).
    /// Implementations should force StartFresh and warn the user if existing checkpoints are discarded.
    /// </summary>
    public Task RunFolderRescanWithDialogAsync(DirHandle startDir, CancellationToken cancellationToken = default);

    public Task RemoveScanRoot(ScanRootId scanRootId);

    public void CancelScan();

    Task SetScanRootDisplayName(ScanRootId scanRootId, string? displayName);
}
