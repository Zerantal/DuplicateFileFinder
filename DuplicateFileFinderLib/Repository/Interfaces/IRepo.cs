using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Interfaces;

internal interface IRepoInternal
{
    void DeleteScanRoot(long scanRootId);
    
    long AllocateRunId();
}

public interface IRepo : IDisposable, IAsyncDisposable
{
    IRepoView GetRepoView();
    public IReadOnlyList<ScanRun> ScanRunsView { get; }
    public IReadOnlyList<ScanRoot> ScanRootsView { get; }
    ScanRootSnapshotView? TryGetScanRootView(long scanRootId);
    public RepoSnapshotView GetRepoSnapshotView();

    public IScanSession BeginScan(
        string rootPath,
        ScanOperation scanOperation = ScanOperation.FullScan,
        VolumeInfo? volumeInfo = null,
        int maxFilesBeforeFlush = 50_000,
        int maxDirsBeforeFlush = 10_000);
    void CommitDelta(RepoDelta delta);
    public Task CommitDeltaAsync(RepoDelta delta, CancellationToken cancellationToken = default);
    public void SaveScanSnapshots();
    public Task CompactAsync(RepoCompactionPolicy? policy = null, CancellationToken ct = default);
    string GetDirPath(long dirId, bool relativeToVolumePath = false);
}