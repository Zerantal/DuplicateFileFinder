using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage;

namespace DuplicateFileFinderLib.Repository.Plugins.Hash;

public sealed partial class HashIndexPlugin
{
    private string GetStateFilePath() => Path.Combine(_dataDirectory, StateFileName);

    private void SaveState(bool materializeSortViews)
    {
        if (materializeSortViews)
            EnsureSortedViews();

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

    private void SaveState() => SaveState(materializeSortViews: true);

    private bool TryLoadState(long expectedGeneration)
    {
        var path = GetStateFilePath();
        if (!File.Exists(path))
            return false;

        HashIndexState? state;

        using (TimingLog.StartPhase("Deserialising HashIndexState"))
        {
            if (!MemoryPackFile.TryLoadMapped(path, out state, CancellationToken.None) || state == null)
                return false;
        }

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

        var sortViewsValid = bySize.Length == groups.Length && byCount.Length == groups.Length;
        if (!sortViewsValid)
        {
            bySize = [];
            byCount = [];
        }

        var stats = new StatsSnapshot(state.TotalDuplicateFileCount, state.TotalSpaceTakenByDuplicates);

        Publish(allFiles, groups, bySize, byCount, stats);
        _sortViewsDirty = !sortViewsValid;

        // Transient delete-helper structure is built lazily on first single-file delete event.
        _groupIndexByFileHandle = new Dictionary<FileHandle, int>();

        _lastIndexedGeneration = state.LastIndexedGeneration;
        return true;
    }

    private void PublishEmpty()
    {
        Publish([], [], [], [], StatsSnapshot.Empty);
        _groupIndexByFileHandle = new Dictionary<FileHandle, int>();
        _sortViewsDirty = false;
    }

    private void PublishFullyMaterializedState(FileHandle[] allFiles, HashGroupDescriptor[] groups)
    {
        var (bySize, byCount) = BuildSortedViews(groups);
        var stats = ComputeStats(groups);
        Publish(allFiles, groups, bySize, byCount, stats);
        _groupIndexByFileHandle = HashIndexPlugin.BuildGroupIndexByFileHandle(groups, allFiles);
        _sortViewsDirty = false;
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
}
