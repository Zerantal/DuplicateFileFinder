using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Hash;

public sealed partial class HashIndexPlugin
{
    private sealed record StatsSnapshot(int DuplicateFileCount, long SpaceTakenByDuplicates)
    {
        public static readonly StatsSnapshot Empty = new(0, 0);
    }

    private readonly record struct RemovalPlan(int[] Counts, FileHandle[] Reps, int NewGroupCount, int NewTotalHandles);

    private struct GroupMeta
    {
        public int Count;
        public long FileSizeBytes;
        public FileHandle FirstFile;

        public int Offset;
        public int Cursor;
    }

    private sealed record MaterializeAndSaveEvent : RepoEvent;

    private static Dictionary<FileHandle, int> BuildGroupIndexByFileHandle(
        HashGroupDescriptor[] groups,
        FileHandle[] allFiles)
    {
        using var _ = TimingLog.StartPhase("HashIndex.BuildGroupIndexByFileHandle");
        var map = new Dictionary<FileHandle, int>(allFiles.Length);

        for (var g = 0; g < groups.Length; g++)
        {
            var d = groups[g];
            if (d.Count <= 0 || d.Offset < 0)
                continue;

            var end = d.Offset + d.Count;
            if ((uint)end > (uint)allFiles.Length)
                continue;

            for (var i = d.Offset; i < end; i++)
                map[allFiles[i]] = g;
        }

        return map;
    }

    private void EnsureGroupIndexBuilt()
    {
        if (_groupIndexByFileHandle.Count != 0 || _groups.Length == 0 || _allFiles.Length == 0)
            return;

        _groupIndexByFileHandle = BuildGroupIndexByFileHandle(_groups, _allFiles);
    }

    private bool ShouldMaterializeSortViewsImmediately()
    {
        return _groups.Length <= ImmediateSortMaterializationMaxGroupCount;
    }
}
