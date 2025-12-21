using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLibTests.TestUtils.Fakes;

internal sealed class CapturingRepo : IRepoInternal
{
    private readonly MethodCounter _methodCounter = new();
    public readonly List<string> BeginScanRoots = new();
    public CapturingScanSession? LastSession { get; private set; }
    public ScanRootSnapshotView? BaselineView { get; set; }
    public string? LastFailedMessage { get; private set; }
    public bool LastFailedCancelled { get; private set; }
    public ScanRootSnapshotV2? LastCommittedSnapshot { get; private set; }

    public long NextRunId { get; set; }

    public long NextDirId { get; set; }

    public long NextFileId { get; set; }

    public IScanSession BeginScan(
        string rootPath,
        ScanOperation scanOperation = ScanOperation.FullScan,
        VolumeInfo? volumeInfo = null,
        int maxFilesBeforeFlush = 50_000,
        int maxDirsBeforeFlush = 10_000)
    {
        _methodCounter.IncrementMethodCalCount();
        LastSession = new CapturingScanSession();
        BeginScanRoots.Add(rootPath);

        return LastSession;
    }

    public Task CompactAsync(RepoCompactionPolicy? policy = null, CancellationToken ct = default)
    {
        _methodCounter.IncrementMethodCalCount();
        return Task.CompletedTask;
    }

    // ---- Unused IRepo members in these tests ----
    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    [Obsolete]
    public IRepoView GetRepoView()
    {
        throw new NotSupportedException();
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

    public void CommitDelta(RepoDelta delta)
    {
        throw new NotSupportedException();
    }

    public Task CommitDeltaAsync(RepoDelta delta, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public void SaveScanSnapshots()
    {
        throw new NotSupportedException();
    }

    public string GetDirPath(long dirId, bool relativeToVolumePath = false)
    {
        throw new NotSupportedException();
    }

    public string GetDirPathV2ByHandle(DirHandle dirHandle, bool relativeToVolumePath = false)
    {
        throw new NotSupportedException();
    }

    public string GetDirPathV2(long dirId, bool relativeToVolumePath = false)
    {
        throw new NotSupportedException();
    }

    public void DeleteScanRoot(long scanRootId)
    {
        throw new NotImplementedException();
    }

    public long AllocateRunId()
    {
        return NextRunId++;
    }

    public long AllocateDirId()
    {
        return NextDirId++;
    }

    public long AllocateFileId()
    {
        return NextFileId++;
    }

    public void MarkScanFailed(long sequence, string? errorMessage, bool cancelled)
    {
        _methodCounter.IncrementMethodCalCount();
        LastFailedMessage = errorMessage;
        LastFailedCancelled = cancelled;
    }

    public void MarkScanCompleted(long sequence)
    {
        _methodCounter.IncrementMethodCalCount();
    }

    public Task CommitScanRootSnapshotV2Async(ScanRootSnapshotV2 snapshot, CancellationToken cancellationToken)
    {
        _methodCounter.IncrementMethodCalCount();
        LastCommittedSnapshot = snapshot;
        return Task.CompletedTask;
    }


    public int GetMethodCount(string methodName)
    {
        return _methodCounter.GetMethodCallCount(methodName);
    }
}