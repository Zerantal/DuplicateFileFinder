// DuplicateFileFinderLib/Core/DuplicateFileFinder.cs
using DuplicateFileFinderLib.Grouping;
using DuplicateFileFinderLib.Indexing;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Scan;
using DuplicateFileFinderLib.Tree;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Core;

public enum ImportMode
{
    Merge,
    Replace
}

public sealed class DuplicateFileFinder
{
    private readonly IChecksumPipeline _checksums;
    private readonly Dictionary<long, int> _fileSizes = new(); // filesize => count
    private readonly IGroupingService _grouping;
    private readonly IScanSerializer _serializer;
    private readonly IScanStrategy _scanStrategy;

    private RootNode _root = new();

    private DuplicateFileFinder(
        IChecksumPipeline? checksums = null,
        IGroupingService? grouping = null,
        IScanSerializer? serializer = null,
        IScanStrategy? scanStrategy = null,
        IFileEnumerator? fsEnumerator = null
        )
    {
        _checksums = checksums ?? new ChecksumPipeline();
        _grouping = grouping ?? new FileSystemGroupsAdapter();
        _serializer = serializer ?? new CsvScanSerializer();
        
        _scanStrategy = scanStrategy ?? new DirectScanStrategy(fsEnumerator ?? new FileEnumerator());
        
    }

    public DuplicateFileFinder() : this(null)
    {
    }

    public IReadOnlyList<string> SearchPaths
        => _root.SubFolders.Select(f => PathUtils.NormalizePath(f.Path)).ToArray();

    public int TotalFilesScanned => _root.SubFolders.Sum(l => l.AggregateFileCount);
    public long DuplicateSpaceBytes => ComputeDuplicateSpaceBytes();
    public int DuplicateFilesWastedCount => ComputeDuplicateWastedFileCount();

    // ------------ Public scanning API ----------------

    public async Task ScanLocation(string location,
        IProgress<DuplicateFileFinderProgressReport>? progressIndicator = null,
        CancellationToken token = default)
    {
        location = PathUtils.NormalizePath(location);

        var throttledProgress = progressIndicator is null ? null : new ThrottledProgress(progressIndicator);

        // 1) Build workspace (transactional)
        FolderNode scope;
        RootNode workRoot;
        using (PhaseScope.Begin(ScanPhase.Preparing))
        using (TimingLog.Start(nameof(ScanPhase.Preparing)))
        {
            (workRoot, scope) = PrepareWorkspace(location);
        }

        // 2) Enumerate (indeterminate progress)
        Dictionary<long, int> tempSizes = new Dictionary<long, int>();
        using (PhaseScope.Begin(ScanPhase.Enumerating))
        using (TimingLog.StartPhase(ScanPhase.Enumerating))
        {
            await EnumeratePhaseAsync(scope, tempSizes, throttledProgress, token);
            TimingLog.Counter("folders", scope.AggregateFolderCount);
            TimingLog.Counter("files",   scope.AggregateFileCount);
        }
        
        // 3) Hashing (determinate)
        using (PhaseScope.Begin(ScanPhase.Hashing))
        using (TimingLog.StartPhase(ScanPhase.Hashing))
        {
            var totalToHash = CountHashTargets(scope, tempSizes);
            TimingLog.Counter("targets", totalToHash);
            await RunHashingAsync(scope, tempSizes, totalToHash, throttledProgress, token);
        }

        // 4) Grouping (determinate)
        using (PhaseScope.Begin(ScanPhase.Grouping))
        using (TimingLog.StartPhase(ScanPhase.Grouping))
        {
            await RunGroupingAsync(scope, throttledProgress, token);
        }

        // 5) Commit on success (atomic)
        using (PhaseScope.Begin(ScanPhase.Committing))
        using (TimingLog.StartPhase(ScanPhase.Committing))
        {
            CommitWorkspace(workRoot, scope, location);
        }

        // 6) Recompute aggregates & rebuild size index
        using (PhaseScope.Begin(ScanPhase.RecomputingAggregates))
        using (TimingLog.StartPhase(ScanPhase.RecomputingAggregates))
        {
            await _root.RecomputeSubtreeAggregatesAsync();
            RebuildFileSizesFromRoot();
        }

        Report(throttledProgress, ScanPhase.Completed, "Finished Scanning", 1.0, running: false);
    }

    private void RebuildFileSizesFromRoot()
    {
        _fileSizes.Clear();
        foreach (var top in _root.SubFolders)
            top.TraverseFolders(f =>
            {
                foreach (var file in f.Files)
                    _fileSizes[file.Size] = _fileSizes.TryGetValue(file.Size, out var n) ? n + 1 : 1;
                return Task.CompletedTask;
            }).Wait();
    }

    private void CommitWorkspace(RootNode workRoot, FolderNode scope, string location)
    {
        var hasDescendants = _root.SubFolders.Any(r => PathUtils.IsAncestorOfPath(location, r.Path));
        var sameOrAncestor = _root.SubFolders.FirstOrDefault(r =>
            PathUtils.IsSamePath(r.Path, location) || PathUtils.IsAncestorOfPath(r.Path, location));

        if (sameOrAncestor is not null)
        {
            // Replace that subtree with scanned clone
            var existing = _root.SubFolders.First(f => PathUtils.IsSamePath(f.Path, scope.Path));
            _root.RemoveChild(existing);
            _root.AddFileSystemNode(scope);
        }
        else if (hasDescendants)
        {
            // Whole promoted layout becomes new truth
            _root = workRoot;
        }
        else
        {
            // Independent new root
            _root.AddFileSystemNode(scope);
        }
    }

    private async Task RunGroupingAsync(
        FolderNode scope,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken token)
    {
        // Pre-count work units for a determinate bar (folders + files)
        long total = 0;
        await scope.TraverseFolders(f =>
        {
            token.ThrowIfCancellationRequested();
            total += 1; // folder
            total += f.Files.Count; // files
            return Task.CompletedTask;
        });

        // Initial report (0% or 100% if empty)
        Report(progress, ScanPhase.Grouping, "Grouping duplicates...",
            processed: 0,
            percent: total == 0 ? 1.0 : 0.0,
            total: total);
        
        await _grouping.AssignGroupsAsync(
            scope,
            processed =>
            {
                var done = Math.Min(processed, total);
                var pct = total == 0 ? 1.0 : Math.Min(1.0, (double)done / total);

                Report(progress, ScanPhase.Grouping, "Grouping duplicates...",
                    processed: done,
                    percent: pct,
                    total: total);
            },
            token);
    }


    private async Task RunHashingAsync(
        FolderNode scope,
        Dictionary<long, int> tempSizes,
        int totalToHash,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken token)
    {
        Report(progress, ScanPhase.Hashing, "Computing checksums...");
        
        await _checksums.ComputeAsync(
            scope,
            f => tempSizes.TryGetValue(f.Size, out var cnt) && cnt > 1 && f.ChecksumBytes == null,
            (processed, filename) =>
            {
                Report(progress, ScanPhase.Hashing, $"File hashed: {filename}",
                    totalToHash == 0 ? 1.0 : (double)processed / totalToHash,
                    processed: processed,
                    total: totalToHash);
            },
            token);
    }


    private static int CountHashTargets(FolderNode scope, Dictionary<long, int> tempSizes)
    {
        var totalToHash = 0;
        scope.TraverseFolders(f =>
        {
            foreach (var file in f.Files)
                if (tempSizes.TryGetValue(file.Size, out var cnt) && cnt > 1 && file.ChecksumBytes == null)
                    totalToHash++;
            return Task.CompletedTask;
        }).Wait();
        return totalToHash;
    }


    private async Task EnumeratePhaseAsync(
        FolderNode scope,
        Dictionary<long, int> tempSizes,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken token)
    {
        if (scope is null) throw new ArgumentNullException(nameof(scope));
        if (tempSizes is null) throw new ArgumentNullException(nameof(tempSizes));

        long foldersVisited = 0;

        await scope.TraverseFolders(
            async folder =>
            {
                token.ThrowIfCancellationRequested();

                foldersVisited++;
                Report(progress, ScanPhase.Enumerating, $"Scanning {folder.Path}",
                    indeterminate: true, processed: foldersVisited);

                // seed tempSizes from preexisting nodes
                foreach (var f in folder.Files)
                    tempSizes[f.Size] = tempSizes.TryGetValue(f.Size, out var n) ? n + 1 : 1;

                // sets to avoid re-adds; keep OrdinalIgnoreCase for NTFS/fuseblk
                var cmp = StringComparer.OrdinalIgnoreCase;
                var existingFiles = new HashSet<string>(folder.Files.Select(x => x.Path), cmp);
                var existingDirs = new HashSet<string>(folder.SubFolders.Select(x => x.Path), cmp);

                await foreach (var e in _scanStrategy.EnumerateChildrenAsync(folder.Path, token))
                {
                    token.ThrowIfCancellationRequested();

                    // full path once; avoid Path.Combine twice
                    var full = e.DirPath.Length == 0 ? e.Name : Path.Join(e.DirPath, e.Name);

                    // Directory or file? Your enumerator already knows; if not, use e.IsDirectory when you add it.
                    
                    if (e.IsDirectory)
                    {
                        if (existingDirs.Add(full))
                            folder.AddFileSystemNode(new FolderNode(full, e.CTimeUtc));
                    }
                    else
                    {
                        if (existingFiles.Add(full))
                        {
                            var fn = new FileNode(full, e.SizeBytes, e.CTimeUtc);
                            folder.AddFileSystemNode(fn);
                            tempSizes[fn.Size] = tempSizes.TryGetValue(fn.Size, out var n) ? n + 1 : 1;
                        }
                    }
                }
            },
            f =>
            {
                f.UpdateFolderStats();
                return Task.CompletedTask;
            });
    }
    
    private static FolderNode EnsureFolderNode(FolderNode ancestor, string targetFolderPath)
    {
        if (string.Equals(ancestor.Path, targetFolderPath, StringComparison.Ordinal)) return ancestor;

        var rel = Path.GetRelativePath(ancestor.Path, targetFolderPath);
        if (rel is "." or "") return ancestor;

        var parts = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var cursor = ancestor;
        var current = ancestor.Path;

        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            var existing = cursor.FindSubFolderByPath(current);
            if (existing is null)
            {
                existing = new FolderNode(current,  creationTimeUtc: default);
                cursor.AddFileSystemNode(existing);
            }
            cursor = existing;
        }
        return cursor;
    }


    private static void Report(
        IProgress<DuplicateFileFinderProgressReport>? progress,
        ScanPhase phase,
        string message,
        double percent = 0.0,
        bool indeterminate = false,
        long processed = 0,
        long total = 0,
        bool running = true)
    {
        progress?.Report(new DuplicateFileFinderProgressReport
        {
            Phase = phase,
            StatusMessage = message,
            PercentComplete = percent,
            IsIndeterminate = indeterminate,
            Processed = processed,
            Total = total,
            IsRunning = running
        });
    }

    private (RootNode workRoot, FolderNode scope) PrepareWorkspace(string location)
    {
        var existingAncestor = _root.SubFolders.FirstOrDefault(r =>
            PathUtils.IsSamePath(r.Path, location) || PathUtils.IsAncestorOfPath(r.Path, location));

        var hasDescendants = _root.SubFolders.Any(r => PathUtils.IsAncestorOfPath(location, r.Path));

        if (existingAncestor is not null)
        {
            // Clone only the affected subtree
            var workRoot = new RootNode();
            var scope = existingAncestor.DeepCloneSubtree();
            workRoot.AddFileSystemNode(scope);
            return (workRoot, scope);
        }

        if (hasDescendants)
        {
            // Clone roots, then promote inside workspace
            var workRoot = new RootNode();
            foreach (var r in _root.SubFolders)
                workRoot.AddFileSystemNode(r.DeepCloneSubtree());

            workRoot = TreePromoter.PromoteAncestor(workRoot, location);
            var scope = workRoot.SubFolders.First(r => PathUtils.IsSamePath(r.Path, location));
            return (workRoot, scope);
        }

        // Independent new root
        var wr = new RootNode();
        var sc = new FolderNode(location);
        wr.AddFileSystemNode(sc);
        return (wr, sc);
    }

    // ------------ CSV I/O ---------------

    public void ClearAllScans()
    {
        _root = new RootNode();
        _fileSizes.Clear();
        _grouping.Reset();
    }

    public void ExportToCsv(TextWriter writer)
    {
        _serializer.Export(_root, writer);
    }

    public void ImportFromCsv(TextReader reader, ImportMode mode = ImportMode.Merge)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        if (mode == ImportMode.Replace) ClearAllScans();

        _serializer.ImportInto(_root, reader);

        // recompute aggregates
        foreach (var top in _root.SubFolders)
            top.TraverseFolders(
                null,
                f =>
                {
                    f.UpdateFolderStats();
                    return Task.CompletedTask;
                }
            ).Wait();

        // rebuild _fileSizes
        _fileSizes.Clear();
        foreach (var file in EnumerateAllFiles())
            _fileSizes[file.Size] = _fileSizes.TryGetValue(file.Size, out var n) ? n + 1 : 1;
    }

    // ------------ Queries ----------------
    
    public async Task<IReadOnlyList<DuplicateFileRow>> GetDuplicateFileRowsAsync()
    {
        var results = new List<DuplicateFileRow>();
        var groups = new Dictionary<int, List<FileNode>>();

        foreach (var top in _root.SubFolders)
            await top.TraverseFolders(folder =>
            {
                foreach (var f in folder.Files)
                {
                    if (f.Group < 0) continue;
                    if (!groups.TryGetValue(f.Group, out var list))
                        groups[f.Group] = list = new List<FileNode>();
                    list.Add(f);
                }

                return Task.CompletedTask;
            });

        foreach (var kv in groups.Where(kv => kv.Value.Count > 1))
        foreach (var f in kv.Value)
        {
            results.Add(new DuplicateFileRow
            {
                Path = f.Path,
                Size = f.Size,
                CreationTimeUtc = f.CreationTime,
                Checksum = f.ChecksumHex,
                Group = f.Group
            });
        }

        return results;
    }

    private long ComputeDuplicateSpaceBytes()
    {
        var groups = new Dictionary<int, (long total, long rep, int count)>();
        foreach (var f in EnumerateAllFiles())
        {
            if (f.Group < 0) continue;
            var acc = groups.GetValueOrDefault(f.Group);
            acc.total += f.Size;
            acc.count++;
            if (acc.rep == 0) acc.rep = f.Size;
            groups[f.Group] = acc;
        }

        long wasted = 0;
        foreach (var a in groups.Values)
            if (a.count > 1)
                wasted += a.total - a.rep;
        return wasted;
    }

    private int ComputeDuplicateWastedFileCount()
    {
        var counts = new Dictionary<int, int>();
        foreach (var f in EnumerateAllFiles())
        {
            if (f.Group < 0) continue;
            counts[f.Group] = counts.TryGetValue(f.Group, out var n) ? n + 1 : 1;
        }

        var wasted = 0;
        foreach (var c in counts.Values)
            if (c > 1)
                wasted += c - 1;
        return wasted;
    }

    private IEnumerable<FileNode> EnumerateAllFiles()
    {
        foreach (var loc in _root.SubFolders)
        {
            var buf = new List<FileNode>();
            loc.TraverseFolders(f =>
            {
                buf.AddRange(f.Files);
                return Task.CompletedTask;
            }).Wait();
            foreach (var f in buf) yield return f;
        }
    }
}