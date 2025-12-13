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

        var ctx = new BuildContext(snapshot, treeIndex, metric, opts, dirRelativePathResolver);

        var liveRoots = ctx.GetLiveScanRoots(scanRoots);
        var scanRootNodes = ctx.BuildScanRootNodes(liveRoots);

        if (scanRootNodes.Count == 0)
            return null;

        ctx.ApplyScanRootColours(scanRootNodes);
        return ctx.BuildDummyRoot(scanRootNodes);
    }

    private sealed class BuildContext
    {
        private readonly IRepoView _snapshot;
        private readonly ITreeIndexReadModel _treeIndex;
        private readonly TreeMapMetric _metric;
        private readonly TreeMapBuildOptions _opts;
        private readonly Func<long, string> _dirRelPath;

        public BuildContext(
            IRepoView snapshot,
            ITreeIndexReadModel treeIndex,
            TreeMapMetric metric,
            TreeMapBuildOptions opts,
            Func<long, string> dirRelativePathResolver)
        {
            _snapshot = snapshot;
            _treeIndex = treeIndex;
            _metric = metric;
            _opts = opts;
            _dirRelPath = dirRelativePathResolver;
        }
    
        // ---------------------------------------------------------------------
        // Scan root handling
        // ---------------------------------------------------------------------

        public List<ScanRoot> GetLiveScanRoots(IEnumerable<ScanRoot> scanRoots)
        {
            var list = new List<ScanRoot>();
            foreach (var r in scanRoots)
            {
                if (r.IsDeleted)
                    continue;

                // if it’s not in snapshot, treat it as missing/stale and skip
                if (!_snapshot.Dirs.ContainsKey(r.DirId))
                    continue;

                list.Add(r);
            }
            return list;
        }
    
        public List<TreeMapNode<ITreeMapNodeElement>> BuildScanRootNodes(IReadOnlyList<ScanRoot> liveRoots)
        {
            var nodes = new List<TreeMapNode<ITreeMapNodeElement>>(liveRoots.Count);

            foreach (var root in liveRoots)
            {
                var node = BuildDirNode(root, root.DirId, depth: 0);
                if (node.Element.Value > 0)
                    nodes.Add(node);
            }

            return nodes;
        }

        public void ApplyScanRootColours(IReadOnlyList<TreeMapNode<ITreeMapNodeElement>> scanRootNodes)
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

        public TreeMapNode<ITreeMapNodeElement> BuildDummyRoot(IReadOnlyList<TreeMapNode<ITreeMapNodeElement>> scanRootNodes)
        {
            var total = scanRootNodes.Sum(n => n.Element.Value);

            var dummy = new SyntheticTreeMapElement(
                label: "All scan roots",
                value: total,
                typeLabel: "Directory",
                lines:
                [
                    ("Metric", _metric == TreeMapMetric.TotalBytes ? "Total size" : "Total files"),
                    ("Total", FormatMetric(total))
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

        private TreeMapNode<ITreeMapNodeElement> BuildDirNode(ScanRoot scanRoot, long dirId, int depth)
        {
            if (!_snapshot.Dirs.TryGetValue(dirId, out var dir))
                return BuildMissingDirNode(dirId);

            var dirStats = _treeIndex.GetDirStats(dirId);
            var dirValue = GetDirMetricValue(dirStats);

            var relPath = SafeResolveRelativePath(dirId);

            // Depth cap -> aggregated leaf dir node
            if (depth >= _opts.MaxDepth)
                return MakeDirLeafNode(dir, scanRoot, dirStats, relPath, dirValue);

            // Build children (subdirs + files + collapsed "Other")
            var children = new List<TreeMapNode<ITreeMapNodeElement>>();

            AddSubdirectoryNodes(scanRoot, dirId, depth, children);

            // Only add file nodes when showing bytes, and not for directory file counts
            if (!_opts.DirectoriesOnly && _metric == TreeMapMetric.TotalBytes)
                AddFileNodes(scanRoot, dirId, relPath, children);

            var element = new DirTreeMapElement(dir, scanRoot, dirStats, relPath, dirValue);

            return new TreeMapNode<ITreeMapNodeElement>
            {
                Element = element,
                Children = children,
                Fill = null
            };
        }

        private void AddSubdirectoryNodes(
            ScanRoot scanRoot,
            long parentDirId,
            int parentDepth,
            List<TreeMapNode<ITreeMapNodeElement>> childrenOut)
        {
            var candidates = GetChildDirCandidates(parentDirId);
            candidates.Sort((a, b) => b.Value.CompareTo(a.Value));

            double otherValue = 0;
            int otherCount = 0;

            for (var i = 0; i < candidates.Count; i++)
            {
                var (childId, v) = candidates[i];

                if (i < _opts.MaxSubdirsPerDir)
                {
                    childrenOut.Add(BuildDirNode(scanRoot, childId, parentDepth + 1));
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
                    Element = BuildSyntheticOtherDirs(otherCount, otherValue),
                    Children = [],
                    Fill = null
                });
            }
        }

        private List<(long DirId, double Value)> GetChildDirCandidates(long parentDirId)
        {
            var list = new List<(long, double)>();

            foreach (var childDirId in _treeIndex.GetChildDirIds(parentDirId))
            {
                if (!_snapshot.Dirs.ContainsKey(childDirId))
                    continue;

                var stats = _treeIndex.GetDirStats(childDirId);
                var v = GetDirMetricValue(stats);
                if (v <= 0)
                    continue;

                list.Add((childDirId, v));
            }

            return list;
        }

        private TreeMapNode<ITreeMapNodeElement> MakeDirLeafNode(
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

        private TreeMapNode<ITreeMapNodeElement> BuildMissingDirNode(long dirId)
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

        private string SafeResolveRelativePath(long dirId)
        {
            try
            {
                return _dirRelPath(dirId);
            }
            catch
            {
                return string.Empty;
            }
        }
    
        // ---------------------------------------------------------------------
        // File nodes
        // ---------------------------------------------------------------------

        private void AddFileNodes(
            ScanRoot scanRoot,
            long dirId,
            string dirRelativePath,
            List<TreeMapNode<ITreeMapNodeElement>> childrenOut)
        {
            var files = GetChildFiles(dirId);
            files.Sort((a, b) => b.Size.CompareTo(a.Size));

            double otherBytes = 0;
            int otherCount = 0;

            for (var i = 0; i < files.Count; i++)
            {
                var f = files[i];

                if (i < _opts.MaxFilesPerDir)
                {
                    childrenOut.Add(BuildFileNode(scanRoot, dirRelativePath, f));
                }
                else
                {
                    otherCount++;
                    otherBytes += f.Size;
                }
            }

            if (otherCount > 0 && otherBytes > 0)
            {
                childrenOut.Add(new TreeMapNode<ITreeMapNodeElement>
                {
                    Element = BuildSyntheticOtherFiles(otherCount, otherBytes),
                    Children = [],
                    Fill = null
                });
            }
        }

        private List<FileRecord> GetChildFiles(long dirId)
        {
            var list = new List<FileRecord>();

            foreach (var fileId in _treeIndex.GetChildFileIds(dirId))
            {
                if (!_snapshot.Files.TryGetValue(fileId, out var f))
                    continue;

                if (f.Size <= 0)
                    continue;

                list.Add(f);
            }

            return list;
        }

        private TreeMapNode<ITreeMapNodeElement> BuildFileNode(
            ScanRoot scanRoot,
            string dirRelativePath,
            FileRecord f)
        {
            var element = new FileTreeMapElement(f, scanRoot, dirRelativePath);

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

        private double GetDirMetricValue(DirAggregateStats stats)
            => _metric == TreeMapMetric.TotalBytes ? stats.TotalBytes : stats.FileCount;

        private ITreeMapNodeElement BuildSyntheticOtherDirs(int count, double value)
        {
            return new SyntheticTreeMapElement(
                label: $"Other dirs ({count})",
                value: value,
                typeLabel: "Directory",
                lines:
                [
                    ("Metric", _metric == TreeMapMetric.TotalBytes ? "Total size" : "Total files"),
                    ("Total", FormatMetric(value))
                ]);
        }

        private ITreeMapNodeElement BuildSyntheticOtherFiles(int count, double value)
        {
            return new SyntheticTreeMapElement(
                label: $"Other files ({count})",
                value: value,
                typeLabel: "File",
                lines:
                [
                    ("Metric", "Total size"),
                    ("Total", FormatMetric(value))
                ]);
        }

        private string FormatMetric(double value)
        {
            if (_metric == TreeMapMetric.TotalBytes)
                return $"{(long)value:n0} bytes";

            return $"{(long)value:n0} files";
        }
    }
}