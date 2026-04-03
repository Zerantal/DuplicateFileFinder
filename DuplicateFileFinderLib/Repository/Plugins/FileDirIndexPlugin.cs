using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage;

namespace DuplicateFileFinderLib.Repository.Plugins;

public sealed class FileDirIndexPlugin : ChannelRepoPlugin, IFileDirReadModel
{
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

        using (TimingLog.StartPhase($"Rebuilding FileDirIndexPlugin (ScanRoot {evt.ScanRootId}, Gen {evt.Generation})"))
        {
            RebuildFromSnapshot(evt.RepoSnapshotView);
        }

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

    // ---------------------------------------------------------------------
    // Core index maintenance (build -> publish)
    // ---------------------------------------------------------------------

    private void RebuildFromSnapshot(RepoSnapshotView repoSnapshot)
    {
        using (TimingLog.StartPhase("Rebuilding FileDirIndex"))
        {
            var activeRootIds = repoSnapshot.ScanRoots.Values
                .Where(r => !r.IsDeleted)
                .Select(r => r.RootId)
                .ToHashSet();

            var liveSnapshots = new Dictionary<ScanRootId, ScanRootSnapshotView>(
                repoSnapshot.Snapshots.Where(kvp => activeRootIds.Contains(kvp.Key)));

            // Build fresh dictionaries (no shared state with readers).
            // Capacity hints reduce rehashing.
            var totalDirs = 0;
            var totalFiles = 0;
            foreach (var (_, s) in liveSnapshots)
            {
                totalDirs += s.Dirs.Count;
                totalFiles += s.Files.Count;
            }

            var newDirs = new Dictionary<DirId, DirHandle>(capacity: totalDirs);
            var newFiles = new Dictionary<FileId, FileHandle>(capacity: totalFiles);

            var newDirCounts = new Dictionary<ScanRootId, int>(capacity: liveSnapshots.Count);
            var newFileCounts = new Dictionary<ScanRootId, int>(capacity: liveSnapshots.Count);

            var activeDirCount = 0;
            var activeFileCount = 0;

            foreach (var (rootId, snapshot) in liveSnapshots)
            {
                // Per-root counts from the snapshot arrays
                var dirCount = snapshot.Dirs.Count;
                var fileCount = snapshot.Files.Count;

                newDirCounts[rootId] = dirCount;
                newFileCounts[rootId] = fileCount;

                activeDirCount += dirCount;
                activeFileCount += fileCount;

                for (int i = 0; i < snapshot.Dirs.Count; i++)
                {
                    var dir = snapshot.Dirs[i];
                    if (!newDirs.TryAdd(dir.DirId, new DirHandle(rootId, i)))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate dirId {dir.DirId} encountered while rebuilding FileDirIndexPlugin.");
                    }
                }

                for (int i = 0; i < snapshot.Files.Count; i++)
                {
                    var file = snapshot.Files[i];
                    if (!newFiles.TryAdd(file.FileId, new FileHandle(rootId, i)))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate FileId {file.FileId} encountered while rebuilding FileDirIndexPlugin.");
                    }
                }
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

            dirCounts[rootId] = sr.Dirs.Count;
            fileCounts[rootId] = sr.Files.Count;

            activeDirCount += sr.Dirs.Count;
            activeFileCount += sr.Files.Count;
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
            LastIndexedGeneration = _lastIndexedGeneration,
            DirsById = dirs,
            FilesById = files
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
