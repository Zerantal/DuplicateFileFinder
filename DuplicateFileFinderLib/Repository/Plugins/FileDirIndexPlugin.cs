using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage;

using NLog;

namespace DuplicateFileFinderLib.Repository.Plugins;

public sealed class FileDirIndexPlugin : ChannelRepoPlugin, IFileDirReadModel
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

    public int DirCount => _activeDirCount;
    public int FileCount => _activeFileCount;

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

        // For mutation events (currently delete-driven snapshot updates), skip the full rebuild.
        // Incremental delete events (RepoFileDeletedEvent / RepoDirDeletedEvent) will update the
        // live maps and counts much more cheaply.
        //
        // Important: we still refresh _snapshotView so path decoding continues to use the latest
        // snapshot arrays/string pools.
        if (evt.Reason == RepoSnapshotCommitReason.Mutation)
            return ValueTask.CompletedTask;

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

        var rootId = evt.ScanRootId;

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
        _filesById = _filesById.RemoveMany(evt.DeletedFileIds);
        if (!ReferenceEquals(_dirsById, beforeDirsById))
            removedDirs = evt.DeletedDirs;
        if (!ReferenceEquals(_filesById, beforeFilesById))
            removedFiles = evt.DeletedFiles;

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

        s_log.Debug(
            "FileDirIndexPlugin applied RepoDirDeletedEvent gen={gen}, scanRootId={scanRootId}, removedDirs={removedDirs}, removedFiles={removedFiles}",
            evt.Generation,
            evt.Dir.ScanRootId,
            removedDirs,
            removedFiles);

        return ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------------
    // Core index maintenance (build -> publish)
    // ---------------------------------------------------------------------

    private void RebuildFromSnapshot(RepoSnapshotView repoSnapshot)
    {
        var activeRootIds = repoSnapshot.ScanRoots.Values
            .Where(r => !r.IsDeleted)
            .Select(r => r.RootId)
            .ToHashSet();

        var liveSnapshots = new Dictionary<ScanRootId, ScanRootSnapshotView>(
            repoSnapshot.Snapshots.Where(kvp => activeRootIds.Contains(kvp.Key)));

        // Capacity hints based on live entries only.
        var totalDirs = 0;
        var totalFiles = 0;
        foreach (var (_, s) in liveSnapshots)
        {
            totalDirs += s.Dirs.Count(d => d.Status is not (ScanEntryStatus.Deleted or ScanEntryStatus.None));
            totalFiles += s.Files.Count(f => f.Status is not (ScanEntryStatus.Deleted or ScanEntryStatus.None));
        }

        var newDirs = new Dictionary<DirId, DirHandle>(capacity: totalDirs);
        var newFiles = new Dictionary<FileId, FileHandle>(capacity: totalFiles);

        var newDirCounts = new Dictionary<ScanRootId, int>(capacity: liveSnapshots.Count);
        var newFileCounts = new Dictionary<ScanRootId, int>(capacity: liveSnapshots.Count);

        var activeDirCount = 0;
        var activeFileCount = 0;

        foreach (var (rootId, snapshot) in liveSnapshots)
        {
            var rootDirCount = 0;
            var rootFileCount = 0;

            for (int i = 0; i < snapshot.Dirs.Count; i++)
            {
                var dir = snapshot.Dirs[i];
                if (dir.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                    continue;

                if (!newDirs.TryAdd(dir.DirId, new DirHandle(rootId, i)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate dirId {dir.DirId} encountered while rebuilding FileDirIndexPlugin.");
                }

                rootDirCount++;
                activeDirCount++;
            }

            for (int i = 0; i < snapshot.Files.Count; i++)
            {
                var file = snapshot.Files[i];
                if (file.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                    continue;

                if (!newFiles.TryAdd(file.FileId, new FileHandle(rootId, i)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate FileId {file.FileId} encountered while rebuilding FileDirIndexPlugin.");
                }

                rootFileCount++;
                activeFileCount++;
            }

            newDirCounts[rootId] = rootDirCount;
            newFileCounts[rootId] = rootFileCount;
        }

        // Publish in a coherent order:
        // 1) publish dictionaries
        // 2) publish counts + active roots
        // 3) publish snapshot view last so readers see coherent state for Decode* usage
        _dirsById = SegmentedMap<DirHandle>.FromDictionary(newDirs);
        _filesById = SegmentedMap<FileHandle>.FromDictionary(newFiles);

        _activeScanRoots = activeRootIds;
        _dirCountByRootId = newDirCounts;
        _fileCountByRootId = newFileCounts;
        _activeDirCount = activeDirCount;
        _activeFileCount = activeFileCount;

        _snapshotView = repoSnapshot;
    }

    private void EnsureCountsFromSnapshotIfMissing(RepoSnapshotView snapshot)
    {
        // If counts dictionaries are empty (e.g., older persisted state), recompute quickly.
        if (_dirCountByRootId.Count != 0 || _fileCountByRootId.Count != 0)
            return;

        var activeRootIds = snapshot.ScanRoots.Values
            .Where(r => !r.IsDeleted)
            .Select(r => r.RootId)
            .ToHashSet();

        var dirCounts = new Dictionary<ScanRootId, int>(capacity: activeRootIds.Count);
        var fileCounts = new Dictionary<ScanRootId, int>(capacity: activeRootIds.Count);

        var activeDirCount = 0;
        var activeFileCount = 0;

        foreach (var rootId in activeRootIds)
        {
            if (!snapshot.Snapshots.TryGetValue(rootId, out var sr))
                continue;

            var rootDirCount = 0;
            for (int i = 0; i < sr.Dirs.Count; i++)
            {
                if (sr.Dirs[i].Status is not (ScanEntryStatus.Deleted or ScanEntryStatus.None))
                    rootDirCount++;
            }

            var rootFileCount = 0;
            for (int i = 0; i < sr.Files.Count; i++)
            {
                if (sr.Files[i].Status is not (ScanEntryStatus.Deleted or ScanEntryStatus.None))
                    rootFileCount++;
            }

            dirCounts[rootId] = rootDirCount;
            fileCounts[rootId] = rootFileCount;

            activeDirCount += rootDirCount;
            activeFileCount += rootFileCount;
        }

        _activeScanRoots = activeRootIds;
        _dirCountByRootId = dirCounts;
        _fileCountByRootId = fileCounts;
        _activeDirCount = activeDirCount;
        _activeFileCount = activeFileCount;
    }

    // ---------------------------------------------------------------------
    // Persistence
    // ---------------------------------------------------------------------

    private string GetStateFilePath() => Path.Combine(_dataDirectory, StateFileName);

    private void SaveState()
    {
        // Snapshot local references to ensure consistency during enumeration.
        var dirs = _dirsById;
        var files = _filesById;

        var state = new FileDirIndexState
        {
            LastIndexedGeneration = _lastIndexedGeneration, DirsById = dirs, FilesById = files
        };

        var path = GetStateFilePath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        MemoryPackFile.SaveToFile(path, state);
    }

    private bool TryLoadState(long expectedGeneration)
    {
        var path = GetStateFilePath();
        if (!File.Exists(path))
            return false;

        FileDirIndexState? state;
        using (TimingLog.StartPhase("Deserialising FileDirIndexState"))
        {
            if (!MemoryPackFile.TryLoadMapped(path, out state, CancellationToken.None) || state is null)
                return false;
        }

        // Only use the state if it matches the current repo position.
        if (state.LastIndexedGeneration != expectedGeneration)
            return false;

        _dirsById = state.DirsById;
        _filesById = state.FilesById;

        _lastIndexedGeneration = state.LastIndexedGeneration;
        return true;
    }

    // ---------------------------------------------------------------------
    // Public query surface (lock-free)
    // ---------------------------------------------------------------------

    public bool TryGetDir(DirId dirId, out DirHandle handle) => _dirsById.TryGetValue(dirId, out handle);

    public bool TryGetFile(FileId fileId, out FileHandle handle) => _filesById.TryGetValue(fileId, out handle);

    public bool TryGetFilePathById(FileId fileId, out string relativePath)
    {
        relativePath = string.Empty;
        return TryGetFile(fileId, out var handle) && TryGetFilePathByHandle(handle, out relativePath);
    }

    public bool TryGetFilePathByHandle(FileHandle fileHandle, out string relativePath)
    {
        relativePath = string.Empty;

        if (!_activeScanRoots.Contains(fileHandle.ScanRootId))
            return false;

        var view = _snapshotView;
        if (view is null)
            return false;

        if (!view.Snapshots.TryGetValue(fileHandle.ScanRootId, out var snapshot))
            return false;

        if ((uint)fileHandle.Index >= (uint)snapshot.Files.Count)
            return false;

        var file = snapshot.Files[fileHandle.Index];

        // Collect segments bottom-up: [fileName, dirName, dirName, ...]
        var segments = new List<string>(capacity: 8) { view.DecodeFileName(fileHandle) };

        // Snapshot dirs dictionary for stable lookups during traversal
        var dirsById = _dirsById;

        DirId dirId = file.DirId;
        while (dirId > 0)
        {
            if (!dirsById.TryGetValue(dirId, out var dh))
                return false;

            if (dh.ScanRootId != fileHandle.ScanRootId)
                return false; // should not happen

            if ((uint)dh.Index >= (uint)snapshot.Dirs.Count)
                return false;

            var dir = snapshot.Dirs[dh.Index];
            segments.Add(view.DecodeDirName(dh));
            dirId = dir.ParentDirId;
        }

        // Build relative path in correct order
        segments.Reverse();

        relativePath = segments.Count == 0 ? string.Empty : segments[0];
        for (int i = 1; i < segments.Count; i++)
            relativePath = Path.Combine(relativePath, segments[i]);

        return true;
    }

    public bool TryGetDirPathById(DirId dirId, out string relativePath)
    {
        relativePath = string.Empty;
        return TryGetDir(dirId, out var handle) && TryGetDirPathByHandle(handle, out relativePath);
    }

    public bool TryGetDirPathByHandle(DirHandle dirHandle, out string relativePath)
    {
        relativePath = string.Empty;

        if (!_activeScanRoots.Contains(dirHandle.ScanRootId))
            return false;

        var view = _snapshotView;
        if (view is null)
            return false;

        if (!view.Snapshots.TryGetValue(dirHandle.ScanRootId, out var snapshot))
            return false;

        if ((uint)dirHandle.Index >= (uint)snapshot.Dirs.Count)
            return false;

        var segments = new List<string>(capacity: 8);

        // Snapshot dirs dictionary for stable lookups during traversal
        var dirsById = _dirsById;

        var dir = snapshot.Dirs[dirHandle.Index];
        segments.Add(view.DecodeDirName(dirHandle));

        DirId parentId = dir.ParentDirId;
        while (parentId > 0)
        {
            if (!dirsById.TryGetValue(parentId, out var parentHandle))
                return false;

            if (parentHandle.ScanRootId != dirHandle.ScanRootId)
                return false;

            if ((uint)parentHandle.Index >= (uint)snapshot.Dirs.Count)
                return false;

            var parent = snapshot.Dirs[parentHandle.Index];
            segments.Add(view.DecodeDirName(parentHandle));
            parentId = parent.ParentDirId;
        }

        segments.Reverse();

        relativePath = segments.Count == 0 ? string.Empty : segments[0];
        for (int i = 1; i < segments.Count; i++)
            relativePath = Path.Combine(relativePath, segments[i]);

        return true;
    }
}
