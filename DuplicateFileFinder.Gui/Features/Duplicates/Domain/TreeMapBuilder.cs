using System.Globalization;
using Avalonia.Media;
using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinder.Gui.Infrastructure.Converters;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Domain;

public static class TreeMapBuilder
{
    public static TreeMapNode<ITreeMapNodeElement>? Build(
        RepoSnapshotView snapshot,
        IEnumerable<ScanRoot> scanRoots,
        ITreeIndexReadModel treeIndex,
        IFileDirReadModel fileDirIndex,
        TreeMapMetric metric,
        TreeMapBuildOptions opts,
        Func<long, string> dirRelativePathResolver)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scanRoots);
        ArgumentNullException.ThrowIfNull(treeIndex);
        ArgumentNullException.ThrowIfNull(fileDirIndex);
        ArgumentNullException.ThrowIfNull(dirRelativePathResolver);

        var ctx = new BuildContext(snapshot, treeIndex, fileDirIndex, metric, opts, dirRelativePathResolver);

        var liveRoots = ctx.GetLiveScanRoots(scanRoots);
        
        if (liveRoots.Count == 0) return null;

        var results = new TreeMapNode<ITreeMapNodeElement>?[liveRoots.Count];

        Parallel.For(
            0, liveRoots.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var (scanRoot, rootHandle) = liveRoots[i];
                var node = ctx.BuildDirNode(scanRoot, rootHandle, depth: 0);
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
        private readonly RepoSnapshotView _snapshot;
        private readonly ITreeIndexReadModel _treeIndex;
        private readonly IFileDirReadModel _fileDirIndex;
        private readonly TreeMapMetric _metric;
        private readonly TreeMapBuildOptions _opts;
        private readonly Func<long, string> _dirRelPath;

        public BuildContext(
            RepoSnapshotView snapshot,
            ITreeIndexReadModel treeIndex,
            IFileDirReadModel fileDirIndex,
            TreeMapMetric metric,
            TreeMapBuildOptions opts,
            Func<long, string> dirRelativePathResolver)
        {
            _snapshot = snapshot;
            _treeIndex = treeIndex;
            _fileDirIndex = fileDirIndex;
            _metric = metric;
            _opts = opts;
            _dirRelPath = dirRelativePathResolver;
        }
    
        // ---------------------------------------------------------------------
        // Scan root handling
        // ---------------------------------------------------------------------

        public List<(ScanRoot scanRoot, DirHandle rootDir)> GetLiveScanRoots(IEnumerable<ScanRoot> scanRoots)
        {
            var list = new List<(ScanRoot, DirHandle)>();
            foreach (var r in scanRoots.Where(r => !r.IsDeleted))
            {
                // Resolve r.DirId -> DirHandle; if missing, treat as stale and skip
                if (!_fileDirIndex.TryGetDir(r.DirId, out var rootHandle))
                    continue;
                
                // Also ensure the snapshot for that scan root exists
                if (!_snapshot.Snapshots.ContainsKey(rootHandle.ScanRootId))
                    continue;
                
                list.Add((r, rootHandle));
            }
            return list;
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

        internal TreeMapNode<ITreeMapNodeElement> BuildDirNode(ScanRoot scanRoot, DirHandle dir, int depth)
        {
            DirRecordV2 dirRec;
            try
            {
                dirRec = _snapshot.GetDir(dir);
            }
            catch
            {
                return BuildMissingDirNode(dir);
            }

            var dirStats = _treeIndex.GetDirStats(dir);
            var dirValue = GetDirMetricValue(dirStats);

            // Depth cap -> aggregated leaf dir node
            if (depth >= _opts.MaxDepth)
                return MakeDirLeafNode(dir, dirRec, scanRoot, dirStats, dirValue);

            // Build children (subdirs + files + collapsed "Other")
            var children = new List<TreeMapNode<ITreeMapNodeElement>>();

            AddSubdirectoryNodes(scanRoot, dir, depth, children);

            // Only add file nodes when showing bytes, and not for directory file counts
            if (!_opts.DirectoriesOnly && _metric == TreeMapMetric.TotalBytes)
                AddFileNodes(scanRoot, dir, children);

            var element = new DirTreeMapElement(
                dirRec,
                scanRoot,
                dirStats,
                () => SafeResolveRelativePath(dirRec.DirId),
                dirValue,
                () => _snapshot.DecodeDirName(dir));

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
            DirHandle parentDir,
            int parentDepth,
            List<TreeMapNode<ITreeMapNodeElement>> childrenOut)
        {
            var candidates = GetChildDirCandidates(parentDir);
            candidates.Sort((a, b) => b.Value.CompareTo(a.Value));

            var take = Math.Min(_opts.MaxSubdirsPerDir, candidates.Count);
            if (take <= 0) return;

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
                        var child = candidates[i].Dir;
                        childNodes[i] = BuildDirNode(scanRoot, child, parentDepth + 1);
                    });
            }
            else
            {
                for (var i = 0; i < take; i++)
                {
                    var child = candidates[i].Dir;
                    childNodes[i] = BuildDirNode(scanRoot, child, parentDepth + 1);
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

        private List<(DirHandle Dir, double Value)> GetChildDirCandidates(DirHandle parentDir)
        {
            var list = new List<(DirHandle, double)>();

            var children = _treeIndex.GetChildDirIds(parentDir);
            for (int i = 0; i < children.Length; i++)
            {
                var child = children[i];

                DirAggregateStats stats;
                try
                {
                    stats = _treeIndex.GetDirStats(child);
                }
                catch
                {
                    continue;
                }

                var v = GetDirMetricValue(stats);
                if (v <= 0)
                    continue;

                list.Add((child, v));
            }

            return list;
        }

        private TreeMapNode<ITreeMapNodeElement> MakeDirLeafNode(
            DirHandle dir,
            DirRecordV2 dirRec,
            ScanRoot scanRoot,
            DirAggregateStats stats,
            double value)
        {
            var element = new DirTreeMapElement(
                dirRec,
                scanRoot,
                stats,
                () => SafeResolveRelativePath(dirRec.DirId),
                value,
                () => _snapshot.DecodeDirName(dir));

            return new TreeMapNode<ITreeMapNodeElement>
            {
                Element = element,
                Children = [],
                Fill = null
            };
        }

        private TreeMapNode<ITreeMapNodeElement> BuildMissingDirNode(DirHandle dir)
        {
            var element = new SyntheticTreeMapElement(
                label: $"[missing:{dir.ScanRootId}:{dir.Index}]",
                value: 0,
                typeLabel: "Directory",
                lines:
                [
                    ("ScanRootId", dir.ScanRootId.ToString()),
                    ("Index", dir.Index.ToString())
                ]);

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
            DirHandle dir,
            List<TreeMapNode<ITreeMapNodeElement>> childrenOut)
        {
            int n = _opts.MaxFilesPerDir;
            if (n <= 0) return;

            // min-heap keyed by size (smallest at top)
            var pq = new PriorityQueue<FileHandle, long>();

            long otherValue = 0;
            int otherCount = 0;

            var childFiles = _treeIndex.GetChildFileIds(dir);
            for (int i = 0; i < childFiles.Length; i++)
            {
                var fh = childFiles[i];

                FileRecordV2 f;
                try
                {
                    f = _snapshot.GetFile(fh);
                }
                catch
                {
                    continue;
                }

                long size = f.Size;
                if (size <= 0) continue;

                if (pq.Count < n)
                {
                    pq.Enqueue(fh, size);
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
                        pq.Enqueue(fh, size);
                    }
                    else
                    {
                        otherValue += size;
                        otherCount++;
                    }
                }
            }

            // Extract top N (heap gives smallest first, so reverse)
            var kept = new List<FileHandle>(pq.Count);
            while (pq.TryDequeue(out var fh, out _))
                kept.Add(fh);

            kept.Sort((a, b) =>
            {
                var fa = _snapshot.GetFile(a);
                var fb = _snapshot.GetFile(b);
                return fb.Size.CompareTo(fa.Size);
            });

            foreach (var fh in kept)
                childrenOut.Add(BuildFileNode(scanRoot, fh));

            if (otherCount > 0 && otherValue > 0)
                childrenOut.Add(new TreeMapNode<ITreeMapNodeElement>
                {
                    Element = BuildSyntheticOtherFiles(otherCount, otherValue),
                    Children = [],
                    Fill = null
                });
        }

        private TreeMapNode<ITreeMapNodeElement> BuildFileNode(ScanRoot scanRoot, FileHandle fh)
        {
            var f = _snapshot.GetFile(fh);

            var element = new FileTreeMapElement(
                f,
                scanRoot,
                () => SafeResolveRelativePath(f.DirId),
                () => _snapshot.DecodeFileName(fh));

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
                var bytesFormatted = (string?)BytesToHumanConverter.Instance.Convert(
                        value,
                        typeof(string),
                        null,
                        CultureInfo.CurrentUICulture) ?? $"{(long)value:n0} bytes";
                return bytesFormatted;
            }

            return $"{(long)value:n0} files";
        }
    }
}