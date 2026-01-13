using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Interfaces;

internal interface IRepoInternal : IRepo
{
    long AllocateDirId();
    long AllocateFileId();

    Task MarkScanFailedAsync(long sequence, string? errorMessage, bool cancelled, CancellationToken ct = default);
    Task MarkScanCompletedAsync(long sequence, CancellationToken token = default);
    Task CommitScanRootSnapshotV2Async(ScanRootSnapshotV2 snapshot, CancellationToken cancellationToken = default);
    Task CommitCheckpoint(ScanCheckpoint checkpoint, CancellationToken ct = default);
    Task DeleteScanCheckpointAsync(long scanRootId, CancellationToken ct = default);

    Task<ScanContext> BeginNewScanAsync(
        string rootPath,
        ScanOptions options,
        VolumeInfo? volumeInfo = null,
        CancellationToken ct = default);

    Task<ScanContext> BeginRescanAsync(
        long scanRootId,
        ScanOptions options,
        VolumeInfo? volumeInfo = null,
        CancellationToken ct = default);

    // NEW: subtree scan (folder rescan) entrypoint.
    // Must be StartFresh=true and requires a loaded snapshot for the root.
    Task<ScanContext> BeginSubtreeScanAsync(
        long scanRootId,
        ScanOptions options,
        VolumeInfo? volumeInfo = null,
        CancellationToken ct = default);
}

public interface IRepo : IDisposable, IAsyncDisposable
{
    public Task DeleteScanRootAsync(long scanRootId, CancellationToken ct = default);
    public Task SetScanRootDisplayNameAsync(long scanRootId, string? displayName, CancellationToken ct = default);
    public IReadOnlyList<ScanRun> ScanRunsView { get; }
    public IReadOnlyList<ScanRoot> ScanRootsView { get; }
    ScanRootSnapshotView? TryGetScanRootView(long scanRootId);
    public RepoSnapshotView GetRepoSnapshotView();
    public bool HasScanCheckpoint(long scanRootId);
    Task<DeleteResult> DeleteFileAsync(FileHandle file, CancellationToken ct = default);
    Task<DeleteResult> DeleteDirAsync(DirHandle dir, CancellationToken ct = default);

}
