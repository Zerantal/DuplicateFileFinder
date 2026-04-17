using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Hash;

public partial class HashIndexPlugin
{
    // IHashIndexReadModel implementation
    public DuplicateGroupPage GetGroupsPage(in DuplicateQuery query, int offset, int count)
        => GetGroupsPageCore(in query, offset, count, hasFilter: false, default);

    public DuplicateGroupPage GetGroupsPage(in DuplicateQuery query, in SubtreeFilter filter, int offset, int count)
        => GetGroupsPageCore(in query, offset, count, hasFilter: true, in filter);

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

    public int TotalDuplicateFileCount => _stats.DuplicateFileCount;
    public long TotalSpaceTakenByDuplicates => _stats.SpaceTakenByDuplicates;



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

        EnsureSortedViews();
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
}
