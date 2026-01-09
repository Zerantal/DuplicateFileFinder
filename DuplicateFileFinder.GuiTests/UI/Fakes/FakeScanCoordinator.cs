using System;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.GuiTests.Ui.Fakes;

public sealed class FakeScanCoordinator : IScanCoordinator
{
    public bool IsScanning { get; private set; }

    public event EventHandler<DuplicateFileFinderProgressReport>? ProgressChanged;
    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;

    public Task RunScanNewLocationWithDialogAsync(string rootPath, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RunRescanLocationWithDialogAsync(long scanRootId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RunFolderRescanWithDialogAsync(DirHandle startDir, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RemoveScanRoot(long scanRootId) => Task.CompletedTask;

    public void CancelScan()
    {
        IsScanning = false;
        ScanCompleted?.Invoke(this, new ScanCompletedEventArgs(arg: new object(), cancelled: true, error: null));
    }

    public Task SetScanRootDisplayName(long scanRootId, string? displayName) => Task.CompletedTask;

    // Optional helper for tests
    public void RaiseProgress(DuplicateFileFinderProgressReport report) => ProgressChanged?.Invoke(this, report);
}
