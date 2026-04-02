// DuplicateFileFinderLib/Repository/Plugins/HashIndexPlugin.cs

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Plugins;

public sealed class HashIndexPlugin : ChannelRepoPlugin, IHashIndexReadModel
{
    private const string StateFileName = "hash-index.bin";
    private readonly string _dataDirectory;

    // Published: concatenation of all group file handles
    private volatile FileHandle[] _allFiles = [];

    // Published: dense descriptors (unsorted)
    private volatile HashGroupDescriptor[] _groups = [];

    // Published: sorted “views” as indices into _groups[]
    private volatile int[] _bySizeDesc = [];
    private volatile int[] _byCountDesc = [];

    // Published stats snapshot
    private volatile StatsSnapshot _stats = StatsSnapshot.Empty;

    private long _lastIndexedGeneration;

    // Needed for filtering duplicate files within a subtree
    private readonly ITreeIndexReadModel _treeIndex;

    public HashIndexPlugin(string dataDirectory, ITreeIndexReadModel treeIndex)
        : base(4096)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory must be non-empty.", nameof(dataDirectory));

        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(_dataDirectory);

        _treeIndex = treeIndex ?? throw new ArgumentNullException(nameof(treeIndex));
    }

    public int TotalDuplicateFileCount => _stats.DuplicateFileCount;
    public long TotalSpaceTakenByDuplicates => _stats.SpaceTakenByDuplicates;

    // ---------------------------------------------------------------------
    // IHashIndexReadModel
    // ---------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long TotalBytes(in HashGroupDescriptor d) => d.FileSizeBytes * d.Count;

    public ReadOnlySpan<FileHandle> GetGroupFiles(in HashGroupDescriptor group)
    {
        var all = _allFiles;

        if (group.Count <= 0)
            return ReadOnlySpan<FileHandle>.Empty;

        if (group.Offset < 0)
            return ReadOnlySpan<FileHandle>.Empty;

        var end = group.Offset + group.Count;
        if ((uint)end > (uint)all.Length)
            return ReadOnlySpan<FileHandle>.Empty;

        return all.AsSpan(group.Offset, group.Count);
    }

    public DuplicateGroupPage GetGroupsPage(in DuplicateQuery query, int offset, int count)
        => GetGroupsPageCore(in query, offset, count, hasFilter: false, default);

    public DuplicateGroupPage GetGroupsPage(in DuplicateQuery query, in SubtreeFilter filter, int offset, int count)
        => GetGroupsPageCore(in query, offset, count, hasFilter: true, in filter);

    private DuplicateGroupPage GetGroupsPageCore(
        in DuplicateQuery query,
        int offset,
        int count,
        bool hasFilter,
        in SubtreeFilter filter)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (query.MinDuplicates < 2) throw new ArgumentOutOfRangeException(nameof(query.MinDuplicates));
        if (query.MinSize < 1) throw new ArgumentOutOfRangeException(nameof(query.MinSize));

        if (hasFilter && (!filter.RootDir.IsValid || filter.Range.IsEmpty))
            return new DuplicateGroupPage(offset, 0, ReadOnlyMemory<HashGroupDescriptor>.Empty);

        var groups = _groups;
        if (groups.Length == 0)
            return new DuplicateGroupPage(offset, 0, ReadOnlyMemory<HashGroupDescriptor>.Empty);

        var order = query.Sort == DuplicateSort.TotalSizeDesc ? _bySizeDesc : _byCountDesc;
        if (order.Length == 0)
            return new DuplicateGroupPage(offset, 0, ReadOnlyMemory<HashGroupDescriptor>.Empty);

        var minDup = query.MinDuplicates;
        var minSize = query.MinSize;
        var sort = query.Sort;

        var scanRootId = hasFilter ? filter.RootDir.ScanRootId : -1;
        var range = hasFilter ? filter.Range : default;

        var all = _allFiles;

        var page = new HashGroupDescriptor[count];
        var w = 0;
        var seen = 0;

        for (var i = 0; i < order.Length; i++)
        {
            var d = groups[order[i]];

            // Early exit only makes sense in the sorted dimension.
            if (d.Count < minDup)
            {
                if (sort == DuplicateSort.DuplicateCountDesc)
                    break;
                continue;
            }

            if (TotalBytes(d) < minSize)
            {
                if (sort == DuplicateSort.TotalSizeDesc)
                    break;
                continue;
            }

            if (hasFilter && !GroupIntersectsSubtree(all, d, scanRootId, range))
                continue;

            if (seen < offset)
            {
                seen++;
                continue;
            }

            page[w++] = d;

            if (w == count)
                break;
        }

        if (w == 0)
            return new DuplicateGroupPage(offset, 0, ReadOnlyMemory<HashGroupDescriptor>.Empty);

        if (w != page.Length)
            Array.Resize(ref page, w);

        return new DuplicateGroupPage(offset, w, page);
    }

    private bool GroupIntersectsSubtree(
        FileHandle[] all,
        in HashGroupDescriptor group,
        long scanRootId,
        SubtreeRange range)
    {
        if (group.Count <= 0 || group.Offset < 0)
            return false;

        var end = group.Offset + group.Count;
        if ((uint)end > (uint)all.Length)
            return false;

        for (var i = group.Offset; i < end; i++)
        {
            var fh = all[i];

            if (fh.ScanRootId != scanRootId)
                continue;

            if (_treeIndex.TryGetFileDirPreorder(fh, out var pre) && range.Contains(pre))
                return true;
        }

        return false;
    }

    // ---------------------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------------------

    protected override ValueTask OnBootstrapEventAsync(BootstrapEvent evt, CancellationToken ct)
    {
        if (TryLoadState(evt.Generation))
        {
            _lastIndexedGeneration = evt.Generation;
            return ValueTask.CompletedTask;
        }

        RebuildAndCommit(evt.Generation, () => RebuildFromSnapshot(evt.RepoSnapshotView));

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnScanRootSnapshotReplacedEventAsync(
        ScanRootSnapshotReplacedEvent evt,
        CancellationToken ct)
    {
        // Ignore stale/out-of-order events (channel may drop old items).
        if (evt.Generation <= _lastIndexedGeneration)
            return ValueTask.CompletedTask;

        RebuildAndCommit(evt.Generation, () => RebuildFromSnapshot(evt.RepoSnapshotView));
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnRepoScanRootRemovedEventAsync(
        RepoScanRootRemovedEvent evt,
        CancellationToken ct)
    {
        if (evt.Generation <= _lastIndexedGeneration)
            return ValueTask.CompletedTask;

        RebuildAndCommit(evt.Generation, () => RebuildExcludingScanRoot(evt.ScanRootId));
        return ValueTask.CompletedTask;
    }

    private void RebuildAndCommit(long generation, Action rebuild)
    {
        rebuild();
        _lastIndexedGeneration = generation;
        SaveState();
    }

    // ---------------------------------------------------------------------
    // Rebuild: fast path from snapshot (no per-group lists)
    // ---------------------------------------------------------------------

    private struct GroupMeta
    {
        public int Count;
        public long FileSizeBytes;
        public FileHandle FirstFile;

        public int Offset;
        public int Cursor;
    }

    private static IEnumerable<(ScanRootSnapshotView snapshot, int index, FileRecordV2 file)> EnumerateEligibleFiles(
        RepoSnapshotView repoSnapshot)
    {
        foreach (var scanRoot in repoSnapshot.ScanRoots.Values)
        {
            if (scanRoot.IsDeleted)
                continue;

            var snapshot = repoSnapshot.Snapshots[scanRoot.RootId];

            for (var i = 0; i < snapshot.Files.Count; i++)
            {
                var file = snapshot.Files[i];

                if (file.Size <= 0)
                    continue;

                if (file.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                    continue;

                if (file.Hash == HashKey.NotComputed || file.Hash == HashKey.CannotCompute)
                    continue;

                yield return (snapshot, i, file);
            }
        }
    }

    private void RebuildFromSnapshot(RepoSnapshotView repoSnapshot)
    {
        using var _ = TimingLog.StartPhase("HashIndex.Rebuild");

        // Pass 1: count per hash + record per-file size (no per-group allocations)
        var metaByHash = new Dictionary<HashKey, GroupMeta>(capacity: 1024);
        var totalHandles = 0;

        foreach (var (snapshot, index, file) in EnumerateEligibleFiles(repoSnapshot))
        {
            totalHandles++;

            ref var meta = ref CollectionsMarshal.GetValueRefOrAddDefault(metaByHash, file.Hash, out var exists);

            if (!exists)
            {
                meta = new GroupMeta
                {
                    Count = 1,
                    FileSizeBytes = file.Size,
                    FirstFile = new FileHandle(snapshot.ScanRootId, index),
                    Offset = 0,
                    Cursor = 0
                };
                continue;
            }

            meta.Count++;
        }

        if (metaByHash.Count == 0 || totalHandles == 0)
        {
            PublishEmpty();
            return;
        }

        // Pass 2: assign offsets + build descriptors
        var groups = new HashGroupDescriptor[metaByHash.Count];
        var allFiles = new FileHandle[totalHandles];

        var offset = 0;
        var gi = 0;

        foreach (var (hash, meta0) in metaByHash)
        {
            var meta = meta0;
            meta.Offset = offset;
            meta.Cursor = 0;
            metaByHash[hash] = meta;

            groups[gi++] = new HashGroupDescriptor(
                Hash: hash,
                FileSizeBytes: meta.FileSizeBytes,
                Offset: offset,
                Count: meta.Count,
                FirstFile: meta.FirstFile);

            offset += meta.Count;
        }

        // Pass 3: fill allFiles using per-hash cursors (same eligibility rules as pass 1)
        foreach (var (snapshot, index, file) in EnumerateEligibleFiles(repoSnapshot))
        {
            if (!metaByHash.TryGetValue(file.Hash, out var meta))
                continue;

            var writeIndex = meta.Offset + meta.Cursor;
            meta.Cursor++;
            metaByHash[file.Hash] = meta;

            allFiles[writeIndex] = new FileHandle(snapshot.ScanRootId, index);
        }

        PublishComputed(allFiles, groups);
    }

    // ---------------------------------------------------------------------
    // Rebuild: exclusion of a scan-root from current index (3-pass compaction)
    // ---------------------------------------------------------------------

    private readonly record struct RemovalPlan(int[] Counts, FileHandle[] Reps, int NewGroupCount, int NewTotalHandles);

    private void RebuildExcludingScanRoot(long removedScanRootId)
    {
        using var _ = TimingLog.StartPhase("HashIndex.RebuildExcludingScanRoot");

        var oldGroups = _groups;
        var oldAll = _allFiles;

        if (oldGroups.Length == 0 || oldAll.Length == 0)
        {
            PublishEmpty();
            return;
        }

        // Pass 1: build plan (counts + representatives + totals)
        var plan = BuildRemovalPlan(oldGroups, oldAll, removedScanRootId);

        if (plan.NewGroupCount == 0 || plan.NewTotalHandles == 0)
        {
            PublishEmpty();
            return;
        }

        // Pass 2: allocate outputs + build newGroups with offsets (compacting away empty groups)
        var newAll = new FileHandle[plan.NewTotalHandles];
        var newGroups = new HashGroupDescriptor[plan.NewGroupCount];

        BuildGroupsFromPlan(oldGroups, plan, newGroups);

        // Pass 3: fill newAll by copying survivors into each group segment
        FillAllFilesFromPlan(oldGroups, oldAll, plan, removedScanRootId, newAll);

        PublishComputed(newAll, newGroups);
    }

    private static RemovalPlan BuildRemovalPlan(
        HashGroupDescriptor[] oldGroups,
        FileHandle[] oldAll,
        long removedScanRootId)
    {
        var newCounts = new int[oldGroups.Length];
        var newReps = new FileHandle[oldGroups.Length];

        var newTotalHandles = 0;
        var newGroupCount = 0;

        for (var g = 0; g < oldGroups.Length; g++)
        {
            var d = oldGroups[g];

            if (d.Count <= 0 || d.Offset < 0)
                continue;

            var end = d.Offset + d.Count;
            if ((uint)end > (uint)oldAll.Length)
                continue;

            var count = 0;
            var rep = FileHandle.Invalid;

            for (var i = d.Offset; i < end; i++)
            {
                var fh = oldAll[i];
                if (fh.ScanRootId == removedScanRootId)
                    continue;

                if (!rep.IsValid)
                    rep = fh;

                count++;
            }

            if (count <= 0)
                continue;

            newCounts[g] = count;
            newReps[g] = rep;

            newTotalHandles += count;
            newGroupCount++;
        }

        return new RemovalPlan(newCounts, newReps, newGroupCount, newTotalHandles);
    }

    private static void BuildGroupsFromPlan(
        HashGroupDescriptor[] oldGroups,
        RemovalPlan plan,
        HashGroupDescriptor[] newGroups)
    {
        var wGroup = 0;
        var wOffset = 0;

        for (var g = 0; g < oldGroups.Length; g++)
        {
            var newCount = plan.Counts[g];
            if (newCount <= 0)
                continue;

            var old = oldGroups[g];

            newGroups[wGroup] = new HashGroupDescriptor(
                Hash: old.Hash,
                FileSizeBytes: old.FileSizeBytes,
                Offset: wOffset,
                Count: newCount,
                FirstFile: plan.Reps[g]);

            wOffset += newCount;
            wGroup++;
        }
    }

    private static void FillAllFilesFromPlan(
        HashGroupDescriptor[] oldGroups,
        FileHandle[] oldAll,
        RemovalPlan plan,
        long removedScanRootId,
        FileHandle[] newAll)
    {
        var dstOffset = 0;

        for (var g = 0; g < oldGroups.Length; g++)
        {
            var newCount = plan.Counts[g];
            if (newCount <= 0)
                continue;

            var d = oldGroups[g];

            // Bounds already validated in the plan, so no repeated checks here.
            var end = d.Offset + d.Count;

            var wrote = 0;
            for (var i = d.Offset; i < end; i++)
            {
                var fh = oldAll[i];
                if (fh.ScanRootId == removedScanRootId)
                    continue;

                newAll[dstOffset + wrote] = fh;
                wrote++;

                if (wrote == newCount)
                    break;
            }

            dstOffset += newCount;
        }
    }

    // ---------------------------------------------------------------------
    // Consolidated “finalize + publish”
    // ---------------------------------------------------------------------

    private void PublishEmpty()
        => Publish([], [], [], [], StatsSnapshot.Empty);

    private void PublishComputed(FileHandle[] allFiles, HashGroupDescriptor[] groups)
    {
        var (bySize, byCount) = BuildSortedViews(groups);
        var stats = ComputeStats(groups);
        Publish(allFiles, groups, bySize, byCount, stats);
    }

    private static StatsSnapshot ComputeStats(HashGroupDescriptor[] groups)
    {
        var dupCount = 0;
        long space = 0;

        for (var i = 0; i < groups.Length; i++)
        {
            var g = groups[i];
            if (g.Count <= 1)
                continue;

            dupCount += g.Count - 1;
            space += (g.Count - 1) * g.FileSizeBytes;
        }

        return dupCount == 0 ? StatsSnapshot.Empty : new StatsSnapshot(dupCount, space);
    }

    private static (int[] bySize, int[] byCount) BuildSortedViews(HashGroupDescriptor[] groups)
    {
        var bySize = BuildIndexArray(groups.Length);
        Array.Sort(bySize, new BySizeDescComparer(groups));

        var byCount = BuildIndexArray(groups.Length);
        Array.Sort(byCount, new ByCountDescComparer(groups));

        return (bySize, byCount);
    }

    private void Publish(
        FileHandle[] allFiles,
        HashGroupDescriptor[] groups,
        int[] bySize,
        int[] byCount,
        StatsSnapshot stats)
    {
        _allFiles = allFiles;
        _groups = groups;
        _bySizeDesc = bySize;
        _byCountDesc = byCount;
        _stats = stats;
    }

    private static int[] BuildIndexArray(int n)
    {
        if (n == 0) return [];
        var arr = new int[n];
        for (var i = 0; i < n; i++) arr[i] = i;
        return arr;
    }

    private sealed class BySizeDescComparer(HashGroupDescriptor[] groups) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            var a = groups[x];
            var b = groups[y];

            var c = TotalBytes(b).CompareTo(TotalBytes(a));
            if (c != 0) return c;

            c = b.Count.CompareTo(a.Count);
            if (c != 0) return c;

            return x.CompareTo(y);
        }
    }

    private sealed class ByCountDescComparer(HashGroupDescriptor[] groups) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            var a = groups[x];
            var b = groups[y];

            var c = b.Count.CompareTo(a.Count);
            if (c != 0) return c;

            c = TotalBytes(b).CompareTo(TotalBytes(a));
            if (c != 0) return c;

            return x.CompareTo(y);
        }
    }

    // ---------------------------------------------------------------------
    // Persistence
    // ---------------------------------------------------------------------

    private string GetStateFilePath() => Path.Combine(_dataDirectory, StateFileName);

    private void SaveState()
    {
        var state = new HashIndexState
        {
            LastIndexedGeneration = _lastIndexedGeneration,
            TotalDuplicateFileCount = TotalDuplicateFileCount,
            TotalSpaceTakenByDuplicates = TotalSpaceTakenByDuplicates,
            AllFiles = _allFiles,
            Groups = _groups,
            BySizeDesc = _bySizeDesc,
            ByCountDesc = _byCountDesc
        };

        var path = GetStateFilePath();

        MemoryPackFile.SaveToFile(path, state);
    }

    private bool TryLoadState(long expectedGeneration)
    {
        var path = GetStateFilePath();
        if (!File.Exists(path))
            return false;

        if (!MemoryPackFile.TryLoadMapped<HashIndexState>(path, out var state, CancellationToken.None) || state == null)
            return false;

        if (state.LastIndexedGeneration != expectedGeneration)
            return false;

        // Build locals first, then publish once (atomic-ish for readers).
        var allFiles = state.AllFiles;
        var groups = state.Groups;

        if (groups.Length == 0 || allFiles.Length == 0)
        {
            PublishEmpty();
            return true;
        }

        // Prefer persisted views; if missing/invalid, rebuild.
        var bySize = state.BySizeDesc;
        var byCount = state.ByCountDesc;

        if (bySize.Length != groups.Length || byCount.Length != groups.Length)
            (bySize, byCount) = BuildSortedViews(groups);


        var stats = new StatsSnapshot(state.TotalDuplicateFileCount, state.TotalSpaceTakenByDuplicates);

        Publish(allFiles, groups, bySize, byCount, stats);

        // Not part of read-model publication, but keep consistent here too.
        _lastIndexedGeneration = state.LastIndexedGeneration;

        return true;
    }

    // ---------------------------------------------------------------------
    // Internal types
    // ---------------------------------------------------------------------

    private sealed record StatsSnapshot(int DuplicateFileCount, long SpaceTakenByDuplicates)
    {
        public static readonly StatsSnapshot Empty = new(0, 0);
    }
}
