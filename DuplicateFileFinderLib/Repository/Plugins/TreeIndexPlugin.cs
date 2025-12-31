using System.Collections.Immutable;

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins;

public sealed class TreeIndexPlugin : ChannelRepoPlugin, ITreeIndexReadModel
{
    private const string StateFileName = "tree-index.bin";

    // Published, read-only snapshots (never mutate after publishing).
    // Rebuilt on plugin worker thread; swapped atomically for readers.
    private volatile Dictionary<DirHandle, ImmutableArray<DirHandle>> _childrenDirsByParentId
        = new();

    private volatile Dictionary<DirHandle, ImmutableArray<FileHandle>> _childrenFilesByDirId
        = new();

    private volatile Dictionary<DirHandle, DirAggregateStats> _dirStatsById
        = new();

    private readonly string _dataDirectory;
    private long _lastIndexedGeneration;

    public TreeIndexPlugin(string dataDirectory)
        : base(4096)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory must be non-empty.", nameof(dataDirectory));

        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(_dataDirectory);
    }

    // ---------------------------------------------------------------------
    // Public query surface (lock-free)
    // ---------------------------------------------------------------------

    public ImmutableArray<DirHandle> GetChildDirs(DirHandle dir)
    {
        var map = _childrenDirsByParentId;
        return map.TryGetValue(dir, out var v) ? v : ImmutableArray<DirHandle>.Empty;
    }

    public ImmutableArray<FileHandle> GetChildFiles(DirHandle dir)
    {
        var map = _childrenFilesByDirId;
        return map.TryGetValue(dir, out var v) ? v : ImmutableArray<FileHandle>.Empty;
    }

    public DirAggregateStats GetDirStats(DirHandle dir)
    {
        var map = _dirStatsById;
        return map.TryGetValue(dir, out var s)
            ? s
            : new DirAggregateStats { DirCount = 0, FileCount = 0, TotalBytes = 0, DuplicateFiles = 0, DuplicateBytes = 0 };
    }

    // ---------------------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------------------

    protected override void OnBootstrapEvent(BootstrapEvent evt)
    {
        if (!TryLoadState(evt.Generation))
        {
            // Fallback: rebuild from snapshot and persist.
            RebuildFromSnapshot(evt.RepoSnapshotView);
            _lastIndexedGeneration = evt.Generation;
            SaveState();
        }
        else
        {
            _lastIndexedGeneration = evt.Generation;
        }
    }

    protected override void OnScanRootSnapshotCommittedEvent(ScanRootSnapshotCommittedEvent evt)
    {
        // Ignore stale/out-of-order events (channel may drop old items).
        if (evt.Generation <= _lastIndexedGeneration)
            return;

        // Rebuild from the new snapshot view and persist.
        RebuildFromSnapshot(evt.RepoSnapshotView);

        _lastIndexedGeneration = evt.Generation;
        SaveState();
    }


    // ---------------------------------------------------------------------
    // Core index maintenance (build -> publish)
    // ---------------------------------------------------------------------

    private void RebuildFromSnapshot(RepoSnapshotView repoSnapshot)
    {
        using (TimingLog.StartPhase("Rebuilding TreeIndex"))
        {
            var snapshotDict = repoSnapshot.Snapshots;

            // Temp accumulators (lists) to avoid repeated ImmutableArray allocations.
            var childrenDirsTmp = new Dictionary<DirHandle, List<DirHandle>>();
            var childrenFilesTmp = new Dictionary<DirHandle, List<FileHandle>>();

            // Forest roots for stats DFS.
            var rootDirs = new List<DirHandle>();

            foreach (var snapshot in snapshotDict.Values)
            {
                var rootId = snapshot.ScanRootId;

                // Map: DirId -> DirHandle (ONLY for live dirs)
                var dirIdToHandle = new Dictionary<long, DirHandle>(capacity: snapshot.Dirs.Count);

                for (int i = 0; i < snapshot.Dirs.Count; i++)
                {
                    var dir = snapshot.Dirs[i];

                    // Skip deleted/absent dirs entirely.
                    if (dir.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                        continue;

                    var h = new DirHandle(rootId, i);
                    dirIdToHandle[dir.DirId] = h;

                    if (dir.ParentDirId < 0)
                        rootDirs.Add(h);
                }

                // Child dirs (only live parent+child)
                for (int i = 0; i < snapshot.Dirs.Count; i++)
                {
                    var dir = snapshot.Dirs[i];

                    if (dir.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                        continue;

                    if (dir.ParentDirId < 0)
                        continue;

                    // Parent must be live too (otherwise ignore this edge)
                    if (!dirIdToHandle.TryGetValue(dir.ParentDirId, out var parentHandle))
                        continue;

                    var childHandle = new DirHandle(rootId, i);

                    if (!childrenDirsTmp.TryGetValue(parentHandle, out var list))
                    {
                        list = new List<DirHandle>(capacity: 4);
                        childrenDirsTmp[parentHandle] = list;
                    }

                    list.Add(childHandle);
                }

                // Child files (only live files, and only under live dirs)
                for (int i = 0; i < snapshot.Files.Count; i++)
                {
                    var file = snapshot.Files[i];

                    if (file.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                        continue;

                    if (!dirIdToHandle.TryGetValue(file.DirId, out var parentDirHandle))
                        continue;

                    var fileHandle = new FileHandle(rootId, i);

                    if (!childrenFilesTmp.TryGetValue(parentDirHandle, out var list))
                    {
                        list = new List<FileHandle>(capacity: 8);
                        childrenFilesTmp[parentDirHandle] = list;
                    }

                    list.Add(fileHandle);
                }
            }

            // Freeze into published dictionaries (values are ImmutableArray for cheap reads).
            // Capacity hints prevent rehashing.
            var newChildDirs = new Dictionary<DirHandle, ImmutableArray<DirHandle>>(childrenDirsTmp.Count);
            foreach (var (parent, list) in childrenDirsTmp)
                newChildDirs[parent] = list.Count == 0 ? ImmutableArray<DirHandle>.Empty : [.. list];

            var newChildFiles = new Dictionary<DirHandle, ImmutableArray<FileHandle>>(childrenFilesTmp.Count);
            foreach (var (parent, list) in childrenFilesTmp)
                newChildFiles[parent] = list.Count == 0 ? ImmutableArray<FileHandle>.Empty : [.. list];

            var newStats = ComputeDirStats(snapshotDict, newChildDirs, newChildFiles, rootDirs);

            // Publish in a coherent order (single-writer pattern):
            // 1) children maps
            // 2) stats
            _childrenDirsByParentId = newChildDirs;
            _childrenFilesByDirId = newChildFiles;
            _dirStatsById = newStats;
        }
    }

    private static Dictionary<DirHandle, DirAggregateStats> ComputeDirStats(
        IReadOnlyDictionary<long, ScanRootSnapshotView> snapshotDict,
        Dictionary<DirHandle, ImmutableArray<DirHandle>> childrenDirsByParent,
        Dictionary<DirHandle, ImmutableArray<FileHandle>> childrenFilesByDir,
        IReadOnlyList<DirHandle> rootDirs)
    {
        // -----------------------------------------------------------------
        // 1) Global duplicate detection across ALL scan roots
        // -----------------------------------------------------------------
        // Count computed hashes for live files (filtered earlier by RebuildFromSnapshot),
        // but keep the same Size>0 rule as FileCount/TotalBytes for consistency.
        var globalHashCounts = new Dictionary<HashKey, int>(capacity: 1024);

        foreach (var snap in snapshotDict.Values)
        {
            var files = snap.Files;
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];

                // RebuildFromSnapshot already filtered Deleted/None into childrenFilesByDir,
                // but be defensive in case of future changes.
                if (f.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                    continue;

                if (f.Size <= 0)
                    continue;

                // Only count computed hashes. (NotComputed/CannotCompute are not duplicates)
                if (f.Hash == HashKey.NotComputed || f.Hash == HashKey.CannotCompute)
                    continue;

                if (globalHashCounts.TryGetValue(f.Hash, out var c))
                    globalHashCounts[f.Hash] = c + 1;
                else
                    globalHashCounts[f.Hash] = 1;
            }
        }

        // -----------------------------------------------------------------
        // 2) Memoized DFS over the directory forest
        // -----------------------------------------------------------------
        var memo = new Dictionary<DirHandle, DirAggregateStats>(capacity: Math.Max(1024, rootDirs.Count));

        DirAggregateStats Dfs(DirHandle dir)
        {
            if (memo.TryGetValue(dir, out var cached))
                return cached;

            long bytes = 0;
            int fileCount = 0;
            int dirCount = 0;
            long duplicateFiles = 0;
            long duplicateBytes = 0;

            // Files directly under this dir
            if (childrenFilesByDir.TryGetValue(dir, out var files))
            {
                for (int i = 0; i < files.Length; i++)
                {
                    var fh = files[i];
                    if (!snapshotDict.TryGetValue(fh.ScanRootId, out var snap))
                        continue;

                    var f = snap.Files[fh.Index];

                    // Should already be live due to childrenFilesByDir build, but keep defensive.
                    if (f.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                        continue;

                    if (f.Size > 0)
                    {
                        bytes += f.Size;
                        fileCount++;

                        // Count this file as "duplicate" if its computed hash appears >= 2 globally.
                        if (f.Hash != HashKey.NotComputed && f.Hash != HashKey.CannotCompute &&
                            globalHashCounts.TryGetValue(f.Hash, out var hc) && hc >= 2)
                        {
                            duplicateFiles++;
                            duplicateBytes += f.Size;
                        }
                    }
                }
            }

            // Recurse into child dirs
            if (childrenDirsByParent.TryGetValue(dir, out var childDirs))
            {
                for (int i = 0; i < childDirs.Length; i++)
                {
                    var child = childDirs[i];

                    // Count the child itself
                    dirCount++;

                    var childStats = Dfs(child);
                    bytes += childStats.TotalBytes;
                    fileCount += childStats.FileCount;
                    dirCount += childStats.DirCount;
                    duplicateFiles += childStats.DuplicateFiles;
                    duplicateBytes += childStats.DuplicateBytes;
                }
            }

            var stats = new DirAggregateStats
            {
                TotalBytes = bytes,
                FileCount = fileCount,
                DirCount = dirCount,
                DuplicateFiles = duplicateFiles,
                DuplicateBytes = duplicateBytes
            };

            memo[dir] = stats;
            return stats;
        }

        // Compute stats for each forest root; DFS will memoize descendants.
        foreach (var t in rootDirs)
            _ = Dfs(t);

        return memo;
    }


    // ---------------------------------------------------------------------
    // Persistence
    // ---------------------------------------------------------------------

    private string GetStateFilePath() => Path.Combine(_dataDirectory, StateFileName);

    private void SaveState()
    {
        // Snapshot the references so the serialized view is consistent.
        var childDirs = _childrenDirsByParentId;
        var childFiles = _childrenFilesByDirId;
        var stats = _dirStatsById;

        var state = new TreeIndexState
        {
            LastIndexedGeneration = _lastIndexedGeneration,
            ChildrenDirsByParentId = childDirs,
            ChildrenFilesByDirId = childFiles,
            DirStatsById = stats
        };

        var path = GetStateFilePath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(path, MemoryPackSerializer.Serialize(state));
    }

    private bool TryLoadState(long expectedGeneration)
    {
        var path = GetStateFilePath();
        if (!File.Exists(path))
            return false;

        try
        {
            TreeIndexState? state;
            using (TimingLog.StartPhase("Deserialising TreeIndex state"))
            {
                var bytes = File.ReadAllBytes(path);
                state = MemoryPackSerializer.Deserialize<TreeIndexState>(bytes);
                if (state is null)
                    return false;
            }

            using (TimingLog.StartPhase("Rehydrating TreeIndex state"))
            {
                // Only use the state if it matches the current repo position.
                if (state.LastIndexedGeneration != expectedGeneration)
                    return false;

                _lastIndexedGeneration = state.LastIndexedGeneration;

                // Publish snapshots (treat deserialized dictionaries as immutable snapshots).
                _childrenDirsByParentId = state.ChildrenDirsByParentId;
                _childrenFilesByDirId = state.ChildrenFilesByDirId;
                _dirStatsById = state.DirStatsById;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
