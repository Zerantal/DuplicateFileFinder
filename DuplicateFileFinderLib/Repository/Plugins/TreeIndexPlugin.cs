// DuplicateFileFinderLib/Repository/Plugins/TreeIndexPlugin.cs

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;

using NLog;

namespace DuplicateFileFinderLib.Repository.Plugins;

public sealed class TreeIndexPlugin : ChannelRepoPlugin, ITreeIndexReadModel
{
    private static readonly Logger s_log = LogManager.GetCurrentClassLogger();

    private const string StateFileName = "tree-index.bin";

    // Dir indices are mostly dense per scan root, but some holes exist (Deleted/None).
    // Allow modest gaps to stay within a single segment.
    private const int SegmentGapThreshold = 64;

    // Published, read-only snapshots (never mutate after publishing).
    // Rebuilt on plugin worker thread; swapped atomically for readers.
    private volatile Dictionary<ScanRootId, RootTreeIndexState> _roots = new();

    // public required int[] ParentDirIndexByDirIndex { get; init; }

    private readonly string _dataDirectory;
    private long _lastIndexedGeneration;

    private volatile RepoSnapshotView? _snapshotView;

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
            _lastIndexedGeneration = evt.Generation;
        }

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnScanRootSnapshotReplacedEventAsync(
        ScanRootSnapshotReplacedEvent evt,
        CancellationToken ct)
    {
        if (evt.Generation <= _lastIndexedGeneration)
            return ValueTask.CompletedTask;

        _snapshotView = evt.RepoSnapshotView;

        s_log.Info("Rebuilding TreeIndex (generation = {0}).", evt.Generation);

        RebuildFromSnapshot(evt.RepoSnapshotView);

        _lastIndexedGeneration = evt.Generation;
        SaveState();

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnRepoScanRootRemovedEventAsync(
        RepoScanRootRemovedEvent evt,
        CancellationToken ct)
    {
        // Ignore stale/out-of-order events (channel may drop old items).
        if (evt.Generation <= _lastIndexedGeneration)
            return ValueTask.CompletedTask;

        var removedRootId = evt.ScanRootIdValue;

        var oldRoots = _roots;

        // remove the per-root entry.
        if (!oldRoots.ContainsKey(removedRootId))
        {
            _lastIndexedGeneration = evt.Generation;
            SaveState();
            return ValueTask.CompletedTask;
        }

        var newRoots = new Dictionary<ScanRootId, RootTreeIndexState>(Math.Max(0, oldRoots.Count - 1));
        foreach (var (k, v) in oldRoots)
            if (k != removedRootId)
                newRoots[k] = v;

        _roots = newRoots;

        _lastIndexedGeneration = evt.Generation;
        SaveState();

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnRepoFileDeletedEventAsync(RepoFileDeletedEvent evt, CancellationToken ct)
    {
        if (evt.Generation <= _lastIndexedGeneration)
            return ValueTask.CompletedTask;

        var snapshot = _snapshotView;
        if (snapshot is null || !snapshot.Snapshots.TryGetValue(evt.File.ScanRootId, out var rootSnapshot))
        {
            _lastIndexedGeneration = evt.Generation;
            SaveState();
            return ValueTask.CompletedTask;
        }

        ApplyFileDeleteToRoot(rootSnapshot, evt.File);

        _lastIndexedGeneration = evt.Generation;
        SaveState();

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnRepoDirDeletedEventAsync(RepoDirDeletedEvent evt, CancellationToken ct)
    {
        if (evt.Generation <= _lastIndexedGeneration)
            return ValueTask.CompletedTask;

        var snapshot = _snapshotView;
        if (snapshot is null || !snapshot.Snapshots.TryGetValue(evt.Dir.ScanRootId, out var rootSnapshot))
        {
            _lastIndexedGeneration = evt.Generation;
            SaveState();
            return ValueTask.CompletedTask;
        }

        var deletedFiles = new FileHandle[evt.DeletedFiles.Length];
        for (var i = 0; i < evt.DeletedFiles.Length; i++)
            deletedFiles[i] = evt.DeletedFiles[i].FileHandle;

        ApplyDirDeleteToRoot(rootSnapshot, evt.Dir, deletedFiles, evt.DeletedDirIds);

        _lastIndexedGeneration = evt.Generation;
        SaveState();

        return ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------------
    // Core index maintenance (build -> publish)
    // ---------------------------------------------------------------------

    private void RebuildFromSnapshot(RepoSnapshotView repoSnapshot)
    {
        _snapshotView = repoSnapshot;

        var liveRootIds = new HashSet<ScanRootId>(
            repoSnapshot.ScanRoots.Values.Where(r => !r.IsDeleted).Select(r => r.RootId));

        var globalHashCounts = ComputeGlobalHashCounts(repoSnapshot);

        var newRoots = new Dictionary<ScanRootId, RootTreeIndexState>(capacity: liveRootIds.Count);

        foreach (var (rootId, snapshot) in repoSnapshot.Snapshots)
        {
            if (!liveRootIds.Contains(rootId))
                continue;

            newRoots[rootId] = BuildRootState(snapshot, globalHashCounts);
        }

        _roots = newRoots;
    }

    private static (SegmentedMap<SubtreeRange> SubtreeRangeByDirIndex, int[] DirPreorderByFileIndex) BuildPreorderData(
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
        var rangeItems = new List<KeyValuePair<DirId, SubtreeRange>>(capacity: 1024);

        for (int i = 0; i < preorderByDirIndex.Length; i++)
        {
            var pre = preorderByDirIndex[i];
            if (pre < 0)
                continue;

            var end = exitByDirIndex[i] >= 0 ? exitByDirIndex[i] : pre + 1;
            rangeItems.Add(new KeyValuePair<DirId, SubtreeRange>(i, new SubtreeRange(pre, end)));
        }

        var subtreeRangeMap = rangeItems.Count == 0
            ? SegmentedMap<SubtreeRange>.Empty
            : SegmentedMap<SubtreeRange>.Build(rangeItems.ToArray(), gapThreshold: SegmentGapThreshold);

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

    private static (TData[] Pool, KeyValuePair<TKey, Slice>[] SliceItems) BuildPoolAndSlices<TKey, TData>(
        Dictionary<TKey, List<TData>> tmp) where TKey : notnull
    {
        if (tmp.Count == 0)
            return ([], []);

        // Deterministic ordering to keep state stable across rebuilds.
        var keys = tmp.Keys.ToArray();
        Array.Sort(keys);

        // Precompute total elements to allocate once.
        int total = 0;
        for (int i = 0; i < keys.Length; i++)
            total += tmp[keys[i]].Count;

        var pool = total == 0 ? [] : new TData[total];
        var sliceItems = new KeyValuePair<TKey, Slice>[keys.Length];

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

            sliceItems[i] = new KeyValuePair<TKey, Slice>(dirIndex, new Slice(offset, len));
        }

        // If total was overestimated (shouldn't happen), trim. Otherwise keep as-is.
        if (write != pool.Length)
            Array.Resize(ref pool, write);

        return (pool, sliceItems);
    }

    private static SegmentedMap<DirAggregateStats> ComputeRootDirStats(
        long scanRootId,
        ScanRootSnapshotView snapshot,
        DirHandle[] childDirsPool,
        SegmentedMap<Slice> childDirSliceByDirIndex,
        FileHandle[] childFilesPool,
        SegmentedMap<Slice> childFileSliceByDirIndex,
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
            return SegmentedMap<DirAggregateStats>.Empty;

        var items = new KeyValuePair<DirId, DirAggregateStats>[memo.Count];
        int w = 0;
        foreach (var (dirIndex, stats) in memo)
            items[w++] = new KeyValuePair<DirId, DirAggregateStats>(dirIndex, stats);

        return SegmentedMap<DirAggregateStats>.Build(items);
    }

    // ---------------------------------------------------------------------
    // Persistence
    // ---------------------------------------------------------------------

    private string GetStateFilePath() => Path.Combine(_dataDirectory, StateFileName);

    private void SaveState()
    {
        var roots = _roots;

        var state = new TreeIndexState { LastIndexedGeneration = _lastIndexedGeneration, Roots = roots };

        var path = GetStateFilePath();

        MemoryPackFile.SaveToFile(path, state);
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

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private void RebuildSingleRootFromSnapshot(RepoSnapshotView repoSnapshot, ScanRootId scanRootId)
    {
        _snapshotView = repoSnapshot;

        if (!repoSnapshot.ScanRoots.TryGetValue(scanRootId, out var scanRoot) || scanRoot.IsDeleted)
        {
            var oldRoots = _roots;
            if (!oldRoots.ContainsKey(scanRootId))
                return;

            var newRoots = new Dictionary<ScanRootId, RootTreeIndexState>(oldRoots.Count - 1);
            foreach (var (k, v) in oldRoots)
                if (k != scanRootId)
                    newRoots[k] = v;

            _roots = newRoots;
            return;
        }

        if (!repoSnapshot.Snapshots.TryGetValue(scanRootId, out var snapshot))
            return;

        var globalHashCounts = ComputeGlobalHashCounts(repoSnapshot);
        var rebuiltRoot = BuildRootState(snapshot, globalHashCounts);

        var roots = _roots;
        var newRootsMap =
            new Dictionary<ScanRootId, RootTreeIndexState>(roots.Count + (roots.ContainsKey(scanRootId) ? 0 : 1));

        foreach (var (k, v) in roots)
            newRootsMap[k] = v;

        newRootsMap[scanRootId] = rebuiltRoot;
        _roots = newRootsMap;
    }

    private static Dictionary<HashKey, int> ComputeGlobalHashCounts(RepoSnapshotView repoSnapshot)
    {
        var liveRootIds = new HashSet<ScanRootId>(
            repoSnapshot.ScanRoots.Values.Where(r => !r.IsDeleted).Select(r => r.RootId));

        var globalHashCounts = new Dictionary<HashKey, int>(capacity: 1024);

        foreach (var (rootId, snap) in repoSnapshot.Snapshots)
        {
            if (!liveRootIds.Contains(rootId))
                continue;

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

        return globalHashCounts;
    }

    private static RootTreeIndexState BuildRootState(
        ScanRootSnapshotView snapshot,
        Dictionary<HashKey, int> globalHashCounts)
    {
        var rootId = snapshot.ScanRootId;

        var dirIdToIndex = new Dictionary<long, int>(capacity: snapshot.Dirs.Count);
        var rootDirIndices = new List<int>();

        for (int i = 0; i < snapshot.Dirs.Count; i++)
        {
            var dir = snapshot.Dirs[i];

            if (dir.Status is ScanEntryStatus.Deleted)
                continue;

            dirIdToIndex[dir.DirId] = i;

            if (dir.ParentDirId < 0)
                rootDirIndices.Add(i);
        }

        var childrenDirsTmp = new Dictionary<int, List<DirHandle>>();
        var childrenFilesTmp = new Dictionary<int, List<FileHandle>>();

        for (int i = 0; i < snapshot.Dirs.Count; i++)
        {
            var dir = snapshot.Dirs[i];

            if (dir.Status is ScanEntryStatus.Deleted)
                continue;

            if (dir.ParentDirId < 0)
                continue;

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

        var (childDirsPool, childDirSlices) = BuildPoolAndSlices(childrenDirsTmp);
        var (childFilesPool, childFileSlices) = BuildPoolAndSlices(childrenFilesTmp);

        var childDirSliceMap = childDirSlices.Length == 0
            ? SegmentedMap<Slice>.Empty
            : SegmentedMap<Slice>.Build(childDirSlices, gapThreshold: SegmentGapThreshold);

        var childFileSliceMap = childFileSlices.Length == 0
            ? SegmentedMap<Slice>.Empty
            : SegmentedMap<Slice>.Build(childFileSlices, gapThreshold: SegmentGapThreshold);

        var statsMap = ComputeRootDirStats(
            scanRootId: rootId,
            snapshot: snapshot,
            childDirsPool: childDirsPool,
            childDirSliceByDirIndex: childDirSliceMap,
            childFilesPool: childFilesPool,
            childFileSliceByDirIndex: childFileSliceMap,
            rootDirIndices: rootDirIndices,
            globalHashCounts: globalHashCounts);

        var (subtreeRangeMap, dirPreorderByFileIndex) = BuildPreorderData(
            snapshot: snapshot,
            rootDirIndices: rootDirIndices,
            childrenDirsTmp: childrenDirsTmp,
            dirIdToIndex: dirIdToIndex);

        return new RootTreeIndexState
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

    private void ApplyFileDeleteToRoot(ScanRootSnapshotView snapshot, FileHandle fileHandle)
    {
        var oldRoots = _roots;
        if (!oldRoots.TryGetValue(fileHandle.ScanRootId, out var oldRoot))
            return;

        if ((uint)fileHandle.Index >= (uint)snapshot.Files.Count)
            return;

        var file = snapshot.Files[fileHandle.Index];
        var dirIdToIndex = BuildDirIdToIndex(snapshot);

        if (!dirIdToIndex.TryGetValue(file.DirId, out var parentDirIndex))
            return;

        var removedFilesByParent = new Dictionary<int, HashSet<FileHandle>>
        {
            [parentDirIndex] = [fileHandle]
        };

        var (newChildFilesPool, newChildFileSlices) = PatchPoolAndSlices(
            oldRoot.ChildFilesPool,
            oldRoot.ChildFileSliceByDirIndex,
            removedParentDirIndices: [],
            removedItemsByParent: removedFilesByParent);

        var newDirPreorderByFileIndex = PatchDirPreorderByFileIndex(
            oldRoot.DirPreorderByFileIndex,
            [fileHandle]);

        // Duplicate stats intentionally not updated exactly here yet.
        var newStats = PatchStatsForFileDelete(
            oldRoot.StatsByDirIndex,
            snapshot,
            parentDirIndex,
            file.Size);

        var newRoot = new RootTreeIndexState
        {
            ChildDirsPool = oldRoot.ChildDirsPool,
            ChildFilesPool = newChildFilesPool,
            ChildDirSliceByDirIndex = oldRoot.ChildDirSliceByDirIndex,
            ChildFileSliceByDirIndex = newChildFileSlices,
            StatsByDirIndex = newStats,
            SubtreeRangeByDirIndex = oldRoot.SubtreeRangeByDirIndex,
            DirPreorderByFileIndex = newDirPreorderByFileIndex
        };

        var newRoots = new Dictionary<ScanRootId, RootTreeIndexState>(oldRoots);
        newRoots[fileHandle.ScanRootId] = newRoot;
        _roots = newRoots;
    }


    private void ApplyDirDeleteToRoot(
        ScanRootSnapshotView snapshot,
        DirHandle deletedRoot,
        ReadOnlySpan<FileHandle> deletedFiles,
        ReadOnlySpan<DirId> deletedDirIds)
    {
        var oldRoots = _roots;
        if (!oldRoots.TryGetValue(deletedRoot.ScanRootId, out var oldRoot))
            return;

        var dirIdToIndex = BuildDirIdToIndex(snapshot);

        var removedDirIndices = new HashSet<int>();
        for (var i = 0; i < deletedDirIds.Length; i++)
        {
            if (dirIdToIndex.TryGetValue(deletedDirIds[i], out var dirIndex))
                removedDirIndices.Add(dirIndex);
        }

        if (removedDirIndices.Count == 0)
            return;

        // Remove the deleted subtree root from its surviving parent child-dir slice, if any.
        var removedDirsByParent = new Dictionary<int, HashSet<DirHandle>>();
        if ((uint)deletedRoot.Index < (uint)snapshot.Dirs.Count)
        {
            var deletedRootRecord = snapshot.Dirs[deletedRoot.Index];
            if (deletedRootRecord.ParentDirId >= 0 &&
                dirIdToIndex.TryGetValue(deletedRootRecord.ParentDirId, out var parentDirIndex))
            {
                removedDirsByParent[parentDirIndex] = [deletedRoot];
            }
        }

        var (newChildDirsPool, newChildDirSlices) = PatchPoolAndSlices(
            oldRoot.ChildDirsPool,
            oldRoot.ChildDirSliceByDirIndex,
            removedParentDirIndices: removedDirIndices,
            removedItemsByParent: removedDirsByParent);

        // For files, whole deleted-dir slices disappear entirely.
        var (newChildFilesPool, newChildFileSlices) = PatchPoolAndSlices(
            oldRoot.ChildFilesPool,
            oldRoot.ChildFileSliceByDirIndex,
            removedParentDirIndices: removedDirIndices,
            removedItemsByParent: null);

        var newSubtreeRanges = RemoveRanges(oldRoot.SubtreeRangeByDirIndex, removedDirIndices);

        var newDirPreorderByFileIndex = PatchDirPreorderByFileIndex(
            oldRoot.DirPreorderByFileIndex,
            deletedFiles);

        var deletedRootStats = oldRoot.StatsByDirIndex.TryGetValue(deletedRoot.Index, out var subtreeStats)
            ? subtreeStats
            : default;

        var newStats = PatchStatsForDirDelete(
            oldRoot.StatsByDirIndex,
            snapshot,
            deletedRoot.Index,
            removedDirIndices,
            deletedRootStats);

        var newRoot = new RootTreeIndexState
        {
            ChildDirsPool = newChildDirsPool,
            ChildFilesPool = newChildFilesPool,
            ChildDirSliceByDirIndex = newChildDirSlices,
            ChildFileSliceByDirIndex = newChildFileSlices,
            StatsByDirIndex = newStats,
            SubtreeRangeByDirIndex = newSubtreeRanges,
            DirPreorderByFileIndex = newDirPreorderByFileIndex
        };

        var newRoots = new Dictionary<ScanRootId, RootTreeIndexState>(oldRoots);
        newRoots[deletedRoot.ScanRootId] = newRoot;
        _roots = newRoots;
    }

    private static Dictionary<long, int> BuildDirIdToIndex(ScanRootSnapshotView snapshot)
    {
        var map = new Dictionary<long, int>(snapshot.Dirs.Count);

        for (var i = 0; i < snapshot.Dirs.Count; i++)
        {
            var dir = snapshot.Dirs[i];
            if (dir.Status is ScanEntryStatus.None)
                continue;

            map[dir.DirId] = i;
        }

        return map;
    }

    private static (THandle[] Pool, SegmentedMap<Slice> SliceMap) PatchPoolAndSlices<THandle>(
        THandle[] oldPool,
        SegmentedMap<Slice> oldSlices,
        HashSet<int> removedParentDirIndices,
        Dictionary<int, HashSet<THandle>>? removedItemsByParent)
        where THandle : unmanaged
    {
        if (oldSlices.SegmentCount == 0)
            return ([], SegmentedMap<Slice>.Empty);

        var newPool = new List<THandle>(oldPool.Length);
        var sliceItems = new List<KeyValuePair<int, Slice>>();

        foreach (var (dirIndex, slice) in oldSlices.Enumerate())
        {
            if (removedParentDirIndices.Contains(dirIndex))
                continue;

            var start = newPool.Count;

            if (!slice.IsEmpty)
            {
                var span = oldPool.AsSpan(slice.Offset, slice.Length);

                if (removedItemsByParent is not null &&
                    removedItemsByParent.TryGetValue(dirIndex, out var removedSet) &&
                    removedSet.Count > 0)
                {
                    for (var i = 0; i < span.Length; i++)
                    {
                        if (!removedSet.Contains(span[i]))
                            newPool.Add(span[i]);
                    }
                }
                else
                {
                    for (var i = 0; i < span.Length; i++)
                        newPool.Add(span[i]);
                }
            }

            var len = newPool.Count - start;
            if (len > 0)
                sliceItems.Add(new KeyValuePair<int, Slice>(dirIndex, new Slice(start, len)));
        }

        return
        (
            newPool.ToArray(),
            sliceItems.Count == 0
                ? SegmentedMap<Slice>.Empty
                : SegmentedMap<Slice>.Build(sliceItems.ToArray(), gapThreshold: SegmentGapThreshold)
        );
    }

    private static SegmentedMap<SubtreeRange> RemoveRanges(
        SegmentedMap<SubtreeRange> oldRanges,
        HashSet<int> removedDirIndices)
    {
        if (oldRanges.SegmentCount == 0)
            return SegmentedMap<SubtreeRange>.Empty;

        var items = new List<KeyValuePair<int, SubtreeRange>>();

        foreach (var (dirIndex, range) in oldRanges.Enumerate())
        {
            if (!removedDirIndices.Contains(dirIndex))
                items.Add(new KeyValuePair<int, SubtreeRange>(dirIndex, range));
        }

        return items.Count == 0
            ? SegmentedMap<SubtreeRange>.Empty
            : SegmentedMap<SubtreeRange>.Build(items.ToArray(), gapThreshold: SegmentGapThreshold);
    }

    private static int[] PatchDirPreorderByFileIndex(int[] oldMap, ReadOnlySpan<FileHandle> deletedFiles)
    {
        var clone = (int[])oldMap.Clone();

        for (var i = 0; i < deletedFiles.Length; i++)
        {
            var fileIndex = deletedFiles[i].Index;
            if ((uint)fileIndex < (uint)clone.Length)
                clone[fileIndex] = -1;
        }

        return clone;
    }

    private static SegmentedMap<DirAggregateStats> PatchStatsForFileDelete(
        SegmentedMap<DirAggregateStats> oldStats,
        ScanRootSnapshotView snapshot,
        int parentDirIndex,
        long deletedFileSize)
    {
        var statsDict = oldStats.Enumerate().ToDictionary(kv => kv.Key, kv => kv.Value);

        var current = parentDirIndex;
        while ((uint)current < (uint)snapshot.Dirs.Count && current >= 0)
        {
            if (statsDict.TryGetValue(current, out var stats))
            {
                statsDict[current] = stats with
                {
                    FileCount = Math.Max(0, stats.FileCount - 1),
                    TotalBytes = Math.Max(0, stats.TotalBytes - deletedFileSize)

                    // DuplicateFiles / DuplicateBytes intentionally left unchanged for file delete
                    // until exact global duplicate mutation is implemented.
                };
            }

            var parentDirId = snapshot.Dirs[current].ParentDirId;
            current = parentDirId >= 0
                ? TryGetDirIndexByDirId(snapshot, parentDirId)
                : -1;
        }

        return statsDict.Count == 0
            ? SegmentedMap<DirAggregateStats>.Empty
            : SegmentedMap<DirAggregateStats>.Build(statsDict.ToArray(), gapThreshold: SegmentGapThreshold);
    }

    private static SegmentedMap<DirAggregateStats> PatchStatsForDirDelete(
    SegmentedMap<DirAggregateStats> oldStats,
    ScanRootSnapshotView snapshot,
    int deletedRootIndex,
    HashSet<int> removedDirIndices,
    DirAggregateStats deletedRootStats)
    {
        var statsDict = new Dictionary<int, DirAggregateStats>();

        foreach (var (dirIndex, stats) in oldStats.Enumerate())
        {
            if (!removedDirIndices.Contains(dirIndex))
                statsDict[dirIndex] = stats;
        }

        if ((uint)deletedRootIndex >= (uint)snapshot.Dirs.Count)
        {
            return statsDict.Count == 0
                ? SegmentedMap<DirAggregateStats>.Empty
                : SegmentedMap<DirAggregateStats>.Build(statsDict.ToArray(), gapThreshold: SegmentGapThreshold);
        }

        var deletedRoot = snapshot.Dirs[deletedRootIndex];
        if (deletedRoot.ParentDirId < 0)
        {
            return statsDict.Count == 0
                ? SegmentedMap<DirAggregateStats>.Empty
                : SegmentedMap<DirAggregateStats>.Build(statsDict.ToArray(), gapThreshold: SegmentGapThreshold);
        }

        var current = TryGetDirIndexByDirId(snapshot, deletedRoot.ParentDirId);

        var removedDirCount = deletedRootStats.DirCount + 1;
        var removedFileCount = deletedRootStats.FileCount;
        var removedBytes = deletedRootStats.TotalBytes;
        var removedDuplicateFiles = deletedRootStats.DuplicateFiles;
        var removedDuplicateBytes = deletedRootStats.DuplicateBytes;

        while ((uint)current < (uint)snapshot.Dirs.Count && current >= 0)
        {
            if (statsDict.TryGetValue(current, out var stats))
            {
                statsDict[current] = stats with
                {
                    DirCount = Math.Max(0, stats.DirCount - removedDirCount),
                    FileCount = Math.Max(0, stats.FileCount - removedFileCount),
                    TotalBytes = Math.Max(0, stats.TotalBytes - removedBytes),

                    // Partial duplicate handling: subtract the deleted subtree contribution.
                    // This is not globally exact yet when hash counts cross 2->1 elsewhere.
                    DuplicateFiles = Math.Max(0, stats.DuplicateFiles - removedDuplicateFiles),
                    DuplicateBytes = Math.Max(0, stats.DuplicateBytes - removedDuplicateBytes),
                };
            }

            var parentDirId = snapshot.Dirs[current].ParentDirId;
            current = parentDirId >= 0
                ? TryGetDirIndexByDirId(snapshot, parentDirId)
                : -1;
        }

        return statsDict.Count == 0
            ? SegmentedMap<DirAggregateStats>.Empty
            : SegmentedMap<DirAggregateStats>.Build(statsDict.ToArray(), gapThreshold: SegmentGapThreshold);
    }

    private static int TryGetDirIndexByDirId(ScanRootSnapshotView snapshot, long dirId)
    {
        for (var i = 0; i < snapshot.Dirs.Count; i++)
        {
            if (snapshot.Dirs[i].DirId == dirId)
                return i;
        }

        return -1;
    }
}
