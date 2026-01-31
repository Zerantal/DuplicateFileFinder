using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Interfaces;

internal interface IRepoInternal : IRepo
{
    DirId AllocateDirId();
    FileId AllocateFileId();

    Task MarkScanFailedAsync(long sequence, string? errorMessage, bool cancelled, CancellationToken ct = default);
    Task MarkScanCompletedAsync(long sequence, CancellationToken token = default);
    Task CommitScanRootSnapshotV2Async(ScanRootSnapshotV2 snapshot, CancellationToken cancellationToken = default);
    Task CommitCheckpoint(ScanCheckpoint checkpoint, CancellationToken ct = default);
    Task DeleteScanCheckpointAsync(ScanRootId scanRootId, CancellationToken ct = default);

    /// <summary>
    /// Finalise a successfully completed scan in one operation:
    /// commit the completed snapshot, mark the scan run completed, and delete any scan checkpoint.
    /// This allows the repo to publish a single, reasoned event using the correct generation + snapshot view.
    /// </summary>
    Task FinaliseCompletedScanAsync(
        long scanSequence,
        ScanRootSnapshotV2 completedSnapshot,
        CancellationToken ct = default);

    Task<ScanContext> BeginNewScanAsync(
        string rootPath,
        ScanOptions options,
        VolumeInfo? volumeInfo = null,
        CancellationToken ct = default);

    Task<ScanContext> BeginRescanAsync(
        ScanRootId scanRootId,
        ScanOptions options,
        VolumeInfo? volumeInfo = null,
        CancellationToken ct = default);

    // subtree scan (folder rescan) entrypoint.
    // Must be StartFresh=true and requires a loaded snapshot for the root.
    Task<ScanContext> BeginSubtreeScanAsync(
        ScanRootId scanRootId,
        ScanOptions options,
        VolumeInfo? volumeInfo = null,
        CancellationToken ct = default);
}

public interface IRepo : IDisposable, IAsyncDisposable
{
    public Task DeleteScanRootAsync(ScanRootId scanRootId, CancellationToken ct = default);
    public Task SetScanRootDisplayNameAsync(ScanRootId scanRootId, string? displayName, CancellationToken ct = default);
    public IReadOnlyList<ScanRun> ScanRunsView { get; }
    public IReadOnlyList<ScanRoot> ScanRootsView { get; }
    ScanRootSnapshotView? TryGetScanRootView(ScanRootId scanRootId);
    public RepoSnapshotView GetRepoSnapshotView();
    public bool HasScanCheckpoint(ScanRootId scanRootId);
    Task<DeleteResult> DeleteFileAsync(FileHandle file, CancellationToken ct = default);
    Task<DeleteResult> DeleteDirAsync(DirHandle dir, CancellationToken ct = default);

}
