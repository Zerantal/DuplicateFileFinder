using System.Runtime.InteropServices;

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Hash;

public sealed partial class HashIndexPlugin
{
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
        // Pass 1: count per hash + record per-file size (no per-group allocations)
        var metaByHash = new Dictionary<HashKey, HashIndexPlugin.GroupMeta>(capacity: 1024);
        var totalHandles = 0;

        foreach (var (snapshot, index, file) in EnumerateEligibleFiles(repoSnapshot))
        {
            totalHandles++;

            var fh = new FileHandle(snapshot.ScanRootId, index);

            ref var meta = ref CollectionsMarshal.GetValueRefOrAddDefault(metaByHash, file.Hash, out var exists);

            if (!exists)
            {
                meta = new HashIndexPlugin.GroupMeta
                {
                    Count = 1,
                    FileSizeBytes = file.Size,
                    FirstFile = fh,
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

        // Pass 3: fill allFiles using per-hash cursors
        foreach (var (snapshot, index, file) in EnumerateEligibleFiles(repoSnapshot))
        {
            if (!metaByHash.TryGetValue(file.Hash, out var meta))
                continue;

            var writeIndex = meta.Offset + meta.Cursor;
            meta.Cursor++;
            metaByHash[file.Hash] = meta;

            allFiles[writeIndex] = new FileHandle(snapshot.ScanRootId, index);
        }

        PublishFullyMaterializedState(allFiles, groups);
    }

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
        var plan = BuildRemovalPlanExcludingScanRoot(oldGroups, oldAll, removedScanRootId);

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
        FillAllFilesFromPlanExcludingScanRoot(oldGroups, oldAll, plan, removedScanRootId, newAll);

        PublishFullyMaterializedState(newAll, newGroups);
    }

    private static HashIndexPlugin.RemovalPlan BuildRemovalPlanExcludingHandles(
        HashGroupDescriptor[] oldGroups,
        FileHandle[] oldAll,
        HashSet<FileHandle> removedHandles)
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
                if (removedHandles.Contains(fh))
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

        return new HashIndexPlugin.RemovalPlan(newCounts, newReps, newGroupCount, newTotalHandles);
    }

    private static HashIndexPlugin.RemovalPlan BuildRemovalPlanExcludingScanRoot(
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

        return new HashIndexPlugin.RemovalPlan(newCounts, newReps, newGroupCount, newTotalHandles);
    }

    private static void BuildGroupsFromPlan(
        HashGroupDescriptor[] oldGroups,
        HashIndexPlugin.RemovalPlan plan,
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


    private static void FillAllFilesFromPlanExcludingHandles(
        HashGroupDescriptor[] oldGroups,
        FileHandle[] oldAll,
        HashIndexPlugin.RemovalPlan plan,
        HashSet<FileHandle> removedHandles,
        FileHandle[] newAll)
    {
        var dstOffset = 0;

        for (var g = 0; g < oldGroups.Length; g++)
        {
            var newCount = plan.Counts[g];
            if (newCount <= 0)
                continue;

            var d = oldGroups[g];
            var end = d.Offset + d.Count;

            var wrote = 0;
            for (var i = d.Offset; i < end; i++)
            {
                var fh = oldAll[i];
                if (removedHandles.Contains(fh))
                    continue;

                newAll[dstOffset + wrote] = fh;
                wrote++;

                if (wrote == newCount)
                    break;
            }

            dstOffset += newCount;
        }
    }

    private static void FillAllFilesFromPlanExcludingScanRoot(
        HashGroupDescriptor[] oldGroups,
        FileHandle[] oldAll,
        HashIndexPlugin.RemovalPlan plan,
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

    private int RebuildExcludingRemovedHandles(ReadOnlySpan<FileHandle> removedHandles)
    {
        using var _ = TimingLog.StartPhase("HashIndex.RebuildExcludingRemovedHandles");

        var oldGroups = _groups;
        var oldAll = _allFiles;

        if (oldGroups.Length == 0 || oldAll.Length == 0 || removedHandles.Length == 0)
            return 0;

        var removedSet = new HashSet<FileHandle>(removedHandles.ToArray());
        var affectedHashes = new HashSet<HashKey>();

        for (var g = 0; g < oldGroups.Length; g++)
        {
            var d = oldGroups[g];
            if (d.Count <= 0 || d.Offset < 0)
                continue;

            var end = d.Offset + d.Count;
            if ((uint)end > (uint)oldAll.Length)
                continue;

            for (var i = d.Offset; i < end; i++)
            {
                if (!removedSet.Contains(oldAll[i]))
                    continue;

                affectedHashes.Add(d.Hash);
                break;
            }
        }

        var plan = BuildRemovalPlanExcludingHandles(oldGroups, oldAll, removedSet);

        if (plan.NewGroupCount == 0 || plan.NewTotalHandles == 0)
        {
            PublishEmpty();
            return affectedHashes.Count;
        }

        var newAll = new FileHandle[plan.NewTotalHandles];
        var newGroups = new HashGroupDescriptor[plan.NewGroupCount];

        BuildGroupsFromPlan(oldGroups, plan, newGroups);
        FillAllFilesFromPlanExcludingHandles(oldGroups, oldAll, plan, removedSet, newAll);

        var stats = HashIndexPlugin.ComputeStats(newGroups);

        _allFiles = newAll;
        _groups = newGroups;
        _stats = stats;
        _groupIndexByFileHandle = HashIndexPlugin.BuildGroupIndexByFileHandle(newGroups, newAll);
        _bySizeDesc = [];
        _byCountDesc = [];
        _sortViewsDirty = true;

        return affectedHashes.Count;
    }


    private int RebuildSingleGroupExcludingFile(int groupIndex, FileHandle removedFile)
    {
        using var _ = TimingLog.StartPhase("HashIndex.RebuildSingleGroupExcludingFile");

        var oldGroups = _groups;
        var oldAll = _allFiles;

        if ((uint)groupIndex >= (uint)oldGroups.Length)
            return 0;

        var target = oldGroups[groupIndex];
        if (target.Count <= 0 || target.Offset < 0)
            return 0;

        var end = target.Offset + target.Count;
        if ((uint)end > (uint)oldAll.Length)
            return 0;

        var survivors = new List<FileHandle>(target.Count - 1);
        for (var i = target.Offset; i < end; i++)
        {
            var fh = oldAll[i];
            if (fh.Equals(removedFile))
                continue;

            survivors.Add(fh);
        }

        if (survivors.Count == target.Count)
            return 0;

        if (survivors.Count < 2)
            return RebuildExcludingRemovedHandles([removedFile]);

        var newAll = new FileHandle[oldAll.Length - 1];
        var newGroups = (HashGroupDescriptor[])oldGroups.Clone();

        // Copy prefix
        if (target.Offset > 0)
            Array.Copy(oldAll, 0, newAll, 0, target.Offset);

        // Copy rewritten target group
        for (var i = 0; i < survivors.Count; i++)
            newAll[target.Offset + i] = survivors[i];

        // Copy suffix shifted left by one
        var oldSuffixStart = target.Offset + target.Count;
        var newSuffixStart = target.Offset + survivors.Count;
        var suffixLen = oldAll.Length - oldSuffixStart;
        if (suffixLen > 0)
            Array.Copy(oldAll, oldSuffixStart, newAll, newSuffixStart, suffixLen);

        // Update target descriptor
        newGroups[groupIndex] = new HashGroupDescriptor(
            Hash: target.Hash,
            FileSizeBytes: target.FileSizeBytes,
            Offset: target.Offset,
            Count: survivors.Count,
            FirstFile: survivors[0]);

        // Shift offsets of later groups by one
        for (var g = groupIndex + 1; g < newGroups.Length; g++)
        {
            var d = newGroups[g];
            newGroups[g] = new HashGroupDescriptor(
                Hash: d.Hash,
                FileSizeBytes: d.FileSizeBytes,
                Offset: d.Offset - 1,
                Count: d.Count,
                FirstFile: d.FirstFile);
        }

        var stats = HashIndexPlugin.ComputeStats(newGroups);

        _allFiles = newAll;
        _groups = newGroups;
        _stats = stats;
        _groupIndexByFileHandle = HashIndexPlugin.BuildGroupIndexByFileHandle(newGroups, newAll);
        _bySizeDesc = [];
        _byCountDesc = [];
        _sortViewsDirty = true;

        return 1;
    }
}
