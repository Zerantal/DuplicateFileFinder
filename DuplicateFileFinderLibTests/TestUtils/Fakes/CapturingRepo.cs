using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLibTests.TestUtils.Fakes;

internal sealed class CapturingRepo : IRepoInternal
{
    private readonly MethodCounter _methodCounter = new();
    // ReSharper disable once CollectionNeverQueried.Global
    public readonly List<string> BeginScanRoots = new();
    public CapturingScanSession? LastSession { get; private set; }
    public ScanRootSnapshotView? BaselineView { get; set; }
    public string? LastFailedMessage { get; private set; }
    public bool LastFailedCancelled { get; private set; }
    public ScanRootSnapshotV2? LastCommittedSnapshot { get; private set; }

    public long NextRunId { get; set; } = 1;

    public long NextDirId { get; set; } = 1;

    public long NextFileId { get; set; } = 1;
    public long NextScanRootId { get; set; } = 1;

    public Task DeleteScanCheckpointAsync(long scanRootId, CancellationToken ct = default)
    {
        _methodCounter.IncrementMethodCalCount();
        return Task.CompletedTask;
    }

    Task<ScanContext> IRepoInternal.BeginNewScanAsync(string rootPath, ScanOptions options,
        VolumeInfo? volumeInfo, CancellationToken ct)
    {
        _methodCounter.IncrementMethodCalCount();
        LastSession = new CapturingScanSession();
        BeginScanRoots.Add(rootPath);

        var scanRootId = NextScanRootId++;
        return Task.FromResult(new ScanContext
        {
            Session = LastSession,
            ScanRoot = new ScanRoot
            {
                RootId = scanRootId,
                RootPath = rootPath,
                DirId = AllocateDirId(),
                CreatedAt = default
            },
            Run = new ScanRun
            {
                ScanSequence = AllocateRunId(),
                ScanRootId = scanRootId,
                RootPath = rootPath,
                StartedAt = default,
                Status = ScanRunStatus.InProgress
            },
            Options = options
        });
    }

    Task<ScanContext> IRepoInternal.BeginRescanAsync(long scanRootId, ScanOptions options, VolumeInfo? volumeInfo,
        CancellationToken ct)
    {
        _methodCounter.IncrementMethodCalCount();
        LastSession = new CapturingScanSession();

        var rootPath = BeginScanRoots.LastOrDefault("");

        return Task.FromResult(new ScanContext
        {
            Session = LastSession,
            ScanRoot = new ScanRoot
            {
                RootId = scanRootId,
                RootPath = rootPath,
                DirId = AllocateDirId(),
                CreatedAt = default
            },
            Run = new ScanRun
            {
                ScanSequence = AllocateRunId(),
                ScanRootId = scanRootId,
                RootPath = rootPath,
                StartedAt = default,
                Status = ScanRunStatus.InProgress
            },
            Options = options
        });
    }

    public Task<ScanContext> BeginSubtreeScanAsync(long scanRootId, ScanOptions options,
        VolumeInfo? volumeInfo = null,
        CancellationToken ct = default)
    {
        _methodCounter.IncrementMethodCalCount();
        LastSession = new CapturingScanSession();

        var rootPath = BeginScanRoots.LastOrDefault("");

        return Task.FromResult(new ScanContext
        {
            Session = LastSession,
            ScanRoot = new ScanRoot
            {
                RootId = scanRootId,
                RootPath = rootPath,
                DirId = AllocateDirId(),
                CreatedAt = default
            },
            Run = new ScanRun
            {
                ScanSequence = AllocateRunId(),
                ScanRootId = scanRootId,
                RootPath = rootPath,
                StartedAt = default,
                Status = ScanRunStatus.InProgress
            },
            Options = options
        });
    }


    // ---- Unused IRepo members in these tests ----
    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public Task SetScanRootDisplayNameAsync(long scanRootId, string? displayName, CancellationToken ct = default)
    {
        _methodCounter.IncrementMethodCalCount();
        return Task.CompletedTask;
    }

    public IReadOnlyList<ScanRun> ScanRunsView => [];
    public IReadOnlyList<ScanRoot> ScanRootsView => [];

    public ScanRootSnapshotView? TryGetScanRootView(long scanRootId)
    {
        return BaselineView;
    }

    public RepoSnapshotView GetRepoSnapshotView()
    {
        throw new NotSupportedException();
    }

    public bool HasScanCheckpoint(long scanRootId)
    {
        _methodCounter.IncrementMethodCalCount();
        return false;
    }

    public Task<DeleteResult> DeleteFileAsync(FileHandle file, CancellationToken ct = default)
    {
        _methodCounter.IncrementMethodCalCount();
        return Task.FromResult(new DeleteResult());
    }

    public Task<DeleteResult> DeleteDirAsync(DirHandle dir, CancellationToken ct = default)
    {
        _methodCounter.IncrementMethodCalCount();
        return Task.FromResult(new DeleteResult());
    }

    public Task DeleteScanRootAsync(long scanRootId, CancellationToken ct)
    {
        _methodCounter.IncrementMethodCalCount();

        return Task.CompletedTask;
    }

    public long AllocateRunId() => NextRunId++;

    public long AllocateDirId() => NextDirId++;

    public long AllocateFileId() => NextFileId++;

    Task IRepoInternal.MarkScanFailedAsync(long sequence, string? errorMessage, bool cancelled, CancellationToken ct)
    {
        _methodCounter.IncrementMethodCalCount();
        LastFailedMessage = errorMessage;
        LastFailedCancelled = cancelled;
        return Task.CompletedTask;
    }

    Task IRepoInternal.MarkScanCompletedAsync(long sequence, CancellationToken ct)
    {
        _methodCounter.IncrementMethodCalCount();
        return Task.CompletedTask;
    }

    public Task CommitScanRootSnapshotV2Async(ScanRootSnapshotV2 snapshot, CancellationToken cancellationToken)
    {
        _methodCounter.IncrementMethodCalCount();
        LastCommittedSnapshot = snapshot;
        return Task.CompletedTask;
    }

    public Task CommitCheckpoint(ScanCheckpoint checkpoint, CancellationToken ct)
    {
        _methodCounter.IncrementMethodCalCount();
        return Task.CompletedTask;
    }


    public int GetMethodCount(string methodName) => _methodCounter.GetMethodCallCount(methodName);
}
