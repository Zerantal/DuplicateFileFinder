using System.Globalization;
using Avalonia.Media;
using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinder.Gui.Infrastructure.Converters;
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
        
        if (liveRoots.Count == 0) return null;

        var results = new TreeMapNode<ITreeMapNodeElement>?[liveRoots.Count];

        Parallel.For(
            0, liveRoots.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (int i) =>
            {
                var root = liveRoots[i];
                var node = ctx.BuildDirNode(root, root.DirId, depth: 0);
                results[i] = node.Element.Value > 0 ? node : null;
            });
        
        // Preserve original ordering of scan roots
        var scanRootNodes = new List<TreeMapNode<ITreeMapNodeElement>>(liveRoots.Count);
        foreach (var t in results)
            if (t is { } n) scanRootNodes.Add(n);

        if (scanRootNodes.Count == 0) return null;

        ctx.ApplyScanRootColours(scanRootNodes);
        return ctx.BuildDummyRoot(scanRootNodes);
    }

    private sealed class BuildContext
    {
        private readonly IReadOnlyDictionary<long, DirRecord> _dirs;
        private readonly IReadOnlyDictionary<long, FileRecord> _files;
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
            _treeIndex = treeIndex;
            _metric = metric;
            _opts = opts;
            _dirRelPath = dirRelativePathResolver;
            _dirs = snapshot.Dirs;
            _files = snapshot.Files;
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
                if (!_dirs.ContainsKey(r.DirId))
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

        internal TreeMapNode<ITreeMapNodeElement> BuildDirNode(ScanRoot scanRoot, long dirId, int depth)
        {
            if (!_dirs.TryGetValue(dirId, out var dir))
                return BuildMissingDirNode(dirId);

            var dirStats = _treeIndex.GetDirStats(dirId);
            var dirValue = GetDirMetricValue(dirStats);

            // Depth cap -> aggregated leaf dir node
            if (depth >= _opts.MaxDepth)
                return MakeDirLeafNode(dir, scanRoot, dirStats, dirValue);

            // Build children (subdirs + files + collapsed "Other")
            var children = new List<TreeMapNode<ITreeMapNodeElement>>();

            AddSubdirectoryNodes(scanRoot, dirId, depth, children);

            // Only add file nodes when showing bytes, and not for directory file counts
            if (!_opts.DirectoriesOnly && _metric == TreeMapMetric.TotalBytes)
                AddFileNodes(scanRoot, dirId, children);

            var element = new DirTreeMapElement(
                dir,
                scanRoot,
                dirStats,
                () => SafeResolveRelativePath(dirId),
                dirValue);

            return new TreeMapNode<ITreeMapNodeElement>
            {
                Element = element,
                Children = children,
                Fill = null
            };
        }

        private const int ParallelDepthCutoff = 5;
        private const int ParallelChildThreshold = 32;

        private void AddSubdirectoryNodes(
            ScanRoot scanRoot,
            long parentDirId,
            int parentDepth,
            List<TreeMapNode<ITreeMapNodeElement>> childrenOut)
        {
            var candidates = GetChildDirCandidates(parentDirId);
            candidates.Sort((a, b) => b.Value.CompareTo(a.Value));

            var take = Math.Min(_opts.MaxSubdirsPerDir, candidates.Count);
            if (take <= 0) return;

            // Build top-N child dir nodes (others become "Other")
            var childNodes = new TreeMapNode<ITreeMapNodeElement>[take];

            var shouldParallelize =
                parentDepth < ParallelDepthCutoff &&
                take >= ParallelChildThreshold;

            if (shouldParallelize)
            {
                Parallel.For(
                    0, take,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    i =>
                    {
                        var childId = candidates[i].DirId;
                        childNodes[i] = BuildDirNode(scanRoot, childId, parentDepth + 1);
                    });
            }
            else
            {
                for (var i = 0; i < take; i++)
                {
                    var childId = candidates[i].DirId;
                    childNodes[i] = BuildDirNode(scanRoot, childId, parentDepth + 1);
                }
            }

            for (var i = 0; i < take; i++)
                childrenOut.Add(childNodes[i]);

            // "Other"
            double otherValue = 0;
            var otherCount = candidates.Count - take;
            for (var i = take; i < candidates.Count; i++)
                otherValue += candidates[i].Value;

            if (otherCount > 0 && otherValue > 0)
                childrenOut.Add(new TreeMapNode<ITreeMapNodeElement>
                {
                    Element = BuildSyntheticOtherDirs(otherCount, otherValue),
                    Children = [],
                    Fill = null
                });
        }


        private List<(long DirId, double Value)> GetChildDirCandidates(long parentDirId)
        {
            var list = new List<(long, double)>();

            foreach (var childDirId in _treeIndex.GetChildDirIds(parentDirId))
            {
                if (!_dirs.ContainsKey(childDirId))
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
            double value)
        {
            var element = new DirTreeMapElement(
                dir,
                scanRoot,
                stats,
                () => SafeResolveRelativePath(dir.DirId),
                value);

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
            List<TreeMapNode<ITreeMapNodeElement>> childrenOut)
        {
            int n = _opts.MaxFilesPerDir;
            if (n <= 0) return;

            // min-heap keyed by size (smallest at top)
            var pq = new PriorityQueue<FileRecord, long>();

            long otherValue = 0;
            int otherCount = 0;

            foreach (var fileId in _treeIndex.GetChildFileIds(dirId))
            {
                if (!_files.TryGetValue(fileId, out var f))
                    continue;

                long size = f.Size;
                if (size <= 0) continue;

                if (pq.Count < n)
                {
                    pq.Enqueue(f, size);
                }
                else
                {
                    pq.TryPeek(out _, out var smallestSize);

                    if (size > smallestSize)
                    {
                        pq.Dequeue();
                        // the evicted one becomes “other”
                        otherValue += smallestSize;
                        otherCount++;

                        pq.Enqueue(f, size);
                    }
                    else
                    {
                        otherValue += size;
                        otherCount++;
                    }
                }
            }

            // Extract top N (heap gives smallest first, so reverse)
            var kept = new List<FileRecord>(pq.Count);
            while (pq.TryDequeue(out var f, out _))
                kept.Add(f);
            kept.Sort(static (a, b) => b.Size.CompareTo(a.Size)); // sort only <= N items

            foreach (var f in kept)
                childrenOut.Add(BuildFileNode(scanRoot, f));

            if (otherCount > 0 && otherValue > 0)
                childrenOut.Add(new TreeMapNode<ITreeMapNodeElement>
                {
                    Element = BuildSyntheticOtherFiles(otherCount, otherValue),
                    Children = [],
                    Fill = null
                });
        }

        private TreeMapNode<ITreeMapNodeElement> BuildFileNode(
            ScanRoot scanRoot,
            FileRecord f)
        {
            var element = new FileTreeMapElement(f, scanRoot, () => SafeResolveRelativePath(f.DirId));

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
                    ("Total", FormatMetric(value))
                ]);
        }

        private string FormatMetric(double value)
        {
            if (_metric == TreeMapMetric.TotalBytes)
            {
                var bytesFormated = (string?)BytesToHumanConverter.Instance.Convert(
                        value,
                        typeof(string),
                        null,
                        CultureInfo.CurrentUICulture) ?? $"{(long)value:n0} bytes";
                return bytesFormated;
            }

            return $"{(long)value:n0} files";
        }
    }
}