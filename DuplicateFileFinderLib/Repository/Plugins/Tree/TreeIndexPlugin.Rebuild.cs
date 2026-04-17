using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Tree;

public sealed partial class TreeIndexPlugin
{
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

    private static SegmentedMap<DirAggregateStats> ComputeRootDirStats(
       ScanRootId scanRootId,
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
}
