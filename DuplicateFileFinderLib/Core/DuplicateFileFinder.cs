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

    private readonly IRepo _repo;

    public DuplicateFileFinder(IRepo repo,
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

    [System.Obsolete("Legacy RootNode-based API. Use repo-based queries instead.")]
    public IReadOnlyList<string> SearchPaths
        => _root.SubFolders.Select(f => PathUtils.NormalizePath(f.Path)).ToArray();

    [System.Obsolete("Legacy RootNode-based API. Use repo-based queries instead.")]
    public int TotalFilesScanned => _root.SubFolders.Sum(l => l.AggregateFileCount);

    [System.Obsolete("Legacy RootNode-based API. Use repo-based queries instead.")]
    public long DuplicateSpaceBytes => ComputeDuplicateSpaceBytes();

    [System.Obsolete("Legacy RootNode-based API. Use repo-based queries instead.")]
    public int DuplicateFilesWastedCount => ComputeDuplicateWastedFileCount();

    // ------------ Public scanning API ----------------

    public async Task ScanLocationAsync(string location,
        IProgress<DuplicateFileFinderProgressReport>? progressIndicator = null,
        CancellationToken token = default)
    {
        location = PathUtils.NormalizePath(location);

        var throttledProgress = progressIndicator is null ? null : new ThrottledProgress(progressIndicator);

        var session = _repo.BeginScan(location);

        try
        {
            // 1) Build workspace for this scan only (no persistent RootNode updates)
            FolderNode scope;
            using (PhaseScope.Begin(ScanPhase.Preparing))
            using (TimingLog.Start(nameof(ScanPhase.Preparing)))
            {
                scope = new FolderNode(location);
            }

            // 2) Enumerate
            var tempSizes = new Dictionary<long, int>();
            using (PhaseScope.Begin(ScanPhase.Enumerating))
            using (TimingLog.StartPhase(ScanPhase.Enumerating))
            {
                await EnumeratePhaseAsync(scope, tempSizes, throttledProgress, session, token);
                TimingLog.Counter("folders", scope.AggregateFolderCount);
                TimingLog.Counter("files", scope.AggregateFileCount);
            }

            // 3) Hashing
            using (PhaseScope.Begin(ScanPhase.Hashing))
            using (TimingLog.StartPhase(ScanPhase.Hashing))
            {
                var totalToHash = CountHashTargets(scope, tempSizes);
                TimingLog.Counter("targets", totalToHash);
                await RunHashingAsync(scope, tempSizes, totalToHash, throttledProgress, session, token);
            }

            // 4) Grouping
            using (PhaseScope.Begin(ScanPhase.Grouping))
            using (TimingLog.StartPhase(ScanPhase.Grouping))
            {
                await RunGroupingAsync(scope, throttledProgress, token);
            }

            // No legacy commit/aggregate phases: repo is the source of truth now.
            await session.CompleteAsync(token);
            Report(throttledProgress, ScanPhase.Completed, "Finished Scanning", 1.0, running: false);
            _repo.CompactIfNeeded();
        }
        catch (OperationCanceledException)
        {
            await session.FailAsync("Scan cancelled.", true, token);
            throw;
        }
        catch (Exception ex)
        {
            await session.FailAsync(ex.Message, false, token);
            throw;
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    // ------------ Enumeration phase ----------------

    private async Task EnumeratePhaseAsync(
        FolderNode scope,
        Dictionary<long, int> tempSizes,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        IScanSession session,
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

                // Register this folder in the repo via path-based API.
                // ScanSession will ensure path→DirId is unique and will
                // handle parent creation if needed.
                session.ObserveDirectory(folder.Path, ScanEntryStatus.Enumerated);

                // 1) Seed tempSizes from existing files in this folder
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

    // ------------ Hashing phase ----------------
    
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
    
    private async Task RunHashingAsync(
        FolderNode scope,
        Dictionary<long, int> tempSizes,
        int totalToHash,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        IScanSession session,
        CancellationToken token)
    {
        Report(progress, ScanPhase.Hashing, "Computing checksums...");

        bool TargetPredicate(FileNode f)
        {
            return tempSizes.TryGetValue(f.Size, out var cnt) && cnt > 1 && f.ChecksumBytes == null;
        }

        long processed = 0;

        await _checksums.ComputeAsync(
            scope,
            TargetPredicate,
            fileNode =>
            {
                processed++;

                // Progress reporting
                var pct = totalToHash == 0
                    ? 1.0
                    : Math.Min(1.0, (double)processed / totalToHash);

                Report(progress, ScanPhase.Hashing,
                    $"File hashed: {fileNode.Path}",
                    pct,
                    processed: processed,
                    total: totalToHash);

                var hashBytes = fileNode.ChecksumBytes;
                if (hashBytes is { Length: > 0 })
                {
                    var hashKey = new HashKey(hashBytes);

                    session.ObserveFile(
                        fullFilePath: fileNode.Path,
                        size: fileNode.Size,
                        hash: hashKey,
                        modified: fileNode.ModifiedTimeUtc,
                        created: fileNode.CreationTimeUtc,
                        status: ScanEntryStatus.Enumerated | ScanEntryStatus.Hashed);
                }
            },
            token);
    }

    // ------------ Grouping phase ----------------
    
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

    [System.Obsolete("Legacy RootNode-based API. Use repo-based queries instead.")]
    public void ClearAllScans()
    {
        _root = new RootNode();
        _fileSizes.Clear();
        _grouping.Reset();
    }

    [System.Obsolete("Legacy RootNode-based API. Use repo-based queries instead.")]
    public void ExportToCsv(TextWriter writer)
    {
        _serializer.Export(_root, writer);
    }

    [System.Obsolete("Legacy RootNode-based API. Use repo-based queries instead.")]
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

    [System.Obsolete("Legacy RootNode-based API. Use repo-based queries instead.")]
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
            results.Add(new DuplicateFileRow
            {
                Path = f.Path,
                Size = f.Size,
                CreationTimeUtc = f.CreationTimeUtc,
                Checksum = f.ChecksumHex,
                Group = f.Group
            });

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