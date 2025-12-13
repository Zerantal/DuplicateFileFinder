using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins;

public class TreeIndexPlugin : ChannelRepoPlugin, ITreeIndexReadModel
{
    private const string StateFileName = "tree-index.bin";

    private readonly Dictionary<long, List<long>> _childrenDirsByParentId = new();
    private readonly Dictionary<long, List<long>> _childrenFilesByDirId = new();
    
    private readonly Dictionary<long, DirAggregateStats> _dirStatsById = new();

    private readonly string _dataDirectory;
    private readonly Lock _lock = new();

    private long _lastIndexedGeneration;
    private long _lastIndexedLogSequence;

    public TreeIndexPlugin(string dataDirectory)
        : base(4096)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory must be non-empty.", nameof(dataDirectory));

        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(_dataDirectory);
    }

    // ---------------------------------------------------------------------
    // Public query surface
    // ---------------------------------------------------------------------

    public IReadOnlyList<long> GetChildFileIds(long dirId)
    {
        _childrenFilesByDirId.TryGetValue(dirId, out var files);
        return files ?? [];
    }

    public IReadOnlyList<long> GetChildDirIds(long dirId)
    {
        _childrenDirsByParentId.TryGetValue(dirId, out var dirs);
        return dirs ?? [];
    }

    public DirAggregateStats GetDirStats(long dirId)
    {
        lock (_lock)
        {
            if (_dirStatsById.TryGetValue(dirId, out var stats))
                return stats;

            // Default if missing (e.g., unknown dir id)
            return new DirAggregateStats { TotalBytes = 0, FileCount = 0, DirCount = 0 };
        }
    }

    // ---------------------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------------------

    protected override void OnBootstrapEvent(BootstrapEvent evt)
    {
        if (!TryLoadState(evt.Generation, evt.NextLogSequence - 1))
        {
            // Fallback: rebuild from snapshot and persist.
            RebuildFromSnapshot(evt.Snapshot);
            lock (_lock)
            {
                _lastIndexedGeneration = evt.Generation;
                _lastIndexedLogSequence = evt.NextLogSequence - 1;
            }
            SaveState();
        }
        else
        {
            lock (_lock)
            {
                _lastIndexedGeneration = evt.Generation;
                _lastIndexedLogSequence = evt.NextLogSequence - 1;
            }
        }
    }

    protected override void OnCompactedEvent(CompactedEvent evt)
    {
        // After compaction, it’s safer to rebuild from snapshot and persist a clean state.
        RebuildFromSnapshot(evt.Snapshot);
        lock (_lock)
        {
            _lastIndexedGeneration = evt.Generation;
            _lastIndexedLogSequence = evt.NextLogSequence - 1;
        }
        SaveState();
    }

    // ---------------------------------------------------------------------
    // Core index maintenance
    // ---------------------------------------------------------------------

    private void RebuildFromSnapshot(IRepoView snapshot)
    {
        var newChildrenDirsByParentId = new Dictionary<long, List<long>>();
        var newChildrenFilesByDirId = new Dictionary<long, List<long>>();

        // Build child dir map
        foreach (var (dirId, dirRecord) in snapshot.Dirs)
        {
            if (!dirRecord.ParentDirId.HasValue)
                continue;

            var parentDirId = dirRecord.ParentDirId.Value;
            if (!newChildrenDirsByParentId.TryGetValue(parentDirId, out var subdirs))
            {
                subdirs = new List<long>();
                newChildrenDirsByParentId[parentDirId] = subdirs;
            }

            subdirs.Add(dirId);
        }

        // Build child file map
        foreach (var (fileId, fileRecord) in snapshot.Files)
        {
            if (!newChildrenFilesByDirId.TryGetValue(fileRecord.DirId, out var fileList))
            {
                fileList = new List<long>();
                newChildrenFilesByDirId[fileRecord.DirId] = fileList;
            }

            fileList.Add(fileId);
        }

        // Compute aggregates
        var newDirStatsById = ComputeDirStats(snapshot, newChildrenDirsByParentId, newChildrenFilesByDirId);

        lock (_lock)
        {
            _childrenDirsByParentId.Clear();
            _childrenFilesByDirId.Clear();
            _dirStatsById.Clear();

            foreach (var (parentDirId, subdirs) in newChildrenDirsByParentId)
                _childrenDirsByParentId[parentDirId] = subdirs;

            foreach (var (dirId, files) in newChildrenFilesByDirId)
                _childrenFilesByDirId[dirId] = files;

            foreach (var (dirId, stats) in newDirStatsById)
                _dirStatsById[dirId] = stats;
        }
    }

    private static Dictionary<long, DirAggregateStats> ComputeDirStats(
        IRepoView snapshot,
        Dictionary<long, List<long>> childrenDirsByParentId,
        Dictionary<long, List<long>> childrenFilesByDirId)
    {
        // We want, for each dir:
        //  - TotalBytes = sum of sizes of all descendant files
        //  - FileCount  = number of descendant files
        //  - DirCount   = number of descendant dirs (excluding self)
        //
        // Approach: memoized DFS over the directory graph (forest).
        // Snapshot may be large; keep allocations modest.

        var memo = new Dictionary<long, DirAggregateStats>(snapshot.Dirs.Count);

        DirAggregateStats Dfs(long dirId)
        {
            if (memo.TryGetValue(dirId, out var cached))
                return cached;

            long bytes = 0;
            int fileCount = 0;
            int dirCount = 0;

            if (childrenFilesByDirId.TryGetValue(dirId, out var fileIds))
            {
                foreach (var fileId in fileIds)
                {
                    if (!snapshot.Files.TryGetValue(fileId, out var f))
                        continue;
                    if (f.Size > 0)
                    {
                        bytes += f.Size;
                        fileCount++;
                    }
        }
    }

            if (childrenDirsByParentId.TryGetValue(dirId, out var childDirIds))
            {
                foreach (var childId in childDirIds)
                {
                    // Count the child dir itself
                    dirCount++;

                    var childStats = Dfs(childId);
                    bytes += childStats.TotalBytes;
                    fileCount += childStats.FileCount;
                    dirCount += childStats.DirCount; // child's descendants
                }
            }

            var stats = new DirAggregateStats
            {
                TotalBytes = bytes,
                FileCount = fileCount,
                DirCount = dirCount
            };

            memo[dirId] = stats;
            return stats;
        }

        // Compute stats for every directory id in snapshot
        foreach (var dirId in snapshot.Dirs.Keys)
            _ = Dfs(dirId);

        return memo;
    }

    // ---------------------------------------------------------------------
    // Persistence
    // ---------------------------------------------------------------------

    private string GetStateFilePath() => Path.Combine(_dataDirectory, StateFileName);

    private void SaveState()
    {
        TreeIndexState state;

        lock (_lock)
        {
            var childrenDirsByParentCopy = new Dictionary<long, List<long>>(_childrenDirsByParentId.Count);
            var childrenFilesByDirCopy = new Dictionary<long, List<long>>(_childrenFilesByDirId.Count);
            var dirStatsCopy = new Dictionary<long, DirAggregateStats>(_dirStatsById.Count);

            foreach (var (dirId, subdirs) in _childrenDirsByParentId)
                childrenDirsByParentCopy[dirId] = [..subdirs];

            foreach (var (dirId, files) in _childrenFilesByDirId)
                childrenFilesByDirCopy[dirId] = [..files];

            foreach (var (dirId, stats) in _dirStatsById)
                dirStatsCopy[dirId] = stats;

            state = new TreeIndexState
            {
                LastIndexedGeneration = _lastIndexedGeneration,
                LastIndexedLogSequence = _lastIndexedLogSequence,
                ChildrenDirsByParentId = childrenDirsByParentCopy,
                ChildrenFilesByDirId = childrenFilesByDirCopy,
                DirStatsById = dirStatsCopy
            };
        }

        var path = GetStateFilePath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var bytes = MemoryPackSerializer.Serialize(state);
        File.WriteAllBytes(path, bytes);
    }

    private bool TryLoadState(long expectedGeneration, long expectedLastLogSequence)
    {
        var path = GetStateFilePath();
        if (!File.Exists(path))
            return false;

        try
        {
            var bytes = File.ReadAllBytes(path);
            var state = MemoryPackSerializer.Deserialize<TreeIndexState>(bytes);
            if (state is null)
                return false;

            // Only use the state if it matches the current repo position.
            if (state.LastIndexedGeneration != expectedGeneration ||
                state.LastIndexedLogSequence != expectedLastLogSequence)
                return false;

            lock (_lock)
            {
                _childrenDirsByParentId.Clear();
                _childrenFilesByDirId.Clear();
                _dirStatsById.Clear();

                foreach (var (dirId, subdirs) in state.ChildrenDirsByParentId)
                    _childrenDirsByParentId[dirId] = subdirs;

                foreach (var (dirId, files) in state.ChildrenFilesByDirId)
                    _childrenFilesByDirId[dirId] = files;

                foreach (var (dirId, stats) in state.DirStatsById)
                    _dirStatsById[dirId] = stats;

                _lastIndexedGeneration = state.LastIndexedGeneration;
                _lastIndexedLogSequence = state.LastIndexedLogSequence;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
