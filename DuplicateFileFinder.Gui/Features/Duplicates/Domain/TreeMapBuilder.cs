using Avalonia.Media;
using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Domain;

public static class TreeMapBuilder
{

    public static TreeMapNode<ITreeMapNodeElement>? Build(
        IRepoView snapshot,
        IEnumerable<ScanRoot> scanRoots,
        ITreeIndexReadModel treeIndex,
        TreeMapMetric metric,
        TreeMapBuildOptions opts,
        Func<long, string> dirRelativePathResolver)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scanRoots);
        ArgumentNullException.ThrowIfNull(treeIndex);
        ArgumentNullException.ThrowIfNull(dirRelativePathResolver);

        var liveRoots = GetLiveScanRoots(scanRoots, snapshot);

        var scanRootNodes = BuildScanRootNodes(
            snapshot,
            liveRoots,
            treeIndex,
            metric,
            opts,
            dirRelativePathResolver);

        if (scanRootNodes.Count == 0)
            return null;

        ApplyScanRootColours(scanRootNodes);

        return BuildDummyRoot(scanRootNodes, metric);
    }

    // ---------------------------------------------------------------------
    // Scan root handling
    // ---------------------------------------------------------------------

    private static List<ScanRoot> GetLiveScanRoots(IEnumerable<ScanRoot> scanRoots, IRepoView snapshot)
    {
        var list = new List<ScanRoot>();
        foreach (var r in scanRoots)
        {
            if (r.IsDeleted)
                continue;

            // if it’s not in snapshot, treat it as missing/stale and skip
            if (!snapshot.Dirs.ContainsKey(r.DirId))
                continue;

            list.Add(r);
        }

        return list;
    }
    
    private static List<TreeMapNode<ITreeMapNodeElement>> BuildScanRootNodes(
        IRepoView snapshot,
        IReadOnlyList<ScanRoot> liveRoots,
        ITreeIndexReadModel treeIndex,
        TreeMapMetric metric,
        TreeMapBuildOptions opts,
        Func<long, string> dirRelativePathResolver)
    {
        var nodes = new List<TreeMapNode<ITreeMapNodeElement>>(liveRoots.Count);

        foreach (var root in liveRoots)
        {
            // root.DirId is guaranteed live by GetLiveScanRoots
            var node = BuildDirNode(
                snapshot,
                treeIndex,
                metric,
                opts,
                dirRelativePathResolver,
                scanRoot: root,
                dirId: root.DirId,
                depth: 0);

            if (node.Element.Value > 0)
                nodes.Add(node);
        }

        return nodes;
    }

    private static void ApplyScanRootColours(IReadOnlyList<TreeMapNode<ITreeMapNodeElement>> scanRootNodes)
    {
        var palette = new[]
        {
            "#FF4E79A7",
            "#FF59A14F",
            "#FFEDC948",
            "#FFB07AA1",
            "#FF9C755F",
            "#FF76B7B2",
            "#FFE15759"
        };

        for (var i = 0; i < scanRootNodes.Count; i++)
            scanRootNodes[i].Fill = new SolidColorBrush(Color.Parse(palette[i % palette.Length]));
    }

    private static TreeMapNode<ITreeMapNodeElement> BuildDummyRoot(IReadOnlyList<TreeMapNode<ITreeMapNodeElement>> scanRootNodes, TreeMapMetric metric)
    {
        var total = scanRootNodes.Sum(n => n.Element.Value);

        // Keep the dummy tooltip minimal; you can enrich later if you want.
        var dummy = new SyntheticTreeMapElement(
            label: "All scan roots",
            value: total,
            typeLabel: "Directory",
            lines:
            [
                ("Metric", metric == TreeMapMetric.TotalBytes ? "Total size" : "Total files"),
                ("Total", FormatMetric(total, metric))
            ]);
        
        return new TreeMapNode<ITreeMapNodeElement>
        {
            Element = dummy,
            Children = scanRootNodes,
            Fill = null
        };
    }
    
    // ---------------------------------------------------------------------
    // Directory nodes
    // ---------------------------------------------------------------------

    private static TreeMapNode<ITreeMapNodeElement> BuildDirNode(
        IRepoView snapshot,
        ITreeIndexReadModel treeIndex,
        TreeMapMetric metric,
        TreeMapBuildOptions opts,
        Func<long, string> dirRelativePathResolver,
        ScanRoot scanRoot,
        long dirId,
        int depth)
    {
        if (!snapshot.Dirs.TryGetValue(dirId, out var dir))
            return BuildMissingDirNode(dirId);

        var dirStats = treeIndex.GetDirStats(dirId);
        var dirValue = GetDirMetricValue(dirStats, metric);

        var relativePath = SafeResolveRelativePath(dirRelativePathResolver, dirId);

        // Depth cap -> aggregated leaf dir node
        if (depth >= opts.MaxDepth)
            return MakeDirLeafNode(dir, scanRoot, dirStats, relativePath, dirValue);

        // Build children (subdirs + files + collapsed "Other")
        var children = new List<TreeMapNode<ITreeMapNodeElement>>();

        AddSubdirectoryNodes(
            snapshot,
            treeIndex,
            metric,
            opts,
            dirRelativePathResolver,
            scanRoot,
            dirId,
            depth,
            children);

        if (!opts.DirectoriesOnly && metric == TreeMapMetric.TotalBytes)
        {
            AddFileNodes(
                snapshot,
                treeIndex,
                metric,
                opts,
                scanRoot,
                dirId,
                relativePath,
                children);
        }

        var element = new DirTreeMapElement(
            dir: dir,
            scanRoot: scanRoot,
            dirAggregateStats: dirStats,
            relativePath: relativePath,
            value: dirValue);

        return new TreeMapNode<ITreeMapNodeElement>
        {
            Element = element,
            Children = children,
            Fill = null
        };
    }

    private static void AddSubdirectoryNodes(
        IRepoView snapshot,
        ITreeIndexReadModel treeIndex,
        TreeMapMetric metric,
        TreeMapBuildOptions opts,
        Func<long, string> dirRelativePathResolver,
        ScanRoot scanRoot,
        long parentDirId,
        int parentDepth,
        List<TreeMapNode<ITreeMapNodeElement>> childrenOut)
    {
        var candidates = GetChildDirCandidates(snapshot, treeIndex, metric, parentDirId);

        candidates.Sort((a, b) => b.Value.CompareTo(a.Value));

        double otherValue = 0;
        int otherCount = 0;

        for (var i = 0; i < candidates.Count; i++)
        {
            var (childId, v) = candidates[i];

            if (i < opts.MaxSubdirsPerDir)
            {
                childrenOut.Add(BuildDirNode(
                    snapshot,
                    treeIndex,
                    metric,
                    opts,
                    dirRelativePathResolver,
                    scanRoot,
                    childId,
                    parentDepth + 1));
            }
            else
            {
                otherValue += v;
                otherCount++;
            }
        }

        if (otherCount > 0 && otherValue > 0)
        {
            childrenOut.Add(new TreeMapNode<ITreeMapNodeElement>
            {
                Element = BuildSyntheticOtherDirs(otherCount, otherValue, metric),
                Children = [],
                Fill = null
            });
        }
    }

    private static List<(long DirId, double Value)> GetChildDirCandidates(
        IRepoView snapshot,
        ITreeIndexReadModel treeIndex,
        TreeMapMetric metric,
        long parentDirId)
    {
        var list = new List<(long, double)>();

        foreach (var childDirId in treeIndex.GetChildDirIds(parentDirId))
        {
            if (!snapshot.Dirs.ContainsKey(childDirId))
                continue;

            var stats = treeIndex.GetDirStats(childDirId);
            var v = GetDirMetricValue(stats, metric);
            if (v <= 0)
                continue;

            list.Add((childDirId, v));
        }

        return list;
    }

    private static TreeMapNode<ITreeMapNodeElement> MakeDirLeafNode(
        DirRecord dir,
        ScanRoot scanRoot,
        DirAggregateStats stats,
        string relativePath,
        double value)
    {
        var element = new DirTreeMapElement(dir, scanRoot, stats, relativePath, value);

        return new TreeMapNode<ITreeMapNodeElement>
        {
            Element = element,
            Children = [],
            Fill = null
        };
    }

    private static TreeMapNode<ITreeMapNodeElement> BuildMissingDirNode(long dirId)
    {
        var element = new SyntheticTreeMapElement(
            label: $"[missing:{dirId}]",
            value: 0,
            typeLabel: "Directory",
            lines: [("DirId", dirId.ToString())]);

        return new TreeMapNode<ITreeMapNodeElement>
        {
            Element = element,
            Children = [],
            Fill = null
        };
    }

    private static string SafeResolveRelativePath(Func<long, string> resolver, long dirId)
    {
        try
        {
            return resolver(dirId);
        }
        catch
        {
            return string.Empty;
        }
    }
    
    // ---------------------------------------------------------------------
    // File nodes
    // ---------------------------------------------------------------------

    private static void AddFileNodes(
        IRepoView snapshot,
        ITreeIndexReadModel treeIndex,
        TreeMapMetric metric,
        TreeMapBuildOptions opts,
        ScanRoot scanRoot,
        long dirId,
        string dirRelativePath,
        List<TreeMapNode<ITreeMapNodeElement>> childrenOut)
    {
        if (metric != TreeMapMetric.TotalBytes)
            return;

        var files = GetChildFiles(snapshot, treeIndex, dirId);

        // keep selection stable by size desc
        files.Sort((a, b) => b.Size.CompareTo(a.Size));

        double otherValue = 0;
        int otherCount = 0;

        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];

            if (i < opts.MaxFilesPerDir)
            {
                childrenOut.Add(BuildFileNode(scanRoot, dirRelativePath, f));
            }
            else
            {
                otherCount++;
                otherValue += f.Size;
            }
        }

        if (otherCount > 0 && otherValue > 0)
        {
            childrenOut.Add(new TreeMapNode<ITreeMapNodeElement>
            {
                Element = BuildSyntheticOtherFiles(otherCount, otherValue, metric),
                Children = [],
                Fill = null
            });
        }
    }

    private static List<FileRecord> GetChildFiles(IRepoView snapshot, ITreeIndexReadModel treeIndex, long dirId)
    {
        var list = new List<FileRecord>();

        foreach (var fileId in treeIndex.GetChildFileIds(dirId))
        {
            if (!snapshot.Files.TryGetValue(fileId, out var f))
                continue;
            if (f.Size <= 0)
                continue;

            list.Add(f);
        }

        return list;
    }

    private static TreeMapNode<ITreeMapNodeElement> BuildFileNode(
        ScanRoot scanRoot,
        string dirRelativePath,
        FileRecord f)
    {
        var element = new FileTreeMapElement(
            f,
            scanRoot,
            dirRelativePath);

        return new TreeMapNode<ITreeMapNodeElement>
        {
            Element = element,
            Children = [],
            Fill = null
        };
    }
    
    // ---------------------------------------------------------------------
    // Metric helpers / synthetic nodes
    // ---------------------------------------------------------------------

    private static double GetDirMetricValue(DirAggregateStats stats, TreeMapMetric metric)
        => metric == TreeMapMetric.TotalBytes ? stats.TotalBytes : stats.FileCount;

    private static ITreeMapNodeElement BuildSyntheticOtherDirs(int count, double value, TreeMapMetric metric)
    {
        return new SyntheticTreeMapElement(
            label: $"Other dirs ({count})",
            value: value,
            typeLabel: "Directory",
            lines:
            [
                ("Metric", metric == TreeMapMetric.TotalBytes ? "Total size" : "Total files"),
                ("Total", FormatMetric(value, metric))
            ]);
    }

    private static ITreeMapNodeElement BuildSyntheticOtherFiles(int count, double value, TreeMapMetric metric)
    {
        return new SyntheticTreeMapElement(
            label: $"Other files ({count})",
            value: value,
            typeLabel: "File",
            lines:
            [
                ("Metric", metric == TreeMapMetric.TotalBytes ? "Total size" : "Total files"),
                ("Total", FormatMetric(value, metric))
            ]);
    }

    private static string FormatMetric(double value, TreeMapMetric metric)
    {
        if (metric == TreeMapMetric.TotalBytes)
            return $"{(long)value:n0} bytes";

        return $"{(long)value:n0} files";
    }
}