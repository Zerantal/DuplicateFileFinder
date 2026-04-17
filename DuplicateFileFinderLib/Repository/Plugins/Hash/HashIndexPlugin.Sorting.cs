using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Hash;

public sealed partial class HashIndexPlugin
{
    private void EnsureSortedViews()
    {
        if (!_sortViewsDirty)
            return;

        var groups = _groups;
        var (bySize, byCount) = BuildSortedViews(groups);

        _bySizeDesc = bySize;
        _byCountDesc = byCount;
        _sortViewsDirty = false;
    }

    private static (int[] bySize, int[] byCount) BuildSortedViews(HashGroupDescriptor[] groups)
    {
        var bySize = BuildIndexArray(groups.Length);
        Array.Sort(bySize, new BySizeDescComparer(groups));

        var byCount = BuildIndexArray(groups.Length);
        Array.Sort(byCount, new ByCountDescComparer(groups));

        return (bySize, byCount);
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

            var c = TotalBytes(b).CompareTo(TotalBytes(a));
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

            c = TotalBytes(b).CompareTo(TotalBytes(a));
            if (c != 0) return c;

            return x.CompareTo(y);
        }
    }

    private void QueueDeferredSortMaterialization()
    {
        if (_deferredSortSaveQueued)
            return;

        _deferredSortSaveQueued = true;

        Post(new MaterializeAndSaveEvent { Generation = _lastIndexedGeneration });
    }

    private void PersistAfterMutation()
    {
        if (ShouldMaterializeSortViewsImmediately())
        {
            SaveState(materializeSortViews: true);
            return;
        }

        SaveState(materializeSortViews: false);
        QueueDeferredSortMaterialization();
    }


}
