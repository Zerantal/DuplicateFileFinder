using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using MemoryPack;
using HashIndexState = DuplicateFileFinderLib.Repository.Plugins.Models.HashIndexState;

namespace DuplicateFileFinderLib.Repository.Plugins;

public sealed class HashIndexPlugin : ChannelRepoPlugin, IHashIndexReadModel
{
    private readonly Lock _lock = new();

    // HashKey => (size, fileIds)
    private readonly Dictionary<HashKey, (long size, List<long> list)> _hashToFileRecords = new();

    private long _lastIndexedGeneration;
    private long _lastIndexedLogSequence;
    
    private int _totalDuplicateFileCount;
    private long _totalSpaceTakenByDuplicates;

    private readonly string _dataDirectory;

    private const string StateFileName = "hash-index.bin";

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
        // Try to load persisted index that matches this generation/log sequence.
        if (!TryLoadState(evt.Generation, evt.NextLogSequence - 1))
        {
            // Fallback: rebuild from snapshot and persist.
            RebuildFromSnapshot(evt.Snapshot);
            lock (_lock)
            {
                _lastIndexedGeneration = evt.Generation;
                _lastIndexedLogSequence = evt.NextLogSequence - 1;
            }

            SaveState();
        }
        else
        {
            // Loaded state is already consistent with generation/log seq.
            lock (_lock)
            {
                _lastIndexedGeneration = evt.Generation;
                _lastIndexedLogSequence = evt.NextLogSequence - 1;
            }
        }
    }

    protected override void OnDeltaCommittedEvent(DeltaCommittedEvent evt)
    {
        ApplyDeltaToIndex(evt.Delta);
        lock (_lock)
        {
            _lastIndexedGeneration = evt.Generation;
            _lastIndexedLogSequence = evt.NextLogSequence - 1;
        }
    }

    protected override void OnCompactedEvent(CompactedEvent evt)
    {
        // After compaction, it’s safer to rebuild from snapshot and persist a clean state.
        RebuildFromSnapshot(evt.Snapshot);
        lock (_lock)
        {
            _lastIndexedGeneration = evt.Generation;
            _lastIndexedLogSequence = evt.NextLogSequence - 1;
        }

        SaveState();
    }

    // ---------------------------------------------------------------------
    // Core index maintenance
    // ---------------------------------------------------------------------

    private void RebuildFromSnapshot(IRepoView snapshot)
    {
        var newIndex = new Dictionary<HashKey, (long size, List<long> list)>();

        var totalDupCount = 0;
        long totalSpaceDup = 0;

        foreach (var file in snapshot.Files.Values)
        {
            if (file.Hash == HashKey.NotComputed || file.Hash == HashKey.CannotCompute)
                continue;

            if (!newIndex.TryGetValue(file.Hash, out var group))
            {
                group = (file.Size, new List<long>());
                newIndex[file.Hash] = group;
            }

            group.list.Add(file.FileId);
        }

        // Compute duplicate stats from the new index.
        foreach (var group in newIndex.Values)
        {
            if (group.list.Count <= 1)
                continue;

            var dupCount = group.list.Count - 1;
            totalDupCount += dupCount;
            totalSpaceDup += dupCount * group.size;
        }

        lock (_lock)
        {
            _hashToFileRecords.Clear();
            foreach (var (hash, ids) in newIndex)
                _hashToFileRecords[hash] = ids;

            _totalDuplicateFileCount = totalDupCount;
            _totalSpaceTakenByDuplicates = totalSpaceDup;
        }
    }

    private void ApplyDeltaToIndex(RepoDelta delta)
    {
        lock (_lock)
        {
            // remove / tombstone old files
            foreach (var file in delta.Files.Where(f => f.Status == ScanEntryStatus.Deleted))
                if (_hashToFileRecords.TryGetValue(file.Hash, out var group))
                {
                    group.list.Remove(file.FileId);
                    if (group.list.Count == 0)
                        _hashToFileRecords.Remove(file.Hash);
                    else
                        _hashToFileRecords[file.Hash] = group;
                }

            // add / update files (including re-hashed files)
            foreach (var file in delta.Files.Where(f =>
                         f.Status != ScanEntryStatus.Deleted &&
                         f.Hash != HashKey.NotComputed &&
                         f.Hash != HashKey.CannotCompute))
            {
                if (!_hashToFileRecords.TryGetValue(file.Hash, out var group))
                {
                    group = (file.Size, new List<long>());
                    _hashToFileRecords[file.Hash] = group;
                }

                if (!group.list.Contains(file.FileId))
                    group.list.Add(file.FileId);

                _hashToFileRecords[file.Hash] = group;
            }

            // Recompute stats after applying delta.
            RecomputeStats_NoLock();
        }
    }

    private void RecomputeStats_NoLock()
    {
        var totalDupCount = 0;
        long totalSpaceDup = 0;

        foreach (var group in _hashToFileRecords.Values)
        {
            if (group.list.Count <= 1)
                continue;

            var dupCount = group.list.Count - 1;
            totalDupCount += dupCount;
            totalSpaceDup += dupCount * group.size;
        }

        _totalDuplicateFileCount = totalDupCount;
        _totalSpaceTakenByDuplicates = totalSpaceDup;
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
        HashIndexState state;

        lock (_lock)
        {
            var indexCopy = new Dictionary<HashKey, (long size, List<long> list)>(_hashToFileRecords.Count);
            foreach (var (hash, group) in _hashToFileRecords)
                // Copy the list so callers can’t mutate persisted state via references.
                indexCopy[hash] = (group.size, [..group.list]);

            state = new HashIndexState
            {
                LastIndexedGeneration = _lastIndexedGeneration,
                LastIndexedLogSequence = _lastIndexedLogSequence,
                Index = indexCopy,
                TotalDuplicateFileCount = _totalDuplicateFileCount,
                TotalSpaceTakenByDuplicates = _totalSpaceTakenByDuplicates
            };
        }

        var path = GetStateFilePath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var bytes = MemoryPackSerializer.Serialize(state);
        File.WriteAllBytes(path, bytes);
    }

    private bool TryLoadState(long expectedGeneration, long expectedLastLogSequence)
    {
        var path = GetStateFilePath();
        if (!File.Exists(path))
            return false;

        try
        {
            var bytes = File.ReadAllBytes(path);
            var state = MemoryPackSerializer.Deserialize<HashIndexState>(bytes);
            if (state is null)
                return false;

            // Only use the state if it matches the current repo position.
            if (state.LastIndexedGeneration != expectedGeneration ||
                state.LastIndexedLogSequence != expectedLastLogSequence)
                return false;

            lock (_lock)
            {
                _hashToFileRecords.Clear();
                foreach (var (hash, group) in state.Index)
                    // lists can be taken as-is; they’re private to this plugin
                    _hashToFileRecords[hash] = (group.size, group.list);

                _totalDuplicateFileCount = state.TotalDuplicateFileCount;
                _totalSpaceTakenByDuplicates = state.TotalSpaceTakenByDuplicates;

                _lastIndexedGeneration = state.LastIndexedGeneration;
                _lastIndexedLogSequence = state.LastIndexedLogSequence;
            }

            return true;
        }
        catch
        {
            // Corrupt or incompatible state; ignore and rebuild from snapshot.
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Public query surface
    // ---------------------------------------------------------------------

    public IReadOnlyList<(long size, IReadOnlyList<long> list)> GetDuplicateGroups(
        int minDuplicates = 2,
        long minSize = 1)
    {
        if (minDuplicates < 2) throw new ArgumentOutOfRangeException(nameof(minDuplicates));
        if (minSize < 1) throw new ArgumentOutOfRangeException(nameof(minSize));

        lock (_lock)
        {
            return _hashToFileRecords.Values
                .Where(group => group.list.Count >= minDuplicates && group.size > minSize)
                .Select(group => ((long size, IReadOnlyList<long>))(group.size, group.list.ToArray()))
                .ToArray();
        }
    }

    public int TotalDuplicateFileCount
    {
        get
        {
            lock (_lock)
            {
                return _totalDuplicateFileCount;
            }
        }
    }

    public long TotalSpaceTakenByDuplicates
    {
        get
        {
            lock (_lock)
            {
                return _totalSpaceTakenByDuplicates;
            }
        }
    }
}