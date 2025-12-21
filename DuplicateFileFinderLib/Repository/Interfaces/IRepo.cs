using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;
using ScanRun = DuplicateFileFinderLib.Repository.Storage.Models.ScanRun;

namespace DuplicateFileFinderLib.Repository.Interfaces;

internal interface IRepoInternal : IRepo
{
    void DeleteScanRoot(long scanRootId);
    
    long AllocateRunId();
    long AllocateDirId();
    long AllocateFileId();

    void MarkScanFailed(long sequence, string? errorMessage, bool cancelled);
    public void MarkScanCompleted(long sequence);
    Task CommitScanRootSnapshotV2Async(ScanRootSnapshotV2 snapshot, CancellationToken cancellationToken);
}

public interface IRepo : IDisposable, IAsyncDisposable
{
    [Obsolete]
    IRepoView GetRepoView();
    public IReadOnlyList<ScanRun> ScanRunsView { get; }
    public IReadOnlyList<ScanRoot> ScanRootsView { get; }
    ScanRootSnapshotView? TryGetScanRootView(long scanRootId);
    public RepoSnapshotView GetRepoSnapshotView();


    void CommitDelta(RepoDelta delta);
    public Task CommitDeltaAsync(RepoDelta delta, CancellationToken cancellationToken = default);
    public void SaveScanSnapshots();
    public IScanSession BeginScan(
        string rootPath,
        ScanOperation scanOperation = ScanOperation.FullScan,
        VolumeInfo? volumeInfo = null,
        int maxFilesBeforeFlush = 50_000,
        int maxDirsBeforeFlush = 10_000);
    public Task CompactAsync(RepoCompactionPolicy? policy = null, CancellationToken ct = default);
    string GetDirPath(long dirId, bool relativeToVolumePath = false);
    public string GetDirPathV2ByHandle(DirHandle dirHandle, bool relativeToVolumePath = false);
    public string GetDirPathV2(long dirId, bool relativeToVolumePath = false);
}