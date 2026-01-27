// DuplicateFileFinderLib/Repository/Plugins/FolderHashIndexPlugin.cs

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins;

public sealed class FolderHashIndexPlugin : ChannelRepoPlugin, IFolderHashIndexReadModel
{
    private const string StateFileName = "folder-hash-index.bin";

    private readonly string _dataDirectory;

    private readonly ITreeIndexReadModel _tree;

    // Published: concatenation of all grouped dirs
    private volatile DirHandle[] _allDirs = [];

    // Published: dense descriptors (unsorted)
    private volatile FolderGroupDescriptor[] _groups = [];

    // Published: sorted view as indices into _groups[]
    private volatile int[] _byCountDesc = [];

    private volatile int _totalDuplicateFolderCount;

    private long _lastIndexedGeneration;

    public FolderHashIndexPlugin(string dataDirectory, ITreeIndexReadModel treeIndex)
        : base(4096)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory must be non-empty.", nameof(dataDirectory));

        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(_dataDirectory);

        _tree = treeIndex ?? throw new ArgumentNullException(nameof(treeIndex));
    }

    public int TotalDuplicateFolderCount => _totalDuplicateFolderCount;

    // ---------------------------------------------------------------------
    // IFolderHashIndexReadModel
    // ---------------------------------------------------------------------

    public ReadOnlySpan<DirHandle> GetGroupDirs(in FolderGroupDescriptor group)
    {
        var all = _allDirs;

        if (group.Count <= 0 || group.Offset < 0)
            return ReadOnlySpan<DirHandle>.Empty;

        var end = group.Offset + group.Count;
        if ((uint)end > (uint)all.Length)
            return ReadOnlySpan<DirHandle>.Empty;

        return all.AsSpan(group.Offset, group.Count);
    }

    public FolderDuplicateGroupPage GetGroupsPage(int offset, int count, FolderDuplicateSort sort)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

        var groups = _groups;
        if (groups.Length == 0)
            return new FolderDuplicateGroupPage(offset, 0, ReadOnlyMemory<FolderGroupDescriptor>.Empty);

        var order = _byCountDesc;
        if (order.Length == 0)
            return new FolderDuplicateGroupPage(offset, 0, ReadOnlyMemory<FolderGroupDescriptor>.Empty);

        var page = new FolderGroupDescriptor[count];
        var w = 0;
        var seen = 0;

        for (int i = 0; i < order.Length; i++)
        {
            var d = groups[order[i]];

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
            return new FolderDuplicateGroupPage(offset, 0, ReadOnlyMemory<FolderGroupDescriptor>.Empty);

        if (w != page.Length)
            Array.Resize(ref page, w);

        return new FolderDuplicateGroupPage(offset, w, page);
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
        if (evt.Generation <= _lastIndexedGeneration)
            return;

        RebuildAndCommit(evt.Generation, () => RebuildFromSnapshot(evt.RepoSnapshotView));
    }

    protected override void OnRepoScanRootRemovedEvent(RepoScanRootRemovedEvent evt)
    {
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
    // Core rebuild
    // ---------------------------------------------------------------------

    private struct GroupMeta
    {
        public int Count;
        public int Offset;
        public int Cursor;

        public DirHandle FirstDir;

        // Optional descriptor fields (for display)
        public int ChildFileCount;
        public int ChildDirCount;
    }

    private static bool IsLiveDir(DirRecordV2 d)
        => d.Status is not (ScanEntryStatus.Deleted or ScanEntryStatus.None);

    private static bool IsEligibleFileForFolderHash(in FileRecordV2 f)
    {
        if (f.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
            return false;

        // keep consistent with TreeIndex duplicate/stat logic: ignore <= 0 sizes
        if (f.Size <= 0)
            return false;

        return true;
    }

    private void RebuildExcludingScanRoot(long removedScanRootId)
    {
        using var _ = TimingLog.StartPhase("FolderHashIndex.RebuildExcludingScanRoot");

        var oldGroups = _groups;
        var oldAll = _allDirs;

        if (oldGroups.Length == 0 || oldAll.Length == 0)
        {
            PublishEmpty();
            return;
        }

        // Pass 1: plan counts + reps, totals
        var newCounts = new int[oldGroups.Length];
        var newReps = new DirHandle[oldGroups.Length];

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

            var survivors = 0;
            var rep = new DirHandle(-1, -1);

            for (var i = d.Offset; i < end; i++)
            {
                var h = oldAll[i];
                if (h.ScanRootId == removedScanRootId)
                    continue;

                if (!rep.IsValid)
                    rep = h;

                survivors++;
            }

            if (survivors < 2)
                continue;

            newCounts[g] = survivors;
            newReps[g] = rep;

            newTotalHandles += survivors;
            newGroupCount++;
        }

        if (newGroupCount == 0 || newTotalHandles == 0)
        {
            PublishEmpty();
            return;
        }

        // Pass 2: build new descriptors with compacted offsets
        var newGroups = new FolderGroupDescriptor[newGroupCount];
        var newAll = new DirHandle[newTotalHandles];

        var wGroup = 0;
        var wOffset = 0;

        for (var g = 0; g < oldGroups.Length; g++)
        {
            var cnt = newCounts[g];
            if (cnt <= 0)
                continue;

            var old = oldGroups[g];

            newGroups[wGroup] = old with
            {
                Offset = wOffset,
                Count = cnt,
                FirstDir = newReps[g]
            };

            wOffset += cnt;
            wGroup++;
        }

        // Pass 3: fill newAll by copying surviving handles group-by-group
        var dst = 0;

        for (var g = 0; g < oldGroups.Length; g++)
        {
            var cnt = newCounts[g];
            if (cnt <= 0)
                continue;

            var d = oldGroups[g];
            var end = d.Offset + d.Count;

            var wrote = 0;
            for (var i = d.Offset; i < end; i++)
            {
                var h = oldAll[i];
                if (h.ScanRootId == removedScanRootId)
                    continue;

                newAll[dst + wrote] = h;
                wrote++;

                if (wrote == cnt)
                    break;
            }

            dst += cnt;
        }

        // Recompute sorted view and stats
        PublishComputed(newAll, newGroups);
    }

    private void RebuildFromSnapshot(RepoSnapshotView repo)
    {
        // ReSharper disable once RedundantAssignment
        using var rebuildTimer = TimingLog.StartPhase("Rebuilding FolderHashIndex");

        // live scan roots
        var liveRootIds = new HashSet<long>(
            repo.ScanRoots.Values.Where(r => !r.IsDeleted).Select(r => r.RootId));

        if (liveRootIds.Count == 0)
        {
            PublishEmpty();
            return;
        }

        // 1) Compute per-dir folder hash for every live dir across every live root
        // 2) Group dirs by folder hash (only computed ones)
        var metaByHash = new Dictionary<HashKey, GroupMeta>(capacity: 1024);
        var totalDirsInGroups = 0;

        foreach (var (rootId, snap) in repo.Snapshots)
        {
            if (!liveRootIds.Contains(rootId))
                continue;

            ComputeRootFolderHashes(
                snapshot: snap,
                out var folderHashByDirIndex,
                out var canComputeByDirIndex,
                out var directChildCountsByDirIndex);

            for (int di = 0; di < snap.Dirs.Count; di++)
            {
                if (!IsLiveDir(snap.Dirs[di]))
                    continue;

                if (!canComputeByDirIndex[di])
                    continue;

                var h = folderHashByDirIndex[di];

                // never treat sentinels as valid computed folder hashes
                if (!h.IsComputed)
                    continue;

                totalDirsInGroups++;

                ref var meta = ref CollectionsMarshal.GetValueRefOrAddDefault(metaByHash, h, out var exists);
                if (!exists)
                {
                    var (cf, cd) = directChildCountsByDirIndex[di];

                    meta = new GroupMeta
                    {
                        Count = 1,
                        Offset = 0,
                        Cursor = 0,
                        FirstDir = new DirHandle(rootId, di),
                        ChildFileCount = cf,
                        ChildDirCount = cd
                    };
                }
                else
                {
                    meta.Count++;
                }
            }
        }

        // keep only hashes with >= 2 dirs
        if (metaByHash.Count == 0)
        {
            PublishEmpty();
            return;
        }

        // prune singletons without allocating a second dictionary
        // (rarely huge; but keep it tight)
        var keys = metaByHash.Keys.ToArray();
        foreach (var k in keys)
            if (metaByHash[k].Count < 2)
                metaByHash.Remove(k);

        if (metaByHash.Count == 0)
        {
            PublishEmpty();
            return;
        }

        // recompute total after pruning
        totalDirsInGroups = 0;
        foreach (var m in metaByHash.Values)
            totalDirsInGroups += m.Count;

        if (totalDirsInGroups == 0)
        {
            PublishEmpty();
            return;
        }

        // Pass 2: assign offsets + build descriptors
        var groups = new FolderGroupDescriptor[metaByHash.Count];
        var allDirs = new DirHandle[totalDirsInGroups];

        var offset = 0;
        var gi = 0;

        foreach (var (hash, meta0) in metaByHash)
        {
            var meta = meta0;
            meta.Offset = offset;
            meta.Cursor = 0;
            metaByHash[hash] = meta;

            groups[gi++] = new FolderGroupDescriptor(
                FolderHash: hash,
                Offset: offset,
                Count: meta.Count,
                FirstDir: meta.FirstDir,
                ChildFileCount: meta.ChildFileCount,
                ChildDirCount: meta.ChildDirCount);

            offset += meta.Count;
        }

        // Pass 3: fill allDirs (second walk over roots)
        foreach (var (rootId, snap) in repo.Snapshots)
        {
            if (!liveRootIds.Contains(rootId))
                continue;

            ComputeRootFolderHashes(
                snapshot: snap,
                out var folderHashByDirIndex,
                out var canComputeByDirIndex,
                out _);

            for (int di = 0; di < snap.Dirs.Count; di++)
            {
                if (!IsLiveDir(snap.Dirs[di]))
                    continue;

                if (!canComputeByDirIndex[di])
                    continue;

                var h = folderHashByDirIndex[di];
                if (!h.IsComputed)
                    continue;

                if (!metaByHash.TryGetValue(h, out var meta))
                    continue;

                var writeIndex = meta.Offset + meta.Cursor;
                meta.Cursor++;
                metaByHash[h] = meta;

                allDirs[writeIndex] = new DirHandle(rootId, di);
            }
        }

        PublishComputed(allDirs, groups);
    }

    private void ComputeRootFolderHashes(
        ScanRootSnapshotView snapshot,
        out HashKey[] folderHashByDirIndex,
        out bool[] canComputeByDirIndex,
        out (int childFiles, int childDirs)[] directChildCountsByDirIndex)
    {
        folderHashByDirIndex = new HashKey[snapshot.Dirs.Count];
        canComputeByDirIndex = new bool[snapshot.Dirs.Count];
        directChildCountsByDirIndex = new (int, int)[snapshot.Dirs.Count];

        // initialise: live dirs default to true; non-live remain false
        for (int i = 0; i < snapshot.Dirs.Count; i++)
        {
            if (IsLiveDir(snapshot.Dirs[i]))
                canComputeByDirIndex[i] = true;
        }

        // find live roots (ParentDirId < 0)
        var rootDirIndices = new List<int>(capacity: 8);
        for (int i = 0; i < snapshot.Dirs.Count; i++)
        {
            var d = snapshot.Dirs[i];
            if (!IsLiveDir(d))
                continue;

            if (d.ParentDirId < 0)
                rootDirIndices.Add(i);
        }

        if (rootDirIndices.Count == 0)
            return;

        // postorder over live tree according to TreeIndex child edges
        var visited = new bool[snapshot.Dirs.Count];
        var postorder = new List<int>(capacity: snapshot.Dirs.Count);

        for (int r = 0; r < rootDirIndices.Count; r++)
        {
            var root = rootDirIndices[r];
            if ((uint)root >= (uint)visited.Length)
                continue;

            if (visited[root])
                continue;

            var stack = new Stack<(int dir, int nextChild)>(capacity: 64);
            stack.Push((root, 0));
            visited[root] = true;

            while (stack.Count > 0)
            {
                var (d, next) = stack.Pop();

                var children = _tree.GetChildDirs(new DirHandle(snapshot.ScanRootId, d));
                if (next < children.Length)
                {
                    // resume this node
                    stack.Push((d, next + 1));

                    var childIndex = children[next].Index;
                    if ((uint)childIndex >= (uint)visited.Length)
                        continue;

                    if (visited[childIndex])
                        continue;

                    visited[childIndex] = true;
                    stack.Push((childIndex, 0));
                }
                else
                {
                    postorder.Add(d);
                }
            }
        }

        // scratch lists reused per node to limit allocations
        var fileHashes = new List<HashKey>(capacity: 32);
        var dirHashes = new List<HashKey>(capacity: 16);

        for (int i = 0; i < postorder.Count; i++)
        {
            var dirIndex = postorder[i];

            // skip non-live (defensive)
            if (!IsLiveDir(snapshot.Dirs[dirIndex]))
            {
                canComputeByDirIndex[dirIndex] = false;
                continue;
            }

            // if already cannot compute (e.g. from earlier), keep it
            if (!canComputeByDirIndex[dirIndex])
                continue;

            fileHashes.Clear();
            dirHashes.Clear();

            // direct child files
            var childFiles = _tree.GetChildFiles(new DirHandle(snapshot.ScanRootId, dirIndex));
            var childFileCount = 0;

            for (int fi = 0; fi < childFiles.Length; fi++)
            {
                var fh = childFiles[fi];
                var f = snapshot.Files[fh.Index];

                if (!IsEligibleFileForFolderHash(in f))
                    continue;

                // If a real file exists but its hash isn't computed, we can't produce a stable folder hash.
                if (!f.Hash.IsComputed)
                {
                    canComputeByDirIndex[dirIndex] = false;
                    break;
                }

                childFileCount++;
                fileHashes.Add(f.Hash);
            }

            if (!canComputeByDirIndex[dirIndex])
                continue;

            // direct child dirs
            var childDirs = _tree.GetChildDirs(new DirHandle(snapshot.ScanRootId, dirIndex));
            var childDirCount = 0;

            for (int ci = 0; ci < childDirs.Length; ci++)
            {
                var child = childDirs[ci];

                // if child dir hash cannot compute, parent cannot compute
                if ((uint)child.Index >= (uint)canComputeByDirIndex.Length || !canComputeByDirIndex[child.Index])
                {
                    canComputeByDirIndex[dirIndex] = false;
                    break;
                }

                var h = folderHashByDirIndex[child.Index];

                // child must have a computed folder hash (never a sentinel)
                if (!h.IsComputed)
                {
                    canComputeByDirIndex[dirIndex] = false;
                    break;
                }

                childDirCount++;
                dirHashes.Add(h);
            }

            if (!canComputeByDirIndex[dirIndex])
                continue;

            directChildCountsByDirIndex[dirIndex] = (childFileCount, childDirCount);

            // deterministic order-independent hashing: sort by (A,B)
            if (fileHashes.Count > 1)
                fileHashes.Sort(HashKeyABComparer.Instance);

            if (dirHashes.Count > 1)
                dirHashes.Sort(HashKeyABComparer.Instance);

            folderHashByDirIndex[dirIndex] = ComputeFolderHash(childFileCount, childDirCount, fileHashes, dirHashes);
        }
    }

    private static HashKey ComputeFolderHash(
        int childFileCount,
        int childDirCount,
        List<HashKey> fileHashes,
        List<HashKey> dirHashes)
    {
        // stream:
        // [fileCount:int32][dirCount:int32][fileHash*][dirHash*]
        checked
        {
            var byteCount = 8 + 16 * (fileHashes.Count + dirHashes.Count);
            var rented = ArrayPool<byte>.Shared.Rent(byteCount);

            try
            {
                var span = rented.AsSpan(0, byteCount);

                // little-endian counts
                Unsafe.WriteUnaligned(ref span[0], childFileCount);
                Unsafe.WriteUnaligned(ref span[4], childDirCount);

                var w = 8;

                for (int i = 0; i < fileHashes.Count; i++)
                {
                    fileHashes[i].ToByteArray(span.Slice(w, 16));
                    w += 16;
                }

                for (int i = 0; i < dirHashes.Count; i++)
                {
                    dirHashes[i].ToByteArray(span.Slice(w, 16));
                    w += 16;
                }

                // MD5 hash into 16 bytes -> HashKey
                Span<byte> out16 = stackalloc byte[16];
                var hash = MD5.HashData(span);
                hash.CopyTo(out16);

                return new HashKey(out16);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private sealed class HashKeyABComparer : IComparer<HashKey>
    {
        public static readonly HashKeyABComparer Instance = new();

        public int Compare(HashKey x, HashKey y)
        {
            var c = x.A.CompareTo(y.A);
            return c != 0 ? c : x.B.CompareTo(y.B);
        }
    }

    // ---------------------------------------------------------------------
    // Sorting / publishing
    // ---------------------------------------------------------------------

    private void PublishEmpty()
    {
        _allDirs = [];
        _groups = [];
        _byCountDesc = [];
        _totalDuplicateFolderCount = 0;
    }

    private void PublishComputed(DirHandle[] allDirs, FolderGroupDescriptor[] groups)
    {
        var byCount = BuildIndexArray(groups.Length);
        Array.Sort(byCount, new ByCountDescComparer(groups));

        int dupFolderCount = 0;
        for (int i = 0; i < groups.Length; i++)
            dupFolderCount += groups[i].Count - 1;

        Publish(allDirs, groups, byCount, dupFolderCount);
    }

    private void Publish(DirHandle[] allDirs, FolderGroupDescriptor[] groups, int[] byCountDesc, int totalDuplicateFolderCount)
    {
        _allDirs = allDirs;
        _groups = groups;
        _byCountDesc = byCountDesc;
        _totalDuplicateFolderCount = totalDuplicateFolderCount;
    }

    private static int[] BuildIndexArray(int n)
    {
        if (n == 0) return [];
        var arr = new int[n];
        for (int i = 0; i < n; i++) arr[i] = i;
        return arr;
    }

    private sealed class ByCountDescComparer(FolderGroupDescriptor[] groups) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            var a = groups[x];
            var b = groups[y];

            var c = b.Count.CompareTo(a.Count);
            if (c != 0) return c;

            // tie-break: more direct children first (helps stable display)
            c = (b.ChildFileCount + b.ChildDirCount).CompareTo(a.ChildFileCount + a.ChildDirCount);
            if (c != 0) return c;

            // deterministic
            c = b.FolderHash.A.CompareTo(a.FolderHash.A);
            if (c != 0) return c;
            c = b.FolderHash.B.CompareTo(a.FolderHash.B);
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
        var state = new FolderHashIndexState
        {
            LastIndexedGeneration = _lastIndexedGeneration,
            TotalDuplicateFolderCount = _totalDuplicateFolderCount,
            AllDirs = _allDirs,
            Groups = _groups,
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
            var state = MemoryPackSerializer.Deserialize<FolderHashIndexState>(File.ReadAllBytes(path));
            if (state is null)
                return false;

            if (state.LastIndexedGeneration != expectedGeneration)
                return false;

            using (TimingLog.StartPhase("Rehydrating folder hash index"))
            {
                var allDirs = state.AllDirs ?? [];
                var groups = state.Groups ?? [];

                var byCount = state.ByCountDesc;
                if (byCount == null || byCount.Length != groups.Length)
                {
                    byCount = BuildIndexArray(groups.Length);
                    Array.Sort(byCount, new ByCountDescComparer(groups));
                }

                Publish(allDirs, groups, byCount, state.TotalDuplicateFolderCount);
                _lastIndexedGeneration = state.LastIndexedGeneration;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
