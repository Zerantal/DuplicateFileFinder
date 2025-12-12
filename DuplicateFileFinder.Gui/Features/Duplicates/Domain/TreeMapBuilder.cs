using Avalonia.Media;
using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Domain;

public static class TreeMapBuilder
{
    private static bool IsLive(ScanEntryStatus s)
    {
        return s is not (ScanEntryStatus.Deleted or ScanEntryStatus.None);
    }

    public static TreeMapNode? Build(
        IRepoView snapshot,
        IEnumerable<ScanRoot> scanRoots,
        ITreeIndexReadModel treeIndex,
        TreeMapMetric metric,
        TreeMapBuildOptions opts)
    {
        long GetDirMetricValue(long dirId)
        {
            var stats = treeIndex.GetDirStats(dirId);
            return metric == TreeMapMetric.TotalBytes
                ? stats.TotalBytes
                : stats.FileCount;
        }

        TreeMapNode BuildDirNode(long dirId, int depth)
        {
            if (!snapshot.Dirs.TryGetValue(dirId, out var dir) || !IsLive(dir.Status))
                return new TreeMapNode
                {
                    Label = $"[missing:{dirId}]",
                    IsDirectory = true,
                    Value = 0,
                    Children = [],
                    Fill = null
                };

            var dirValue = GetDirMetricValue(dirId);

            // Depth cap: aggregated leaf (Value is already subtree total)
            if (depth >= opts.MaxDepth)
                return new TreeMapNode
                {
                    Label = dir.Name,
                    IsDirectory = true,
                    Value = dirValue,
                    Children = [],
                    Fill = null
                };

            var children = new List<TreeMapNode>();

            // ---- Subdirectories (top N by selected metric) ----
            var subdirs = new List<(long Id, long Value)>();
            foreach (var childDirId in treeIndex.GetChildDirIds(dirId))
            {
                if (!snapshot.Dirs.TryGetValue(childDirId, out var childDir) || !IsLive(childDir.Status))
                    continue;

                var v = GetDirMetricValue(childDirId);
                if (v <= 0) continue;

                subdirs.Add((childDirId, v));
            }

            subdirs.Sort((a, b) => b.Value.CompareTo(a.Value));

            long otherDirsValue = 0;
            var otherDirsCount = 0;

            for (var i = 0; i < subdirs.Count; i++)
            {
                var (childId, v) = subdirs[i];

                if (i < opts.MaxSubdirsPerDir)
                {
                    children.Add(BuildDirNode(childId, depth + 1));
                }
                else
                {
                    otherDirsValue += v;
                    otherDirsCount++;
                }
            }

            if (otherDirsCount > 0 && otherDirsValue > 0)
                children.Add(new TreeMapNode
                {
                    Label = $"Other dirs ({otherDirsCount})",
                    IsDirectory = true,
                    Value = otherDirsValue,
                    Children = [],
                    Fill = null
                });

            // ---- Files (top M by size; value depends on metric) ----
            if (!opts.DirectoriesOnly)
            {
                var files = new List<FileRecord>();

                foreach (var fileId in treeIndex.GetChildFileIds(dirId))
                {
                    if (!snapshot.Files.TryGetValue(fileId, out var f))
                        continue;
                    if (!IsLive(f.Status))
                        continue;
                    if (f.Size <= 0)
                        continue;

                    files.Add(f);
                }

                // stable selection: by size
                files.Sort((a, b) => b.Size.CompareTo(a.Size));

                long otherFilesValue = 0;
                var otherFilesCount = 0;

                for (var i = 0; i < files.Count; i++)
                {
                    var f = files[i];

                    if (i < opts.MaxFilesPerDir)
                    {
                        children.Add(new TreeMapNode
                        {
                            Label = f.Name,
                            IsDirectory = false,
                            Value = metric == TreeMapMetric.TotalBytes ? f.Size : 1,
                            Children = [],
                            Fill = null
                        });
                    }
                    else
                    {
                        otherFilesCount++;
                        otherFilesValue += metric == TreeMapMetric.TotalBytes ? f.Size : 1;
                    }
                }

                if (otherFilesCount > 0 && otherFilesValue > 0)
                    children.Add(new TreeMapNode
                    {
                        Label = $"Other files ({otherFilesCount})",
                        IsDirectory = false,
                        Value = otherFilesValue,
                        Children = [],
                        Fill = null
                    });
            }

            return new TreeMapNode
            {
                Label = dir.Name,
                IsDirectory = true,
                Value = dirValue, // pre-summed subtree total
                Children = children,
                Fill = null
            };
        }

        var scanRootNodes = new List<TreeMapNode>();

        foreach (var scanRoot in scanRoots.Where(r => !r.IsDeleted))
        {
            if (!snapshot.Dirs.TryGetValue(scanRoot.DirId, out var rootDir))
                continue;
            if (!IsLive(rootDir.Status))
                continue;

            var node = BuildDirNode(scanRoot.DirId, 0);
            if (node.Value > 0)
                scanRootNodes.Add(node);
        }

        if (scanRootNodes.Count == 0)
            return null;

        // Colour each scan-root differently
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

        return new TreeMapNode
        {
            Label = "All scan roots",
            IsDirectory = true,
            Value = scanRootNodes.Sum(n => n.Value),
            Children = scanRootNodes,
            Fill = null
        };
    }
}