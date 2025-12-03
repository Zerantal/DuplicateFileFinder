using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Plugins;

public sealed class HashIndexPlugin() : ChannelRepoPlugin(capacity: 4096), IHashIndexReadModel
{
    private readonly object _lock = new();
    private readonly Dictionary<HashKey, List<FileRecord>> _hashToFileRecords = new();
    
    // ReSharper disable once NotAccessedField.Local
    private long _lastIndexedGeneration;
    // ReSharper disable once NotAccessedField.Local
    private long _lastIndexedLogSequence;

    // ReSharper disable once InconsistentNaming
    private bool evtSuccessfullyHandler = true;
    private int _totalDuplicateFileCount;
    private long _totalSpaceTakenByDuplicates;

    protected override ValueTask HandleEventAsync(RepoEvent evt, CancellationToken ct)
    {
        switch (evt)
        {
            case BootstrapEvent bootstrap:
                HandleBootstrap(bootstrap);
                SignalReady();
                break;

            case DeltaCommittedEvent deltaEvt:
                HandleDelta(deltaEvt);
                break;

            case CompactedEvent compacted:
                // Optional: rebuild from fresh snapshot instead of incremental deltas
                HandleCompacted(compacted);
                break;

            case ScanRunCompletedEvent _:
                break;
        }

        if (evtSuccessfullyHandler)
        {
            _lastIndexedGeneration = evt.Generation;
            _lastIndexedLogSequence = evt.NextLogSequence;
        }
        
        return ValueTask.CompletedTask;
    }
    
    private void HandleBootstrap(BootstrapEvent evt)
    {
        RebuildFromSnapshot(evt.Snapshot);
        lock (_lock)
        {
            _lastIndexedGeneration  = evt.Generation;
            _lastIndexedLogSequence = evt.NextLogSequence - 1;
        }
    }

    private void HandleDelta(DeltaCommittedEvent evt)
    {
        ApplyDeltaToIndex(evt.Delta);
        lock (_lock)
        {
            _lastIndexedGeneration  = evt.Generation;
            _lastIndexedLogSequence = evt.NextLogSequence - 1;
        }
    }

    private void HandleCompacted(CompactedEvent evt)
    {
        RebuildFromSnapshot(evt.Snapshot);
        lock (_lock)
        {
            _lastIndexedGeneration  = evt.Generation;
            _lastIndexedLogSequence = evt.NextLogSequence - 1;
        }
    }

    private void RebuildFromSnapshot(RepoViewSnapshot snapshot)
    {
        var newIndex = new Dictionary<HashKey, List<FileRecord>>();

        foreach (var file in snapshot.Files.Values)
        {
            if (file.Hash == HashKey.NotComputed || file.Hash == HashKey.CannotCompute)
                continue;

            if (!newIndex.TryGetValue(file.Hash, out var list))
            {
                list = new List<FileRecord>();
                newIndex[file.Hash] = list;
            }
            else // duplicate found
            {
                _totalDuplicateFileCount++;
                _totalSpaceTakenByDuplicates += file.Size;
            }

            list.Add(file);
        }

        lock (_lock)
        {
            _hashToFileRecords.Clear();
            foreach (var (hash, ids) in newIndex)
                _hashToFileRecords[hash] = ids;
        }
    }

    private void ApplyDeltaToIndex(RepoDelta delta)
    {
        lock (_lock)
        {
            // remove / tombstone old files
            foreach (var file in delta.Files.Where(f => f.Status == ScanEntryStatus.Deleted))
            {
                if (_hashToFileRecords.TryGetValue(file.Hash, out var list))
                {
                    list.Remove(file);
                    if (list.Count == 0)
                        _hashToFileRecords.Remove(file.Hash);
                }
            }

            // add / update files
            foreach (var file in delta.Files.Where(f => f.Status != ScanEntryStatus.Deleted))
            {
                if (!_hashToFileRecords.TryGetValue(file.Hash, out var list))
                {
                    list = new List<FileRecord>();
                    _hashToFileRecords[file.Hash] = list;
                }

                if (!list.Contains(file))
                    list.Add(file);
            }
        }
    }

    // Public query surface for DuplicatesView etc.
    public IReadOnlyList<IReadOnlyList<FileRecord>> GetDuplicateGroups(int minDuplicates = 2, long minSize = 1)
    {
        if (minDuplicates < 2) throw new ArgumentOutOfRangeException(nameof(minDuplicates));
        if (minSize < 1) throw new ArgumentOutOfRangeException(nameof(minSize));
        
        lock (_lock)
        {
            return _hashToFileRecords.Values
                .Where(list => list.Count >= minDuplicates && list[0].Size > minSize)
                .Select(list => (IReadOnlyList<FileRecord>)list.ToArray())
                .ToArray();
        }
    }

    public int TotalDuplicateFileCount => _totalDuplicateFileCount;

    public long TotalSpaceTakenByDuplicates => _totalSpaceTakenByDuplicates;
}