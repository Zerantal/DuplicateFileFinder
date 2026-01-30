// DuplicateFileFinderLib/Repository/Plugins/TreeIndexPlugin.cs

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins;

public sealed class TreeIndexPlugin : ChannelRepoPlugin, ITreeIndexReadModel
{
    private const string StateFileName = "tree-index.bin";

    // Dir indices are mostly dense per scan root, but some holes exist (Deleted/None).
    // Allow modest gaps to stay within a single segment.
    private const int SegmentGapThreshold = 64;

    // Published, read-only snapshots (never mutate after publishing).
    // Rebuilt on plugin worker thread; swapped atomically for readers.
    private volatile Dictionary<long, RootTreeIndexState> _roots = new();

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

    public ReadOnlySpan<DirHandle> GetChildDirs(DirHandle dir)
    {
        var roots = _roots;

        if (!roots.TryGetValue(dir.ScanRootId, out var root))
            return ReadOnlySpan<DirHandle>.Empty;

        if (!root.ChildDirSliceByDirIndex.TryGetValue(dir.Index, out var slice) || slice.IsEmpty)
            return ReadOnlySpan<DirHandle>.Empty;

        return root.ChildDirsPool.AsSpan(slice.Offset, slice.Length);
    }

    public ReadOnlySpan<FileHandle> GetChildFiles(DirHandle dir)
    {
        var roots = _roots;

        if (!roots.TryGetValue(dir.ScanRootId, out var root))
            return ReadOnlySpan<FileHandle>.Empty;

        if (!root.ChildFileSliceByDirIndex.TryGetValue(dir.Index, out var slice) || slice.IsEmpty)
            return ReadOnlySpan<FileHandle>.Empty;

        return root.ChildFilesPool.AsSpan(slice.Offset, slice.Length);
    }

    public DirAggregateStats GetDirStats(DirHandle dir)
    {
        var roots = _roots;

        return roots.TryGetValue(dir.ScanRootId, out var root) &&
               root.StatsByDirIndex.TryGetValue(dir.Index, out var s)
            ? s
            : new DirAggregateStats
            {
                DirCount = 0,
                FileCount = 0,
                TotalBytes = 0,
                DuplicateFiles = 0,
                DuplicateBytes = 0
            };
    }

    public bool TryGetSubtreeRange(DirHandle dir, out SubtreeRange range)
    {
        var roots = _roots;

        if (!roots.TryGetValue(dir.ScanRootId, out var root))
        {
            range = default;
            return false;
        }

        return root.SubtreeRangeByDirIndex.TryGetValue(dir.Index, out range);
    }

    public bool TryGetFileDirPreorder(FileHandle file, out int preorder)
    {
        var roots = _roots;

        if (!roots.TryGetValue(file.ScanRootId, out var root))
        {
            preorder = -1;
            return false;
        }

        var arr = root.DirPreorderByFileIndex;
        if ((uint)file.Index >= (uint)arr.Length)
        {
            preorder = -1;
            return false;
        }

        preorder = arr[file.Index];
        return preorder >= 0;
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

    protected override void OnScanRootSnapshotReplacedEvent(ScanRootSnapshotReplacedEvent evt)
    {
        // Ignore stale/out-of-order events (channel may drop old items).
        if (evt.Generation <= _lastIndexedGeneration)
            return;

        // Rebuild from the new snapshot view and persist.
        RebuildFromSnapshot(evt.RepoSnapshotView);

        _lastIndexedGeneration = evt.Generation;
        SaveState();
    }

    protected override void OnRepoScanRootRemovedEvent(RepoScanRootRemovedEvent evt)
    {
        // Ignore stale/out-of-order events (channel may drop old items).
        if (evt.Generation <= _lastIndexedGeneration)
            return;

        var removedRootId = evt.ScanRootId;

        var oldRoots = _roots;

        // remove the per-root entry.
        if (!oldRoots.ContainsKey(removedRootId))
        {
            _lastIndexedGeneration = evt.Generation;
            SaveState();
            return;
        }

        var newRoots = new Dictionary<long, RootTreeIndexState>(Math.Max(0, oldRoots.Count - 1));
        foreach (var (k, v) in oldRoots)
            if (k != removedRootId)
                newRoots[k] = v;

        _roots = newRoots;

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
            var liveRootIds = new HashSet<long>(
                repoSnapshot.ScanRoots.Values.Where(r => !r.IsDeleted).Select(r => r.RootId));

            var liveSnapshots = new Dictionary<long, ScanRootSnapshotView>(capacity: liveRootIds.Count);
            foreach (var (rootId, snap) in repoSnapshot.Snapshots)
                if (liveRootIds.Contains(rootId))
                    liveSnapshots[rootId] = snap;

            // -----------------------------------------------------------------
            // 1) Global duplicate detection across ALL live scan roots
            // -----------------------------------------------------------------
            var globalHashCounts = new Dictionary<HashKey, int>(capacity: 1024);

            foreach (var snap in liveSnapshots.Values)
            {
                var files = snap.Files;
                for (int i = 0; i < files.Count; i++)
                {
                    var f = files[i];

                    if (f.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                        continue;

                    if (f.Size <= 0)
                        continue;

                    if (f.Hash == HashKey.NotComputed || f.Hash == HashKey.CannotCompute)
                        continue;

                    if (globalHashCounts.TryGetValue(f.Hash, out var c))
                        globalHashCounts[f.Hash] = c + 1;
                    else
                        globalHashCounts[f.Hash] = 1;
                }
            }

            // -----------------------------------------------------------------
            // 2) Build per-root pools + slice maps + stats + preorder subtree data
            // -----------------------------------------------------------------
            var newRoots = new Dictionary<long, RootTreeIndexState>(capacity: liveSnapshots.Count);

            foreach (var snapshot in liveSnapshots.Values)
            {
                var rootId = snapshot.ScanRootId;

                // Map: DirId -> DirIndex (ONLY for live dirs)
                var dirIdToIndex = new Dictionary<long, int>(capacity: snapshot.Dirs.Count);
                var rootDirIndices = new List<int>();

                for (int i = 0; i < snapshot.Dirs.Count; i++)
                {
                    var dir = snapshot.Dirs[i];

                    // Skip deleted/absent dirs entirely.
                    if (dir.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                        continue;

                    dirIdToIndex[dir.DirId] = i;

                    if (dir.ParentDirId < 0)
                        rootDirIndices.Add(i);
                }

                // Temp accumulators: per parent dirIndex -> handles list
                var childrenDirsTmp = new Dictionary<int, List<DirHandle>>();
                var childrenFilesTmp = new Dictionary<int, List<FileHandle>>();

                // Child dirs (only live parent+child)
                for (int i = 0; i < snapshot.Dirs.Count; i++)
                {
                    var dir = snapshot.Dirs[i];

                    if (dir.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                        continue;

                    if (dir.ParentDirId < 0)
                        continue;

                    // Parent must be live too (otherwise ignore this edge)
                    if (!dirIdToIndex.TryGetValue(dir.ParentDirId, out var parentIndex))
                        continue;

                    var childHandle = new DirHandle(rootId, i);

                    if (!childrenDirsTmp.TryGetValue(parentIndex, out var list))
                    {
                        list = new List<DirHandle>(capacity: 4);
                        childrenDirsTmp[parentIndex] = list;
                    }

                    list.Add(childHandle);
                }

                // Child files (only live files, and only under live dirs)
                for (int i = 0; i < snapshot.Files.Count; i++)
                {
                    var file = snapshot.Files[i];

                    if (file.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                        continue;

                    if (!dirIdToIndex.TryGetValue(file.DirId, out var parentIndex))
                        continue;

                    var fileHandle = new FileHandle(rootId, i);

                    if (!childrenFilesTmp.TryGetValue(parentIndex, out var list))
                    {
                        list = new List<FileHandle>(capacity: 8);
                        childrenFilesTmp[parentIndex] = list;
                    }

                    list.Add(fileHandle);
                }

                // Flatten into pools + slices
                var (childDirsPool, childDirSlices) = BuildPoolAndSlices(childrenDirsTmp);
                var (childFilesPool, childFileSlices) = BuildPoolAndSlices(childrenFilesTmp);

                var childDirSliceMap = childDirSlices.Length == 0
                    ? SegmentedIdMap<Slice>.Empty
                    : SegmentedIdMap<Slice>.Build(childDirSlices, gapThreshold: SegmentGapThreshold);

                var childFileSliceMap = childFileSlices.Length == 0
                    ? SegmentedIdMap<Slice>.Empty
                    : SegmentedIdMap<Slice>.Build(childFileSlices, gapThreshold: SegmentGapThreshold);

                var statsMap = ComputeRootDirStats(
                    scanRootId: rootId,
                    snapshot: snapshot,
                    childDirsPool: childDirsPool,
                    childDirSliceByDirIndex: childDirSliceMap,
                    childFilesPool: childFilesPool,
                    childFileSliceByDirIndex: childFileSliceMap,
                    rootDirIndices: rootDirIndices,
                    globalHashCounts: globalHashCounts);

                // compute preorder subtree intervals + file->dir preorder mapping (Option A)
                var (subtreeRangeMap, dirPreorderByFileIndex) = BuildPreorderData(snapshot: snapshot,
                    rootDirIndices: rootDirIndices,
                    childrenDirsTmp: childrenDirsTmp,
                    dirIdToIndex: dirIdToIndex);

                newRoots[rootId] = new RootTreeIndexState
                {
                    ChildDirsPool = childDirsPool,
                    ChildFilesPool = childFilesPool,
                    ChildDirSliceByDirIndex = childDirSliceMap,
                    ChildFileSliceByDirIndex = childFileSliceMap,
                    StatsByDirIndex = statsMap,
                    SubtreeRangeByDirIndex = subtreeRangeMap,
                    DirPreorderByFileIndex = dirPreorderByFileIndex
                };
            }

            _roots = newRoots;
        }
    }

    private static (SegmentedIdMap<SubtreeRange> SubtreeRangeByDirIndex, int[] DirPreorderByFileIndex) BuildPreorderData(
        ScanRootSnapshotView snapshot,
        IReadOnlyList<int> rootDirIndices,
        Dictionary<int, List<DirHandle>> childrenDirsTmp,
        Dictionary<long, int> dirIdToIndex)
    {
        // preorderByDirIndex[dirIndex] == preorder, or -1 if not visited/live
        var preorderByDirIndex = new int[snapshot.Dirs.Count];
        var exitByDirIndex = new int[snapshot.Dirs.Count];

        Array.Fill(preorderByDirIndex, -1);
        Array.Fill(exitByDirIndex, -1);

        var clock = 0;

        for (int r = 0; r < rootDirIndices.Count; r++)
        {
            var root = rootDirIndices[r];
            if ((uint)root >= (uint)preorderByDirIndex.Length)
                continue;

            if (preorderByDirIndex[root] >= 0)
                continue;

            // Iterative DFS: stack of (dirIndex, nextChildIdx)
            var stack = new Stack<(int dir, int nextChild)>(capacity: 64);

            preorderByDirIndex[root] = clock++;
            stack.Push((root, 0));

            while (stack.Count > 0)
            {
                var (d, next) = stack.Pop();

                if (!childrenDirsTmp.TryGetValue(d, out var kids) || kids.Count == 0)
                {
                    exitByDirIndex[d] = clock;
                    continue;
                }

                if (next < kids.Count)
                {
                    // Resume this node later with next child
                    stack.Push((d, next + 1));

                    var childIndex = kids[next].Index;
                    if ((uint)childIndex >= (uint)preorderByDirIndex.Length)
                        continue;

                    if (preorderByDirIndex[childIndex] >= 0)
                        continue;

                    preorderByDirIndex[childIndex] = clock++;
                    stack.Push((childIndex, 0));
                }
                else
                {
                    exitByDirIndex[d] = clock;
                }
            }
        }

        // Build ranges for visited dirs
        var rangeItems = new List<KeyValuePair<long, SubtreeRange>>(capacity: 1024);

        for (int i = 0; i < preorderByDirIndex.Length; i++)
        {
            var pre = preorderByDirIndex[i];
            if (pre < 0)
                continue;

            var end = exitByDirIndex[i] >= 0 ? exitByDirIndex[i] : pre + 1;
            rangeItems.Add(new KeyValuePair<long, SubtreeRange>(i, new SubtreeRange(pre, end)));
        }

        var subtreeRangeMap = rangeItems.Count == 0
            ? SegmentedIdMap<SubtreeRange>.Empty
            : SegmentedIdMap<SubtreeRange>.Build(rangeItems.ToArray(), gapThreshold: SegmentGapThreshold);

        // Per-file mapping: fileIndex -> preorder(parent dir), or -1
        var dirPreorderByFileIndex = new int[snapshot.Files.Count];
        Array.Fill(dirPreorderByFileIndex, -1);

        for (int fi = 0; fi < snapshot.Files.Count; fi++)
        {
            var f = snapshot.Files[fi];

            if (f.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                continue;

            if (!dirIdToIndex.TryGetValue(f.DirId, out var parentDirIndex))
                continue;

            if ((uint)parentDirIndex >= (uint)preorderByDirIndex.Length)
                continue;

            dirPreorderByFileIndex[fi] = preorderByDirIndex[parentDirIndex];
        }

        return (subtreeRangeMap, dirPreorderByFileIndex);
    }

    private static (T[] Pool, KeyValuePair<long, Slice>[] SliceItems) BuildPoolAndSlices<T>(
        Dictionary<int, List<T>> tmp)
    {
        if (tmp.Count == 0)
            return (Array.Empty<T>(), Array.Empty<KeyValuePair<long, Slice>>());

        // Deterministic ordering to keep state stable across rebuilds.
        var keys = tmp.Keys.ToArray();
        Array.Sort(keys);

        // Precompute total elements to allocate once.
        int total = 0;
        for (int i = 0; i < keys.Length; i++)
            total += tmp[keys[i]].Count;

        var pool = total == 0 ? Array.Empty<T>() : new T[total];
        var sliceItems = new KeyValuePair<long, Slice>[keys.Length];

        int write = 0;

        for (int i = 0; i < keys.Length; i++)
        {
            var dirIndex = keys[i];
            var list = tmp[dirIndex];

            var offset = write;
            var len = list.Count;

            if (len > 0)
            {
                // Copy into pool
                list.CopyTo(pool, write);
                write += len;
            }

            sliceItems[i] = new KeyValuePair<long, Slice>(dirIndex, new Slice(offset, len));
        }

        // If total was overestimated (shouldn't happen), trim. Otherwise keep as-is.
        if (write != pool.Length)
            Array.Resize(ref pool, write);

        return (pool, sliceItems);
    }

    private static SegmentedIdMap<DirAggregateStats> ComputeRootDirStats(
        long scanRootId,
        ScanRootSnapshotView snapshot,
        DirHandle[] childDirsPool,
        SegmentedIdMap<Slice> childDirSliceByDirIndex,
        FileHandle[] childFilesPool,
        SegmentedIdMap<Slice> childFileSliceByDirIndex,
        IReadOnlyList<int> rootDirIndices,
        Dictionary<HashKey, int> globalHashCounts)
    {
        var memo = new Dictionary<int, DirAggregateStats>(capacity: Math.Max(1024, rootDirIndices.Count));

        DirAggregateStats Dfs(int dirIndex)
        {
            if (memo.TryGetValue(dirIndex, out var cached))
                return cached;

            long bytes = 0;
            int fileCount = 0;
            int dirCount = 0;
            long duplicateFiles = 0;
            long duplicateBytes = 0;

            // Files directly under this dir
            if (childFileSliceByDirIndex.TryGetValue(dirIndex, out var fileSlice) && fileSlice.Length > 0)
            {
                var span = childFilesPool.AsSpan(fileSlice.Offset, fileSlice.Length);

                for (int i = 0; i < span.Length; i++)
                {
                    var fh = span[i];

                    // Defensive (should always match).
                    if (fh.ScanRootId != scanRootId)
                        continue;

                    var f = snapshot.Files[fh.Index];

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
            if (childDirSliceByDirIndex.TryGetValue(dirIndex, out var dirSlice) && dirSlice.Length > 0)
            {
                var span = childDirsPool.AsSpan(dirSlice.Offset, dirSlice.Length);

                for (int i = 0; i < span.Length; i++)
                {
                    var child = span[i];

                    if (child.ScanRootId != scanRootId)
                        continue;

                    dirCount++;

                    var childStats = Dfs(child.Index);
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

            memo[dirIndex] = stats;
            return stats;
        }

        for (int i = 0; i < rootDirIndices.Count; i++)
            _ = Dfs(rootDirIndices[i]);

        if (memo.Count == 0)
            return SegmentedIdMap<DirAggregateStats>.Empty;

        var items = new KeyValuePair<long, DirAggregateStats>[memo.Count];
        int w = 0;
        foreach (var (dirIndex, stats) in memo)
            items[w++] = new KeyValuePair<long, DirAggregateStats>(dirIndex, stats);

        return SegmentedIdMap<DirAggregateStats>.Build(items);
    }

    // ---------------------------------------------------------------------
    // Persistence
    // ---------------------------------------------------------------------

    private string GetStateFilePath() => Path.Combine(_dataDirectory, StateFileName);

    private void SaveState()
    {
        var roots = _roots;

        var state = new TreeIndexState
        {
            LastIndexedGeneration = _lastIndexedGeneration,
            Roots = roots
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

        TreeIndexState? state;
        using (TimingLog.StartPhase("Deserialising TreeIndex state"))
        {
            if (!MemoryPackFile.TryLoadMapped(path, out state, CancellationToken.None) || state is null)
                return false;
        }

        if (state.LastIndexedGeneration != expectedGeneration)
            return false;

        _lastIndexedGeneration = state.LastIndexedGeneration;
        _roots = state.Roots;

        return true;
    }
}
