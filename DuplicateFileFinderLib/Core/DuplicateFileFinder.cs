// DuplicateFileFinderLib/Core/DuplicateFileFinder.cs

using DuplicateFileFinderLib.FileSystem;
using DuplicateFileFinderLib.Grouping;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
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
    private readonly IFileEnumerator _fs;
    private readonly IGroupingService _grouping;
    private readonly IScanSerializer _serializer;

    private RootNode _root = new();
    
    private readonly Repo? _repo;
    private readonly RepoCompactionPolicy _compactPolicy = new();

    public DuplicateFileFinder(Repo? repo = null,
        IFileEnumerator? fs = null,
        IChecksumPipeline? checksums = null,
        IGroupingService? grouping = null,
        IScanSerializer? serializer = null)
    {
        _fs = fs ?? new FileEnumerator();
        _checksums = checksums ?? new ChecksumPipeline();
        _grouping = grouping ?? new FileSystemGroupsAdapter();
        _serializer = serializer ?? new CsvScanSerializer();
        _repo = repo;
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
        
        // 3.5) Persist to repo
        if (_repo is not null)
        {
            using var session = _repo.BeginScan(scanId: Environment.TickCount, rootPath: location);
            await PersistScopeToRepoAsync(session, scope, token);
            session.Commit();
            _repo.CompactIfNeeded(); // optional, or call at app-controlled cadence
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
    
    private static async Task PersistScopeToRepoAsync(ScanSession session, FolderNode scope, CancellationToken token)
    {
        await scope.TraverseFolders(folder =>
        {
            token.ThrowIfCancellationRequested();

            foreach (var f in folder.Files)
            {
                // Only persist files that were hashed in this run (or were already hashed)
                var hash = f.ChecksumBytes;
                if (hash is null || hash.Length == 0) continue;

                session.UpsertFile(
                    fullPath: f.Path,
                    size: f.Size,
                    hash: hash,
                    modified:  f.ModifiedTimeUtc,
                    created:  f.CreationTimeUtc
                );
            }
            return Task.CompletedTask;
        });
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
        long foldersVisited = 0;

        await scope.TraverseFolders(
            async folder =>
            {
                token.ThrowIfCancellationRequested();

                foldersVisited++;
                Report(progress, ScanPhase.Enumerating,
                    $"Scanning {folder.Path}",
                    indeterminate: true,
                    processed: foldersVisited);
                await Task.CompletedTask;

                // 1) Seed tempSizes from any existing (cloned/promoted) files
                foreach (var existingFile in folder.Files)
                    tempSizes[existingFile.Size] = tempSizes.TryGetValue(existingFile.Size, out var n) ? n + 1 : 1;

                // 2) Merge live FS enumeration with existing children (no re-adds)
                var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var existingDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var f in folder.Files) existingFiles.Add(f.Path);
                foreach (var d in folder.SubFolders) existingDirs.Add(d.Path);

                foreach (var e in _fs.EnumerateChildren(folder.Path, token))
                    if (e.IsDirectory)
                    {
                        if (existingDirs.Add(e.FullPath))
                            folder.AddFileSystemNode(new FolderNode(e.FullPath, e.CreationTimeUtc, e.ModifiedTimeUtc));
                    }
                    else
                    {
                        if (existingFiles.Add(e.FullPath))
                        {
                            var fn = new FileNode(e.FullPath, e.Length, e.CreationTimeUtc, e.ModifiedTimeUtc);
                            folder.AddFileSystemNode(fn);
                            tempSizes[fn.Size] = tempSizes.TryGetValue(fn.Size, out var n) ? n + 1 : 1;
                        }
                    }
            },
            f =>
            {
                f.UpdateFolderStats();
                return Task.CompletedTask;
            });
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
                CreationTimeUtc = f.CreationTimeUtc,
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
    
    public IReadOnlyList<(byte[] Hash, IReadOnlyList<Guid> FileIds)> GetRepoDuplicateSets(int minCount = 2)
    {
        if (_repo is null) return [];
        var results = new List<(byte[], IReadOnlyList<Guid>)>();
        foreach (var kv in _repo.HashIndex)
            if (kv.Value.Count >= minCount)
            {
                var hashBytes = new byte[16];
                HashKey.ToByteArray(kv.Key, hashBytes);
                results.Add((hashBytes, kv.Value.ToArray()));
            }

        return results;
    }
}