using System.Collections.Immutable;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using MemoryPack;
using HashIndexState = DuplicateFileFinderLib.Repository.Plugins.Models.HashIndexState;

namespace DuplicateFileFinderLib.Repository.Plugins;

public sealed class HashIndexPlugin : ChannelRepoPlugin, IHashIndexReadModel
{
    // Published (read-only) index snapshot
    private volatile ImmutableDictionary<HashKey, HashGroup> _groupsByHash
        = ImmutableDictionary<HashKey, HashGroup>.Empty;

    // Published stats snapshot
    private volatile StatsSnapshot _stats = new(0, 0);
    public int TotalDuplicateFileCount => _stats.DuplicateFileCount;
    public long TotalSpaceTakenByDuplicates => _stats.SpaceTakenByDuplicates;

    // Persisted position (only mutated on bootstrap/compaction thread)
    private long _lastIndexedGeneration;
    private long _lastIndexedLogSequence;
    
    private readonly string _dataDirectory;
    private const string StateFileName = "hash-index.bin";

    private readonly record struct HashGroup(long Size, ImmutableArray<FileHandle> Files);
    private sealed record StatsSnapshot(int DuplicateFileCount, long SpaceTakenByDuplicates);

    public HashIndexPlugin(string dataDirectory)
        : base(4096)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory must be non-empty.", nameof(dataDirectory));

        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(_dataDirectory);
    }

    // ---------------------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------------------

    protected override void OnBootstrapEvent(BootstrapEvent evt)
    {
        var expectedLastLogSequence = evt.NextLogSequence - 1;

        if (!TryLoadState(evt.Generation, expectedLastLogSequence))
        {
            // Fallback: rebuild from snapshot and persist.
            RebuildFromSnapshot(evt.RepoSnapshotView);
            _lastIndexedGeneration = evt.Generation;
            _lastIndexedLogSequence = expectedLastLogSequence;
            SaveState();
        }
        else
        {
            // Loaded state is already consistent with generation/log seq.
            _lastIndexedGeneration = evt.Generation;
            _lastIndexedLogSequence = expectedLastLogSequence;
        }
    }

    protected override void OnCompactedEvent(CompactedEvent evt)
    {
        var expectedLastLogSequence = evt.NextLogSequence - 1;

        // After compaction, it’s safer to rebuild from snapshot and persist a clean state.
        RebuildFromSnapshot(evt.RepoSnapshotView);
        _lastIndexedGeneration = evt.Generation;
        _lastIndexedLogSequence = expectedLastLogSequence;
        SaveState();
    }

    // ---------------------------------------------------------------------
    // Core index maintenance (build -> freeze -> publish)
    // ---------------------------------------------------------------------

    private void RebuildFromSnapshot(RepoSnapshotView repoSnapshot)
    {
        var snapshotDict = repoSnapshot.Snapshots;
        // Build mutable groups first
        var tmp = new Dictionary<HashKey, (long size, List<FileHandle> list)>(capacity: 1024);

        foreach (var snapshot in snapshotDict.Values)
        {
            for (int i = 0; i < snapshot.Files.Count; i++)
            {
                var file = snapshot.Files[i];
                
                if (file.Hash == HashKey.NotComputed || file.Hash == HashKey.CannotCompute)
                    continue;

                if (!tmp.TryGetValue(file.Hash, out var group))
                {
                    group = (file.Size, new List<FileHandle>());
                    tmp[file.Hash] = group;
                }

                group.list.Add(new FileHandle(snapshot.ScanRootId, i));
            }

            // Freeze into immutable dictionary once, and compute stats once
            var builder = ImmutableDictionary.CreateBuilder<HashKey, HashGroup>();

            int totalDupCount = 0;
            long totalSpaceDup = 0;
            
            // Compute duplicate stats from the new index.
            foreach (var (hash, group) in tmp)
            {
                var files = group.list.Count == 0
                    ? ImmutableArray<FileHandle>.Empty
                    : [..group.list];

                builder[hash] = new HashGroup(group.size, files);
                if (files.Length > 1)
                {
                    var dupCount = files.Length - 1;
                    totalDupCount += dupCount;
                    totalSpaceDup += dupCount * group.size;
                }
            }

            // Publish snapshots (single volatile writes)
            _groupsByHash = builder.ToImmutable();
            _stats = new StatsSnapshot(totalDupCount, totalSpaceDup);
        }
    }

// ---------------------------------------------------------------------
// Persistence
// ---------------------------------------------------------------------

    private string GetStateFilePath() => Path.Combine(_dataDirectory, StateFileName);

    private void SaveState()
    {
        // Capture published snapshots locally so they’re consistent for this write
        var groups = _groupsByHash;
        var totalDupCount = TotalDuplicateFileCount;
        var totalSpaceDup = TotalSpaceTakenByDuplicates;

        var state = new HashIndexState
        {
            LastIndexedGeneration = _lastIndexedGeneration,
            LastIndexedLogSequence = _lastIndexedLogSequence,
            TotalDuplicateFileCount = totalDupCount,
            TotalSpaceTakenByDuplicates = totalSpaceDup,
            Index = new Dictionary<HashKey, HashGroupState>(groups.Count)
        };

        foreach (var (hash, group) in groups)
        {
            state.Index[hash] = new HashGroupState
            {
                Size = group.Size,
                Files = group.Files.IsDefaultOrEmpty ? [] : group.Files.ToArray()
            };
        }

        var path = GetStateFilePath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(path, MemoryPackSerializer.Serialize(state));
    }

    private bool TryLoadState(long expectedGeneration, long expectedLastLogSequence)
    {
        var path = GetStateFilePath();
        if (!File.Exists(path))
            return false;

        try
        {
            var state = MemoryPackSerializer.Deserialize<HashIndexState>(File.ReadAllBytes(path));
            if (state is null)
                return false;

            // Only use the state if it matches the current repo position.
            if (state.LastIndexedGeneration != expectedGeneration ||
                state.LastIndexedLogSequence != expectedLastLogSequence)
                return false;

            // Rehydrate into immutable snapshot + publish
            var builder = ImmutableDictionary.CreateBuilder<HashKey, HashGroup>();

            foreach (var (hash, g) in state.Index)
            {
                var files = g.Files is { Length: > 0 }
                    ? [..g.Files]
                    : ImmutableArray<FileHandle>.Empty;

                builder[hash] = new HashGroup(g.Size, files);
            }

            _groupsByHash = builder.ToImmutable();
            _stats = new StatsSnapshot(state.TotalDuplicateFileCount, state.TotalSpaceTakenByDuplicates);

            _lastIndexedGeneration = state.LastIndexedGeneration;
            _lastIndexedLogSequence = state.LastIndexedLogSequence;

            return true;
        }
        catch
        {
            // Corrupt or incompatible state; ignore and rebuild from snapshot.
            return false;
        }
    }

// ---------------------------------------------------------------------
    // Public query surface (lock-free)
// ---------------------------------------------------------------------

    public IReadOnlyList<(long size, IReadOnlyList<FileHandle> list)> GetDuplicateGroups(
        int minDuplicates = 2,
        long minSize = 1)
    {
        if (minDuplicates < 2) throw new ArgumentOutOfRangeException(nameof(minDuplicates));
        if (minSize < 1) throw new ArgumentOutOfRangeException(nameof(minSize));

        var groups = _groupsByHash;

        // Materialize only the result list; do not clone per-group lists.
        var result = new List<(long size, IReadOnlyList<FileHandle> list)>();

        foreach (var g in groups.Values)
        {
            if (g.Files.Length >= minDuplicates && g.Size >= minSize)
                result.Add((g.Size, g.Files));
        }

        return result;
    }
}