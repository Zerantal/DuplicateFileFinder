using System.Collections;

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

    // Published flat file handles blob
    private volatile FileHandle[] _allFiles = [];

    // Published (read-only) index snapshot: hash -> (size, offset, count)
    private volatile Dictionary<HashKey, HashGroupState> _groupsByHash = new();

    // Persisted position (only mutated on bootstrap/compaction thread)
    private long _lastIndexedGeneration;

    // Published stats snapshot
    private volatile StatsSnapshot _stats = new(0, 0);

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
    // Public query surface (lock-free)
    // ---------------------------------------------------------------------

    public IReadOnlyList<(long size, IReadOnlyList<FileHandle> list)> GetDuplicateGroups(
        int minDuplicates = 2,
        long minSize = 1)
    {
        if (minDuplicates < 2)
            throw new ArgumentOutOfRangeException(nameof(minDuplicates));
        if (minSize < 1)
            throw new ArgumentOutOfRangeException(nameof(minSize));

        var groups = _groupsByHash;
        var allFiles = _allFiles;

        var result = new List<(long size, IReadOnlyList<FileHandle> list)>();

        foreach (var meta in groups.Values)
            if (meta.Count >= minDuplicates && meta.Size >= minSize)
                // Zero-copy slice view over the published flat array.
                result.Add((meta.Size, new FileHandleSlice(allFiles, meta.Offset, meta.Count)));

        return result;
    }

    // ---------------------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------------------

    protected override void OnBootstrapEvent(BootstrapEvent evt)
    {
        if (!TryLoadState(evt.Generation))
        {
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

        var oldGroups = _groupsByHash;
        var oldAll = _allFiles;

        // Rebuild per-hash handle lists excluding the removed scan root.
        var tmp = new Dictionary<HashKey, (long size, List<FileHandle> list)>(oldGroups.Count);

        foreach (var (hash, meta) in oldGroups)
        {
            if (meta.Count <= 0)
            {
                tmp[hash] = (meta.Size, new List<FileHandle>(capacity: 0));
                continue;
            }

            var list = new List<FileHandle>(capacity: meta.Count);

            var start = meta.Offset;
            var end = meta.Offset + meta.Count;

            for (var i = start; i < end; i++)
            {
                var fh = oldAll[i];
                if (fh.ScanRootId == removedRootId)
                    continue;

                list.Add(fh);
            }

            // Preserve best-known size for the group.
            tmp[hash] = (meta.Size, list);
        }

        // Drop empty groups? Either is fine, but keeping empties wastes map entries.
        // We'll drop empties to keep the published index compact.
        var compact = new Dictionary<HashKey, (long size, List<FileHandle> list)>(tmp.Count);
        foreach (var (hash, g) in tmp)
            if (g.list.Count > 0)
                compact[hash] = g;

        FlattenAndPublish(compact);

        _lastIndexedGeneration = evt.Generation;
        SaveState();
    }


    // ---------------------------------------------------------------------
    // Core index maintenance (build -> publish)
    // ---------------------------------------------------------------------

    private void RebuildFromSnapshot(RepoSnapshotView repoSnapshot)
    {
        var liveScanRoots = repoSnapshot.ScanRoots.Values.Where(r => !r.IsDeleted);

        var tmp = new Dictionary<HashKey, (long size, List<FileHandle> list)>(1024);

        foreach (var scanRootId in liveScanRoots.Select(r => r.RootId))
        {
            var snapshot = repoSnapshot.Snapshots[scanRootId];
            {
                for (var i = 0; i < snapshot.Files.Count; i++)
                {
                    var file = snapshot.Files[i];

                    // Filter deleted/absent
                    if (file.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                        continue;

                    if (file.Hash == HashKey.NotComputed || file.Hash == HashKey.CannotCompute)
                        continue;

                    if (!tmp.TryGetValue(file.Hash, out var group))
                    {
                        group = (file.Size, new List<FileHandle>());
                        tmp[file.Hash] = group;
                    }
                    else
                    {
                        // Robust: size should be identical for equal-content hashes, but keep stable if data is imperfect.
                        if (file.Size > group.size)
                            group = (file.Size, group.list);
                        tmp[file.Hash] = group;
                    }

                    group.list.Add(new FileHandle(snapshot.ScanRootId, i));
                }
            }

            FlattenAndPublish(tmp);
        }
    }

    private void FlattenAndPublish(Dictionary<HashKey, (long size, List<FileHandle> list)> tmp)
    {
        var totalHandles = 0;
        var totalDupCount = 0;
        long totalSpaceDup = 0;

        foreach (var (_, g) in tmp)
        {
            var c = g.list.Count;
            totalHandles += c;

            if (c > 1)
            {
                var dup = c - 1;
                totalDupCount += dup;
                totalSpaceDup += dup * g.size;
            }
        }

        var allFiles = totalHandles == 0 ? Array.Empty<FileHandle>() : new FileHandle[totalHandles];
        var groups = new Dictionary<HashKey, HashGroupState>(tmp.Count);

        var offset = 0;
        foreach (var (hash, g) in tmp)
        {
            var count = g.list.Count;

            if (count > 0)
            {
                g.list.CopyTo(allFiles, offset);
                groups[hash] = new HashGroupState { Size = g.size, Offset = offset, Count = count };
                offset += count;
            }
            else
            {
                groups[hash] = new HashGroupState { Size = g.size, Offset = offset, Count = 0 };
            }
        }

        // Publish snapshots
        _allFiles = allFiles;
        _groupsByHash = groups;
        _stats = new StatsSnapshot(totalDupCount, totalSpaceDup);
    }

    // ---------------------------------------------------------------------
    // Persistence
    // ---------------------------------------------------------------------

    private string GetStateFilePath()
    {
        return Path.Combine(_dataDirectory, StateFileName);
    }

    private void SaveState()
    {
        var groups = _groupsByHash;
        var allFiles = _allFiles;

        var index = new KeyValuePair<HashKey, HashGroupState>[groups.Count];
        var i = 0;
        foreach (var (hash, meta) in groups)
            index[i++] = new KeyValuePair<HashKey, HashGroupState>(
                hash,
                new HashGroupState { Size = meta.Size, Offset = meta.Offset, Count = meta.Count });

        var state = new HashIndexState
        {
            LastIndexedGeneration = _lastIndexedGeneration,
            TotalDuplicateFileCount = TotalDuplicateFileCount,
            TotalSpaceTakenByDuplicates = TotalSpaceTakenByDuplicates,
            AllFiles = allFiles,
            Index = index
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

            if (state.LastIndexedGeneration != expectedGeneration)
                return false;

            Dictionary<HashKey, HashGroupState> groups;

            using (TimingLog.StartPhase("Rehydrating hash index"))
            {
                groups = new Dictionary<HashKey, HashGroupState>(state.Index);
            }

            _allFiles = state.AllFiles;
            _groupsByHash = groups;
            _stats = new StatsSnapshot(state.TotalDuplicateFileCount, state.TotalSpaceTakenByDuplicates);
            _lastIndexedGeneration = state.LastIndexedGeneration;

            return true;
        }
        catch
        {
            return false;
        }
    }


    // private readonly record struct HashGroupMeta(long Size, int Offset, int Count);
    private sealed record StatsSnapshot(int DuplicateFileCount, long SpaceTakenByDuplicates);

    private readonly struct FileHandleSlice(FileHandle[] all, int offset, int count) : IReadOnlyList<FileHandle>
    {
        public int Count { get; } = count;

        public FileHandle this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return all[offset + index];
            }
        }

        public IEnumerator<FileHandle> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
                yield return all[offset + i];
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
