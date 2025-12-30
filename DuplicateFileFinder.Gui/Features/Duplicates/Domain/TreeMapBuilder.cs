using System.Globalization;
using Avalonia.Media;
using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinder.Gui.Infrastructure.Converters;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Storage.Models;

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
        private int _remainingFileBudget;
        private readonly ITreeMapDataResolver _resolver;

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
            _remainingFileBudget = _opts.MaxTotalFileNodes;
            _resolver = new TreeMapDataResolver(snapshot, treeIndex, dirRelativePathResolver);
        }
    
        // ---------------------------------------------------------------------
        // Scan root handling
        // ---------------------------------------------------------------------

        public List<(ScanRoot scanRoot, DirHandle rootDir)> GetLiveScanRoots(IEnumerable<ScanRoot> scanRoots)
        {
            var list = new List<(ScanRoot, DirHandle)>();
            foreach (var r in scanRoots)
            {
                if (r.IsDeleted)
                    continue;

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
            double total = 0;
            for (int i = 0; i < scanRootNodes.Count; i++)
                total += scanRootNodes[i].Element.Value;

            var dummy = new SyntheticTreeMapElement(
                _resolver,
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
            // Resolve scan-root snapshot ONCE for this subtree.
            if (!_snapshot.Snapshots.TryGetValue(dir.ScanRootId, out var rootSnapshot))
                return BuildMissingDirNode(dir);

            var dirRec = rootSnapshot.Dirs[dir.Index];

            if (dirRec.Status == ScanEntryStatus.Deleted)
                return MakeDirLeafNode(dir, scanRoot, value: 0);

            var dirStats = _treeIndex.GetDirStats(dir);
            var dirValue = GetDirMetricValue(dirStats);

            // prune subtrees that contribute nothing for the selected metric.
            // DirAggregateStats are aggregate over subtree; value==0 implies no contributing descendants.
            if (dirValue <= 0)
                return MakeDirLeafNode(dir, scanRoot, value: 0);

            // Depth cap -> aggregated leaf dir node
            if (depth >= _opts.MaxDepth)
                return MakeDirLeafNode(dir, scanRoot, dirValue);

            // Build children (subdirs + files + collapsed "Other")
            var cap = _opts.MaxSubdirsPerDir + (_opts.DirectoriesOnly ? 0 : _opts.MaxFilesPerDir) + 2;
            if (cap < 8) cap = 8;

            // Build children (subdirs + files + collapsed "Other")
            var children = new List<TreeMapNode<ITreeMapNodeElement>>(cap);

            AddSubdirectoryNodes(scanRoot, dir, depth, children);

            // Only add file nodes when showing bytes, and not for directory file counts
            if (!_opts.DirectoriesOnly && _metric == TreeMapMetric.TotalBytes)
                AddFileNodes(scanRoot, rootSnapshot, dir, children);

            var element = new DirTreeMapElement(
                _resolver,
                dir,
                scanRoot,
                dirValue);

            return new TreeMapNode<ITreeMapNodeElement>
            {
                Element = element,
                Children = children,
                Fill = null
            };
        }

        private void AddSubdirectoryNodes(
            ScanRoot scanRoot,
            DirHandle parentDir,
            int parentDepth,
            List<TreeMapNode<ITreeMapNodeElement>> childrenOut)
        {
            var k = _opts.MaxSubdirsPerDir;
            if (k <= 0) return;

            // Top-K selection without sorting all children.
            // Keep a min-heap of the top K by Value.
            var pq = new PriorityQueue<(DirHandle Dir, double Value), double>();

            double otherValue = 0;
            int otherCount = 0;

            var children = _treeIndex.GetChildDirs(parentDir);
            for (int i = 0; i < children.Length; i++)
            {
                var child = children[i];

                // Never include deleted directories in the treemap.
                try
                {
                    var childRec = _snapshot.GetDirRecord(child);
                    if (childRec.Status == ScanEntryStatus.Deleted)
                        continue;
                }
                catch
                {
                    continue;
                }

                DirAggregateStats stats;
                try { stats = _treeIndex.GetDirStats(child); }
                catch { continue; }

                var v = GetDirMetricValue(stats);
                if (v <= 0) continue;

                if (pq.Count < k)
                {
                    pq.Enqueue((child, v), v);
                }
                else
                {
                    pq.TryPeek(out _, out var smallest);
                    if (v > smallest)
                    {
                        var evicted = pq.Dequeue();
                        otherValue += evicted.Value;
                        otherCount++;
                        pq.Enqueue((child, v), v);
                    }
                    else
                    {
                        otherValue += v;
                        otherCount++;
                    }
                }
            }

            // Extract kept, sort by value desc.
            if (pq.Count > 0)
            {
                var kept = new List<(DirHandle Dir, double Value)>(pq.Count);
                while (pq.TryDequeue(out var item, out _))
                    kept.Add(item);

                kept.Sort((a, b) => b.Value.CompareTo(a.Value));
            
                for (int i = 0; i < kept.Count; i++)
                    childrenOut.Add(BuildDirNode(scanRoot, kept[i].Dir, parentDepth + 1));
            }

            // "Other" collapsed node
            if (otherCount > 0 && otherValue > 0)
                childrenOut.Add(new TreeMapNode<ITreeMapNodeElement>
                {
                    Element = BuildSyntheticOtherDirs(otherCount, otherValue),
                    Children = [],
                    Fill = null
                });
        }
 
        // ---------------------------------------------------------------------
        // File nodes
        // ---------------------------------------------------------------------

        private void AddFileNodes(
            ScanRoot scanRoot,
            ScanRootSnapshotView rootSnapshot,
            DirHandle dir,
            List<TreeMapNode<ITreeMapNodeElement>> childrenOut)
        {
            int n = _opts.MaxFilesPerDir;
            if (n <= 0) return;

            // min-heap keyed by size (smallest at top)
            var pq = new PriorityQueue<(FileHandle File, long Size), long>();

            long otherValue = 0;
            int otherCount = 0;

            var files = rootSnapshot.Files;
            var childFiles = _treeIndex.GetChildFiles(dir);

            for (int i = 0; i < childFiles.Length; i++)
            {
                var fh = childFiles[i];

                FileRecordV2 f;
                try { f = files[fh.Index]; }
                catch { continue; }

                if (f.Status == ScanEntryStatus.Deleted) continue;

                var size = f.Size;
                if (size <= 0) continue;

                if (pq.Count < n)
                {
                    pq.Enqueue((fh, size), size);
                }
                else
                {
                    pq.TryPeek(out _, out var smallestSize);
                    if (size > smallestSize)
                    {
                        var evicted = pq.Dequeue();
                        otherValue += evicted.Size;
                        otherCount++;
                        pq.Enqueue((fh, size), size);
                    }
                    else
                    {
                        otherValue += size;
                        otherCount++;
                    }
                }
            }

            if (pq.Count > 0)
            {
                // Extract kept and sort by size descending without re-reading snapshot records.
                var kept = new List<(FileHandle File, long Size)>(pq.Count);
                while (pq.TryDequeue(out var item, out _))
                    kept.Add(item);

                kept.Sort((a, b) => b.Size.CompareTo(a.Size));

                foreach (var (fh, size) in kept)
                {
                    if (Interlocked.Decrement(ref _remainingFileBudget) < 0)
                    {
                        otherCount++;
                        otherValue += size;
                        break;
                    }

                    childrenOut.Add(BuildFileNode(scanRoot, rootSnapshot, fh));
                }
            }

            if (otherCount > 0 && otherValue > 0)
            {
                childrenOut.Add(new TreeMapNode<ITreeMapNodeElement>
                {
                    Element = BuildSyntheticOtherFiles(otherCount, otherValue),
                    Children = [],
                    Fill = null
                });
            }
        }

        private TreeMapNode<ITreeMapNodeElement> BuildFileNode(
            ScanRoot scanRoot,
            ScanRootSnapshotView rootSnapshot,
            FileHandle fh)
        {
            var f = rootSnapshot.Files[fh.Index];

            var element = new FileTreeMapElement(
                _resolver,
                fh,
                scanRoot,
                value: f.Size);

            return new TreeMapNode<ITreeMapNodeElement>
            {
                Element = element,
                Children = [],
                Fill = null
            };
        }
    
        private TreeMapNode<ITreeMapNodeElement> MakeDirLeafNode(
            DirHandle dir,
            ScanRoot scanRoot,
            double value)
        {
            var element = new DirTreeMapElement(
                _resolver,
                dir,
                scanRoot,
                value);

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
                _resolver,
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

        // ---------------------------------------------------------------------
        // Metric helpers / synthetic nodes
        // ---------------------------------------------------------------------

        private double GetDirMetricValue(DirAggregateStats stats)
            => _metric == TreeMapMetric.TotalBytes ? stats.TotalBytes : stats.FileCount;

        private ITreeMapNodeElement BuildSyntheticOtherDirs(int count, double value)
        {
            return new SyntheticTreeMapElement(
                _resolver,
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
                _resolver,
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