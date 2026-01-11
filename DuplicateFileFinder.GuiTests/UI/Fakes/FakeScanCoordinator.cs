using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Repository.Core.Models;
// ReSharper disable UnassignedGetOnlyAutoProperty

#pragma warning disable CS0067

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public sealed class FakeScanCoordinator : IScanCoordinator
{
    public List<DirHandle> RescannedFolders { get; } = [];

    public readonly List<(long ScanRootId, string? DisplayName)> DisplayNameUpdates = [];

    public Task RunFolderRescanWithDialogAsync(DirHandle dir)
    {
        RescannedFolders.Add(dir);
        return Task.CompletedTask;
    }

    public bool IsScanning { get; }
    public event EventHandler<DuplicateFileFinderProgressReport>? ProgressChanged;
    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;
    public Task RunScanNewLocationWithDialogAsync(string rootPath, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task RunRescanLocationWithDialogAsync(long scanRootId, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task RunFolderRescanWithDialogAsync(DirHandle startDir, CancellationToken cancellationToken)
    {
        RescannedFolders.Add(startDir);
        return Task.CompletedTask;
    }

    public Task RemoveScanRoot(long scanRootId) => throw new NotImplementedException();

    public void CancelScan() => throw new NotImplementedException();

    public Task SetScanRootDisplayName(long scanRootId, string? displayName)
    {
        DisplayNameUpdates.Add((scanRootId, displayName));
        return Task.CompletedTask;
    }
}

#pragma warning restore CS0067
