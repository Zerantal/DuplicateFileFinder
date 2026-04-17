using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage;

using NLog;

namespace DuplicateFileFinderLib.Repository.Plugins.FileDir;

public sealed partial class FileDirIndexPlugin : ChannelRepoPlugin, IFileDirReadModel
{
    private static readonly Logger s_log = LogManager.GetCurrentClassLogger();

    // Published, read-only snapshots (we never mutate these maps after publishing).
    // We swap the reference atomically when rebuilding.
    private volatile SegmentedMap<FileHandle> _filesById = SegmentedMap<FileHandle>.Empty;
    private volatile SegmentedMap<DirHandle> _dirsById = SegmentedMap<DirHandle>.Empty;

    // Cached snapshot view used for path decoding
    private volatile RepoSnapshotView? _snapshotView;

    // Persisted position (only mutated on bootstrap/worker thread)
    private long _lastIndexedGeneration;

    private readonly string _dataDirectory;
    private const string StateFileName = "file-dir-index.bin";

    // Active (non-deleted) scan roots (based on snapshot scanroot meta)
    private HashSet<ScanRootId> _activeScanRoots = new();

    // Per-root counts (only updated on worker thread)
    private Dictionary<ScanRootId, int> _dirCountByRootId = new();
    private Dictionary<ScanRootId, int> _fileCountByRootId = new();

    // Published counts (exclude deleted scan roots)
    private int _activeDirCount;
    private int _activeFileCount;

    public FileDirIndexPlugin(string dataDirectory) : base(4096)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory must be non-empty.", nameof(dataDirectory));

        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(_dataDirectory);
    }

    // ---------------------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------------------

    protected override ValueTask OnBootstrapEventAsync(BootstrapEvent evt, CancellationToken ct)
    {
        _snapshotView = evt.RepoSnapshotView;

        if (!TryLoadState(evt.Generation))
        {
            // Fallback: rebuild from snapshot and persist.
            RebuildFromSnapshot(evt.RepoSnapshotView);
            _lastIndexedGeneration = evt.Generation;
            SaveState();
        }
        else
        {
            // State loaded for this generation.
            // Ensure counts are coherent with the current snapshot (state may be older format).
            EnsureCountsFromSnapshotIfMissing(evt.RepoSnapshotView);
            _lastIndexedGeneration = evt.Generation;
        }

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnScanRootSnapshotReplacedEventAsync(
        ScanRootSnapshotReplacedEvent evt,
        CancellationToken ct)
    {
        // Ignore stale/out-of-order events (channel may drop old items).
        if (evt.Generation <= _lastIndexedGeneration)
            return ValueTask.CompletedTask;

        _snapshotView = evt.RepoSnapshotView;

        RebuildFromSnapshot(evt.RepoSnapshotView);

        _lastIndexedGeneration = evt.Generation;
        SaveState();
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnRepoScanRootRemovedEventAsync(
        RepoScanRootRemovedEvent evt,
        CancellationToken ct)
    {
        if (evt.Generation <= _lastIndexedGeneration)
            return ValueTask.CompletedTask;

        var rootId = evt.ScanRootIdValue;

        // Mark inactive (idempotent)
        if (_activeScanRoots.Remove(rootId))
        {
            // Subtract the removed root's contribution
            if (_dirCountByRootId.TryGetValue(rootId, out var d))
                _activeDirCount = Math.Max(0, _activeDirCount - d);

            if (_fileCountByRootId.TryGetValue(rootId, out var f))
                _activeFileCount = Math.Max(0, _activeFileCount - f);
        }

        _lastIndexedGeneration = evt.Generation;
        SaveState();
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnRepoFileDeletedEventAsync(RepoFileDeletedEvent evt, CancellationToken ct)
    {
        // Incremental live-state update:
        // remove the deleted file from the lookup map and adjust active counts.
        var oldFilesById = _filesById;
        _filesById = _filesById.Remove(evt.FileId);

        if (!ReferenceEquals(_filesById, oldFilesById))
        {
            _activeFileCount = Math.Max(0, _activeFileCount - 1);

            if (_fileCountByRootId.TryGetValue(evt.File.ScanRootId, out var rootCount))
            {
                rootCount--;
                if (rootCount <= 0)
                    _fileCountByRootId.Remove(evt.File.ScanRootId);
                else
                    _fileCountByRootId[evt.File.ScanRootId] = rootCount;
            }
        }

        _lastIndexedGeneration = evt.Generation;
        SaveState();

        s_log.Debug(
            "FileDirIndexPlugin applied RepoFileDeletedEvent gen={gen}, scanRootId={scanRootId}, fileId={fileId}",
            evt.Generation,
            evt.File.ScanRootId,
            evt.FileId);

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnRepoDirDeletedEventAsync(RepoDirDeletedEvent evt, CancellationToken ct)
    {
        // Incremental live-state update:
        // remove all deleted dir/file ids from the lookup maps and adjust active counts.
        var removedDirs = 0;
        var removedFiles = 0;

        var beforeDirsById = _dirsById;
        var beforeFilesById = _filesById;
        _dirsById = _dirsById.RemoveMany(evt.DeletedDirIds);
        _filesById = _filesById.RemoveMany(evt.DeletedFiles.Select(f => f.FileId));
        if (!ReferenceEquals(_dirsById, beforeDirsById))
            removedDirs = evt.DeletedDirsCount;
        if (!ReferenceEquals(_filesById, beforeFilesById))
            removedFiles = evt.DeletedFilesCount;

        if (removedDirs != 0)
        {
            _activeDirCount = Math.Max(0, _activeDirCount - removedDirs);

            if (_dirCountByRootId.TryGetValue(evt.Dir.ScanRootId, out var rootDirCount))
            {
                rootDirCount -= removedDirs;
                if (rootDirCount <= 0)
                    _dirCountByRootId.Remove(evt.Dir.ScanRootId);
                else
                    _dirCountByRootId[evt.Dir.ScanRootId] = rootDirCount;
            }
        }

        if (removedFiles != 0)
        {
            _activeFileCount = Math.Max(0, _activeFileCount - removedFiles);

            if (_fileCountByRootId.TryGetValue(evt.Dir.ScanRootId, out var rootFileCount))
            {
                rootFileCount -= removedFiles;
                if (rootFileCount <= 0)
                    _fileCountByRootId.Remove(evt.Dir.ScanRootId);
                else
                    _fileCountByRootId[evt.Dir.ScanRootId] = rootFileCount;
            }
        }

        _lastIndexedGeneration = evt.Generation;
        SaveState();

        s_log.Debug(
            "FileDirIndexPlugin applied RepoDirDeletedEvent gen={gen}, scanRootId={scanRootId}, removedDirs={removedDirs}, removedFiles={removedFiles}",
            evt.Generation,
            evt.Dir.ScanRootId,
            removedDirs,
            removedFiles);

        return ValueTask.CompletedTask;
    }
}
