using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Tree;

public sealed partial class TreeIndexPlugin
{
    private sealed record RootMutationContext(
        ScanRootSnapshotView Snapshot,
        RootTreeIndexState OldRoot,
        Dictionary<long, int> DirIdToIndex);

    private bool TryCreateRootMutationContext(
        ScanRootId scanRootId,
        ScanRootSnapshotView snapshot,
        out RootMutationContext context)
    {
        context = default!;

        var oldRoots = _roots;
        if (!oldRoots.TryGetValue(scanRootId, out var oldRoot))
            return false;

        context = new RootMutationContext(
            Snapshot: snapshot,
            OldRoot: oldRoot,
            DirIdToIndex: BuildDirIdToIndex(snapshot));

        return true;
    }

    private void PublishMutatedRoot(ScanRootId scanRootId, RootTreeIndexState newRoot)
    {
        var oldRoots = _roots;
        var newRoots = new Dictionary<ScanRootId, RootTreeIndexState>(oldRoots)
        {
            [scanRootId] = newRoot
        };

        _roots = newRoots;
    }

    private void ApplyFileDeleteToRoot(ScanRootSnapshotView snapshot, FileHandle fileHandle)
    {
        if (!TryCreateRootMutationContext(fileHandle.ScanRootId, snapshot, out var ctx))
            return;

        if ((uint)fileHandle.Index >= (uint)ctx.Snapshot.Files.Count)
            return;

        var file = ctx.Snapshot.Files[fileHandle.Index];

        if (!ctx.DirIdToIndex.TryGetValue(file.DirId, out var parentDirIndex))
            return;

        var removedFilesByParent = new Dictionary<int, HashSet<FileHandle>>
        {
            [parentDirIndex] = [fileHandle]
        };

        var (newChildFilesPool, newChildFileSlices) = PatchPoolAndSlices(
            ctx.OldRoot.ChildFilesPool,
            ctx.OldRoot.ChildFileSliceByDirIndex,
            removedParentDirIndices: [],
            removedItemsByParent: removedFilesByParent);

        var newDirPreorderByFileIndex = PatchDirPreorderByFileIndex(
            ctx.OldRoot.DirPreorderByFileIndex,
            [fileHandle]);

        // Duplicate stats intentionally not updated exactly here yet.
        var newStats = PatchStatsForFileDelete(
            ctx.OldRoot.StatsByDirIndex,
            ctx.Snapshot,
            parentDirIndex,
            file.Size);

        var newRoot = new RootTreeIndexState
        {
            ChildDirsPool = ctx.OldRoot.ChildDirsPool,
            ChildFilesPool = newChildFilesPool,
            ChildDirSliceByDirIndex = ctx.OldRoot.ChildDirSliceByDirIndex,
            ChildFileSliceByDirIndex = newChildFileSlices,
            StatsByDirIndex = newStats,
            SubtreeRangeByDirIndex = ctx.OldRoot.SubtreeRangeByDirIndex,
            DirPreorderByFileIndex = newDirPreorderByFileIndex
        };

        PublishMutatedRoot(fileHandle.ScanRootId, newRoot);
    }

    private void ApplyDirDeleteToRoot(
        ScanRootSnapshotView snapshot,
        DirHandle deletedRoot,
        ReadOnlySpan<FileHandle> deletedFiles,
        ReadOnlySpan<DirId> deletedDirIds)
    {
        if (!TryCreateRootMutationContext(deletedRoot.ScanRootId, snapshot, out var ctx))
            return;

        var removedDirIndices = new HashSet<int>();
        for (var i = 0; i < deletedDirIds.Length; i++)
        {
            if (ctx.DirIdToIndex.TryGetValue(deletedDirIds[i], out var dirIndex))
                removedDirIndices.Add(dirIndex);
        }

        if (removedDirIndices.Count == 0)
            return;

        var removedDirsByParent = BuildRemovedDirsByParent(ctx, deletedRoot);

        var (newChildDirsPool, newChildDirSlices) = PatchPoolAndSlices(
            ctx.OldRoot.ChildDirsPool,
            ctx.OldRoot.ChildDirSliceByDirIndex,
            removedParentDirIndices: removedDirIndices,
            removedItemsByParent: removedDirsByParent);

        // For files, whole deleted-dir slices disappear entirely.
        var (newChildFilesPool, newChildFileSlices) = PatchPoolAndSlices(
            ctx.OldRoot.ChildFilesPool,
            ctx.OldRoot.ChildFileSliceByDirIndex,
            removedParentDirIndices: removedDirIndices,
            removedItemsByParent: null);

        var newSubtreeRanges = RemoveRanges(ctx.OldRoot.SubtreeRangeByDirIndex, removedDirIndices);

        var newDirPreorderByFileIndex = PatchDirPreorderByFileIndex(
            ctx.OldRoot.DirPreorderByFileIndex,
            deletedFiles);

        var deletedRootStats = ctx.OldRoot.StatsByDirIndex.TryGetValue(deletedRoot.Index, out var subtreeStats)
            ? subtreeStats
            : default;

        var newStats = PatchStatsForDirDelete(
            ctx.OldRoot.StatsByDirIndex,
            ctx.Snapshot,
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

        PublishMutatedRoot(deletedRoot.ScanRootId, newRoot);
    }

    private static Dictionary<int, HashSet<DirHandle>> BuildRemovedDirsByParent(
        RootMutationContext ctx,
        DirHandle deletedRoot)
    {
        var removedDirsByParent = new Dictionary<int, HashSet<DirHandle>>();

        if ((uint)deletedRoot.Index >= (uint)ctx.Snapshot.Dirs.Count)
            return removedDirsByParent;

        var deletedRootRecord = ctx.Snapshot.Dirs[deletedRoot.Index];
        if (deletedRootRecord.ParentDirId < 0)
            return removedDirsByParent;

        if (!ctx.DirIdToIndex.TryGetValue(deletedRootRecord.ParentDirId, out var parentDirIndex))
            return removedDirsByParent;

        removedDirsByParent[parentDirIndex] = [deletedRoot];
        return removedDirsByParent;
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
