// DuplicateFileFinderLib/Core/DuplicateFileFinder.cs

using DuplicateFileFinderLib.FileSystem;
using DuplicateFileFinderLib.Grouping;
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
    private readonly IFileEnumerator _fs;

    private readonly IRepo _repo;
    
    private readonly bool _throttleProgress = true;

    public DuplicateFileFinder(IRepo repo,
        IFileEnumerator? fs = null,
        IChecksumPipeline? checksums = null)
    {
        _fs = fs ?? new FileEnumerator();
        _checksums = checksums ?? new ChecksumPipeline();
        _repo = repo;
    }
    
    internal DuplicateFileFinder(IRepo repo,
        bool throttleProgress) : this(repo)
    {
        _throttleProgress = throttleProgress;
    }

    // ------------ Public scanning API ----------------

    public async Task ScanLocationAsync(string location,
        IProgress<DuplicateFileFinderProgressReport>? progressIndicator = null,
        CancellationToken token = default)
    {
        location = PathUtils.NormalizePath(location);

        IProgress<DuplicateFileFinderProgressReport>? progress = progressIndicator;
        
        if (_throttleProgress)
            progress = progress is null ? null : new ThrottledProgress(progress);
        

        var session = _repo.BeginScan(location);

        try
        {
            // 0.5) Bail on error reading scan location
            if (!Directory.Exists(location))
            {
                string msg = $"Root scan path does not exist: {location}";
                throw new DirectoryNotFoundException(msg);
            }
            
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
                await EnumeratePhaseAsync(scope, tempSizes, progress, session, token);
                TimingLog.Counter("folders", scope.AggregateFolderCount);
                TimingLog.Counter("files", scope.AggregateFileCount);
            }

            // 3) Hashing
            using (PhaseScope.Begin(ScanPhase.Hashing))
            using (TimingLog.StartPhase(ScanPhase.Hashing))
            {
                var totalToHash = CountHashTargets(scope, tempSizes);
                TimingLog.Counter("targets", totalToHash);
                await RunHashingAsync(scope, tempSizes, totalToHash, progress, session, token);
            }

            // No legacy commit/aggregate phases: repo is the source of truth now.
            await session.CompleteAsync(token);
            Report(progress, ScanPhase.Completed, "Finished Scanning", 1.0, running: false);
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
                session.AddOrUpdateDirectory(folder.Path);
                
                // session.ObserveDirectory(folder.Path, ScanEntryStatus.Enumerated);

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
                        session.AddOrUpdateFile(
                            fullFilePath: e.FullPath,
                            size: e.Length,
                            hash: HashKey.NotComputed, 
                            modified: e.ModifiedTimeUtc,
                            created: e.CreationTimeUtc,
                            status: ScanEntryStatus.Enumerated);
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

                    session.AddOrUpdateFile(
                        fullFilePath: fileNode.Path,
                        hash: hashKey,
                        status: ScanEntryStatus.Hashed);
                }
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
}