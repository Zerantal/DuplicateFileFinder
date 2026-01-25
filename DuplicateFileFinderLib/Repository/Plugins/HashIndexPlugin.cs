// DuplicateFileFinderLib/Repository/Plugins/HashIndexPlugin.cs

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

using MemoryPack;

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
    private volatile StatsSnapshot _stats = new(0, 0);

    private long _lastIndexedGeneration;

    public HashIndexPlugin(string dataDirectory)
        : base(4096)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory must be non-empty.", nameof(dataDirectory));

        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(_dataDirectory);
    }

    public int TotalDuplicateFileCount => _stats.DuplicateFileCount;
    public long TotalSpaceTakenByDuplicates => _stats.SpaceTakenByDuplicates;

    // ---------------------------------------------------------------------
    // IHashIndexReadModel
    // ---------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long TotalBytes(in HashGroupDescriptor d) => d.SizeBytes * d.Count;

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
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (query.MinDuplicates < 2) throw new ArgumentOutOfRangeException(nameof(query.MinDuplicates));
        if (query.MinSize < 1) throw new ArgumentOutOfRangeException(nameof(query.MinSize));

        var groups = _groups;
        if (groups.Length == 0)
            return new DuplicateGroupPage(0, offset, 0, ReadOnlyMemory<HashGroupDescriptor>.Empty);

        var order = query.Sort == DuplicateSort.TotalSizeDesc ? _bySizeDesc : _byCountDesc;
        if (order.Length == 0)
            return new DuplicateGroupPage(0, offset, 0, ReadOnlyMemory<HashGroupDescriptor>.Empty);

        // Count matches with early-exit for the active sort
        var total = CountMatches(groups, order, query);

        if (total == 0 || offset >= total)
            return new DuplicateGroupPage(total, offset, 0, ReadOnlyMemory<HashGroupDescriptor>.Empty);

        var wanted = Math.Min(count, total - offset);
        var page = new HashGroupDescriptor[wanted];

        var seen = 0;
        var w = 0;

        for (var i = 0; i < order.Length; i++)
        {
            var d = groups[order[i]];

            if (!Matches(d, query))
            {
                if (CanEarlyExit(d, query))
                    break;
                continue;
            }

            if (seen < offset)
            {
                seen++;
                continue;
            }

            page[w++] = d;
            seen++;

            if (w == wanted)
                break;
        }

        return new DuplicateGroupPage(total, offset, w, page);
    }

    private static int CountMatches(HashGroupDescriptor[] groups, int[] order, in DuplicateQuery q)
    {
        var total = 0;

        for (var i = 0; i < order.Length; i++)
        {
            var d = groups[order[i]];

            if (!Matches(d, q))
            {
                if (CanEarlyExit(d, q))
                    break;
                continue;
            }

            total++;
        }

        return total;
    }

    private static bool Matches(in HashGroupDescriptor d, in DuplicateQuery q)
    {
        return d.Count >= q.MinDuplicates && TotalBytes(d) >= q.MinSize;
    }

    private static bool CanEarlyExit(in HashGroupDescriptor d, in DuplicateQuery q)
    {
        return q.Sort switch
        {
            DuplicateSort.TotalSizeDesc => TotalBytes(d) < q.MinSize,
            DuplicateSort.DuplicateCountDesc => d.Count < q.MinDuplicates,
            _ => false
        };
    }

    // ---------------------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------------------

    protected override void OnBootstrapEvent(BootstrapEvent evt)
    {
        if (TryLoadState(evt.Generation))
        {
            _lastIndexedGeneration = evt.Generation;
            return;
        }

        RebuildAndCommit(evt.Generation, () => RebuildFromSnapshot(evt.RepoSnapshotView));
    }

    protected override void OnScanRootSnapshotReplacedEvent(ScanRootSnapshotReplacedEvent evt)
    {
        // Ignore stale/out-of-order events (channel may drop old items).
        if (evt.Generation <= _lastIndexedGeneration)
            return;

        RebuildAndCommit(evt.Generation, () => RebuildFromSnapshot(evt.RepoSnapshotView));
    }

    protected override void OnRepoScanRootRemovedEvent(RepoScanRootRemovedEvent evt)
    {
        // Keep existing behaviour for now (rare path). Still correct, just not optimal.
        if (evt.Generation <= _lastIndexedGeneration)
            return;

        RebuildAndCommit(evt.Generation, () => RebuildExcludingScanRoot(evt.ScanRootId));
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
        public long SizeBytes;
        public FileHandle FirstFile;

        public int Offset;
        public int Cursor;
    }

    private void RebuildFromSnapshot(RepoSnapshotView repoSnapshot)
    {
        using var _ = TimingLog.StartPhase("HashIndex.Rebuild");

        // Pass 1: count per hash + sizeBytes (no per-group allocations)
        var metaByHash = new Dictionary<HashKey, GroupMeta>(capacity: 1024);

        var totalHandles = 0;

        foreach (var scanRoot in repoSnapshot.ScanRoots.Values)
        {
            if (scanRoot.IsDeleted)
                continue;

            var snapshot = repoSnapshot.Snapshots[scanRoot.RootId];

            for (var i = 0; i < snapshot.Files.Count; i++)
            {
                var file = snapshot.Files[i];

                if (file.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                    continue;

                if (file.Hash == HashKey.NotComputed || file.Hash == HashKey.CannotCompute)
                    continue;

                totalHandles++;

                ref var meta = ref CollectionsMarshal.GetValueRefOrAddDefault(metaByHash, file.Hash, out var exists);

                if (!exists)
                {
                    meta = new GroupMeta
                    {
                        Count = 1,
                        SizeBytes = file.Size,
                        FirstFile = new FileHandle(snapshot.ScanRootId, i),
                        Offset = 0,
                        Cursor = 0
                    };
                }
                else
                {
                    meta.Count++;
                }
            }
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

        var totalDupCount = 0;
        long totalSpaceDup = 0;

        foreach (var kv in metaByHash)
        {
            var hash = kv.Key;
            var meta = kv.Value;

            meta.Offset = offset;
            meta.Cursor = 0;
            metaByHash[hash] = meta;

            groups[gi++] = new HashGroupDescriptor(
                Hash: hash,
                SizeBytes: meta.SizeBytes,
                Offset: offset,
                Count: meta.Count,
                FirstFile: meta.FirstFile);

            offset += meta.Count;

            if (meta.Count > 1)
            {
                totalDupCount += meta.Count - 1;
                totalSpaceDup += (meta.Count - 1) * meta.SizeBytes;
            }
        }

        // Pass 3: fill allFiles using per-hash cursors
        foreach (var scanRoot in repoSnapshot.ScanRoots.Values)
        {
            if (scanRoot.IsDeleted)
                continue;

            var snapshot = repoSnapshot.Snapshots[scanRoot.RootId];

            for (var i = 0; i < snapshot.Files.Count; i++)
            {
                var file = snapshot.Files[i];

                if (file.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                    continue;

                if (file.Hash == HashKey.NotComputed || file.Hash == HashKey.CannotCompute)
                    continue;

                if (!metaByHash.TryGetValue(file.Hash, out var meta))
                    continue;

                var writeIndex = meta.Offset + meta.Cursor;
                meta.Cursor++;
                metaByHash[file.Hash] = meta;

                allFiles[writeIndex] = new FileHandle(snapshot.ScanRootId, i);
            }
        }

        var (bySize, byCount) = BuildSortedViews(groups);

        Publish(allFiles, groups, bySize, byCount, new StatsSnapshot(totalDupCount, totalSpaceDup));
    }

    // ---------------------------------------------------------------------
    // Rebuild: allocation-free exclusion of a scan-root from current index
    // ---------------------------------------------------------------------

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

        // Pass 1: compute new counts + new total handle count + how many groups survive.
        var newTotalHandles = 0;
        var newGroupCount = 0;

        // Per-old-group newCount and representative handle.
        var newCounts = new int[oldGroups.Length];
        var newReps = new FileHandle[oldGroups.Length];

        for (var g = 0; g < oldGroups.Length; g++)
        {
            var d = oldGroups[g];

            if (d.Count <= 0)
                continue;

            if (d.Offset < 0)
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

        if (newGroupCount == 0 || newTotalHandles == 0)
        {
            PublishEmpty();
            return;
        }

        // Pass 2: allocate output arrays and compute offsets (compact groups: drop empties)
        var newAll = new FileHandle[newTotalHandles];
        var newGroups = new HashGroupDescriptor[newGroupCount];

        var wGroup = 0;
        var wOffset = 0;

        var totalDupCount = 0;
        long totalSpaceDup = 0;

        for (var g = 0; g < oldGroups.Length; g++)
        {
            var newCount = newCounts[g];
            if (newCount <= 0)
                continue;

            var old = oldGroups[g];

            newGroups[wGroup] = new HashGroupDescriptor(
                Hash: old.Hash,
                SizeBytes: old.SizeBytes,
                Offset: wOffset,
                Count: newCount,
                FirstFile: newReps[g]);

            if (newCount > 1)
            {
                totalDupCount += newCount - 1;
                totalSpaceDup += (newCount - 1) * old.SizeBytes;
            }

            wOffset += newCount;
            wGroup++;
        }

        // Pass 3: fill newAll by slicing oldAll and copying survivors into assigned segments.
        var dstOffset = 0;

        for (var g = 0; g < oldGroups.Length; g++)
        {
            var newCount = newCounts[g];
            if (newCount <= 0)
                continue;

            var d = oldGroups[g];
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

        var (bySize, byCount) = BuildSortedViews(newGroups);
        Publish(newAll, newGroups, bySize, byCount, new StatsSnapshot(totalDupCount, totalSpaceDup));
    }

    // ---------------------------------------------------------------------
    // Sorting / publishing
    // ---------------------------------------------------------------------

    private static (int[] bySize, int[] byCount) BuildSortedViews(HashGroupDescriptor[] groups)
    {
        var bySize = BuildIndexArray(groups.Length);
        Array.Sort(bySize, new BySizeDescComparer(groups));

        var byCount = BuildIndexArray(groups.Length);
        Array.Sort(byCount, new ByCountDescComparer(groups));

        return (bySize, byCount);
    }

    private void PublishEmpty()
        => Publish([], [], [], [], new StatsSnapshot(0, 0));

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

            var aTotal = TotalBytes(a);
            var bTotal = TotalBytes(b);
            var c = bTotal.CompareTo(aTotal);
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

            var aTotal = TotalBytes(a);
            var bTotal = TotalBytes(b);
            c = bTotal.CompareTo(aTotal);
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, MemoryPackSerializer.Serialize(state));
    }

    private bool TryLoadState(long expectedGeneration)
    {
        var path = GetStateFilePath();
        if (!File.Exists(path))
            return false;

        try
        {
            var state = MemoryPackSerializer.Deserialize<HashIndexState>(File.ReadAllBytes(path));
            if (state == null) return false;
            if (state.LastIndexedGeneration != expectedGeneration)
                return false;

            using (TimingLog.StartPhase("Rehydrating hash index"))
            {
                var allFiles = state.AllFiles ?? [];
                var groups = state.Groups ?? [];

                // Prefer persisted views; if missing, rebuild (but still load is fast-ish)
                var bySize = state.BySizeDesc;
                var byCount = state.ByCountDesc;

                if (bySize == null || byCount == null ||
                    bySize.Length != groups.Length || byCount.Length != groups.Length)
                {
                    (bySize, byCount) = BuildSortedViews(groups);
                }

                _allFiles = allFiles;
                _groups = groups;
                _bySizeDesc = bySize;
                _byCountDesc = byCount;
                _stats = new StatsSnapshot(state.TotalDuplicateFileCount, state.TotalSpaceTakenByDuplicates);
                _lastIndexedGeneration = state.LastIndexedGeneration;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Internal types
    // ---------------------------------------------------------------------

    private sealed record StatsSnapshot(int DuplicateFileCount, long SpaceTakenByDuplicates);
}
