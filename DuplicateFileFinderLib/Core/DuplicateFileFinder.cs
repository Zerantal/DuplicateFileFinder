// DuplicateFileFinderLib/Core/DuplicateFileFinder.cs

using DuplicateFileFinderLib.FileSystem;
using DuplicateFileFinderLib.Hashing;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Core;

public enum ScanMode
{
    Full,   // Enumerate & compute hashes of all files/dirs in scan root           
    Quick   // Enumerate & compute hashes of files where a change has been detected
}

public sealed class DuplicateFileFinder
{
    private readonly IChecksumPipeline _checksums;
    private readonly IFileEnumerator _fs;

    private readonly IRepo _repo;
    
    private readonly bool _throttleProgress = true;
    private readonly int _hashDegreeOfParallelism;


    /// <summary>
    /// Internal representation of a file that needs hashing.
    /// </summary>
    private readonly record struct FileToHash(
        string         Path,
        long           Size,
        DateTimeOffset CreatedUtc,
        DateTimeOffset ModifiedUtc);

    public DuplicateFileFinder(
        IRepo repo,
        IFileEnumerator? fs = null,
        IChecksumPipeline? checksums = null,
        int? hashDegreeOfParallelism  = null)
    {
        _fs = fs ?? new FileEnumerator();
        _checksums = checksums ?? new ChecksumPipelineMD5();
        _repo = repo;
        
        var dop = hashDegreeOfParallelism ?? Environment.ProcessorCount;
        if (dop < 1) dop = 1;
        _hashDegreeOfParallelism = dop;

    }
    
    internal DuplicateFileFinder(IRepo repo, bool throttleProgress)
        : this(repo)
    {
        _throttleProgress = throttleProgress;
    }

    // ------------ Public scanning API ----------------

    public async Task ScanLocationAsync(
        string location,
        ScanMode mode = ScanMode.Full,
        IProgress<DuplicateFileFinderProgressReport>? progressIndicator = null,
        CancellationToken token = default)
    {
        location = PathUtils.NormalizePath(location);

        var progress = _throttleProgress && progressIndicator is not null
            ? new ThrottledProgress(progressIndicator)
            : progressIndicator;
        
        var session = _repo.BeginScan(location);

        try
        {
            // 0.5) Bail on error reading scan location
            if (!Directory.Exists(location))
            {
                string msg = $"Root scan path does not exist: {location}";
                throw new DirectoryNotFoundException(msg);
            }
            
            // 1) Enumerate filesystem and record into repo
            QuickRescanState? quickState = null;
            if (mode == ScanMode.Quick)
            {
                var snapshot = _repo.GetSnapshot();
                quickState = BuildQuickRescanState(snapshot, location);
            }

            List<FileToHash> filesToHash;
            using (PhaseScope.Begin(ScanPhase.Enumerating))
            using (TimingLog.StartPhase(ScanPhase.Enumerating))
            {
                filesToHash = await EnumeratePhaseAsync(
                    rootPath: location,
                    progress: progress,
                    session: session,
                    quickState: quickState,
                    token: token);
            }

            // 2) Hash all non-zero files
            using (PhaseScope.Begin(ScanPhase.Hashing))
            using (TimingLog.StartPhase(ScanPhase.Hashing))
            {
                await RunHashingAsync(filesToHash, progress, session, token);
            }
            
            // Deletion detection (quick rescan only)
            if (quickState is not null)
            {
                ApplyQuickRescanDeletions(quickState, session);
            }

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
    
    private async Task<List<FileToHash>> EnumeratePhaseAsync(
        string rootPath,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        IScanSession session,
        QuickRescanState? quickState,
        CancellationToken token)
    {
        var filesToHash   = new List<FileToHash>();
        var dirsToVisit   = new Stack<string>();
        long foldersVisited = 0;

        var prevFiles = quickState?.PreviousFiles;
        var prevDirs  = quickState?.PreviousDirs;

        dirsToVisit.Push(rootPath);

        while (dirsToVisit.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            var dir = dirsToVisit.Pop();
            var normDir = PathUtils.NormalizePath(dir);

            foldersVisited++;

            Report(
                progress,
                ScanPhase.Enumerating,
                $"Scanning {normDir}",
                indeterminate: true,
                processed: foldersVisited);
            
            // Mark this directory as seen, if it existed previously
            prevDirs?.Remove(normDir);

            session.AddOrUpdateDirectory(normDir);
            TimingLog.Counter("folders");

            foreach (var e in _fs.EnumerateChildren(normDir, token))
            {
                if (e.IsDirectory)
                {
                    dirsToVisit.Push(e.FullPath);
                    continue;
                }

                var fullPath = PathUtils.NormalizePath(e.FullPath);
                
                // Quick-rescan: try to reuse existing hash
                if (TryReuseExistingFile(
                        fullPath,
                        e.Length,
                        e.CreationTimeUtc,
                        e.ModifiedTimeUtc,
                        prevFiles,
                        session))
                {
                    prevFiles?.Remove(fullPath);
                    continue;
                }

                // Normal path: record as enumerated, hash not computed yet.
                session.AddOrUpdateFile(
                    fullFilePath: fullPath,
                    size: e.Length,
                    hash: HashKey.NotComputed,
                    modified: e.ModifiedTimeUtc,
                    created: e.CreationTimeUtc,
                    status: ScanEntryStatus.Enumerated);

                // Only non-zero files are hashed
                if (e.Length > 0)
                {
                    filesToHash.Add(new FileToHash(
                        fullPath,
                        e.Length,
                        e.CreationTimeUtc,
                        e.ModifiedTimeUtc));
                }
                
                TimingLog.Counter("files");
            }

            // Give the scheduler a chance occasionally in large trees
            if ((foldersVisited & 0xFF) == 0)
                await Task.Yield();
        }

        return filesToHash;
    }

    private static bool TryReuseExistingFile(
        string fullPath,
        long size,
        DateTimeOffset createdUtc,
        DateTimeOffset modifiedUtc,
        Dictionary<string, FileRecord>? previousFilesByPath,
        IScanSession session)
    {
        if (previousFilesByPath is null)
            return false;

        if (!previousFilesByPath.TryGetValue(fullPath, out var prev))
            return false;

        // Must be a previously hashed, error-free file
        if (prev.Status != ScanEntryStatus.Hashed)
            return false;
        if (!prev.Hash.IsComputed)
            return false;
        if (!string.IsNullOrEmpty(prev.ErrorMessage))
            return false;

        // Size and modified timestamp must match
        if (prev.Size != size)
            return false;
        if (prev.Modified != modifiedUtc)
            return false;

        // ignore creation time mismatch

        // Reuse the hash, mark as hashed in the new scan
        session.AddOrUpdateFile(
            fullFilePath: fullPath,
            size: size,
            hash: prev.Hash,
            modified: modifiedUtc,
            created: createdUtc,
            status: ScanEntryStatus.Hashed);

        return true;
    }

    private sealed class QuickRescanState
    {
        public Dictionary<string, FileRecord> PreviousFiles { get; }
        public Dictionary<string, DirRecord>  PreviousDirs  { get; }

        public QuickRescanState(
            Dictionary<string, FileRecord> previousFiles,
            Dictionary<string, DirRecord>  previousDirs)
        {
            PreviousFiles = previousFiles;
            PreviousDirs  = previousDirs;
        }
    }

    private QuickRescanState BuildQuickRescanState(RepoViewSnapshot snapshot, string rootPath)
    {
        var rootNormalized = PathUtils.NormalizePath(rootPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var files = new Dictionary<string, FileRecord>(StringComparer.FromComparison(comparison));
        var dirs  = new Dictionary<string, DirRecord>(StringComparer.FromComparison(comparison));

        foreach (var dir in snapshot.Dirs.Values)
        {
            var dirPath = _repo.GetFullDirPath(dir.Id);
            var norm    = PathUtils.NormalizePath(dirPath);

            if (!norm.StartsWith(rootNormalized,comparison))
                continue;

            dirs[norm] = dir;
        }

        foreach (var file in snapshot.Files.Values)
        {
            var dirPath = _repo.GetFullDirPath(file.DirId);
            var full    = PathUtils.NormalizePath(Path.Combine(dirPath, file.Name));

            if (!full.StartsWith(rootNormalized, comparison))
                continue;

            files[full] = file;
        }

        return new QuickRescanState(files, dirs);
    }

    private void ApplyQuickRescanDeletions(QuickRescanState quickState, IScanSession session)
    {
        // Files that remained in the map are now missing on disk
        foreach (var file in quickState.PreviousFiles.Values)
        {
            session.MarkFileDeleted(file.Id);
        }

        // Directories that remained are missing; this naturally includes whole subtrees.
        foreach (var dir in quickState.PreviousDirs.Values)
        {
            session.MarkDirectoryDeleted(dir.Id);
        }
    }


    
    // ------------ Hashing phase ----------------
    
    // Inside DuplicateFileFinder

    private async Task RunHashingAsync(
        IReadOnlyList<FileToHash> filesToHash,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        IScanSession session,
        CancellationToken token)
    {
        Report(progress, ScanPhase.Hashing, "Computing checksums...");

        var total = filesToHash.Count;
        if (total == 0)
        {
            Report(progress, ScanPhase.Hashing, "No files to hash.", 1.0, processed: 0, total: 0);
            return;
        }

        var result = await HashingRunner.HashFilesAsync(
            filesToHash,
            _checksums,
            _hashDegreeOfParallelism,
            progress,
            token).ConfigureAwait(false);

        HashingRunner.ApplyHashResults(filesToHash, result, session);
        HashingRunner.RecordHashingStats(filesToHash, result);

        Report(
            progress,
            ScanPhase.Hashing,
            "Hashing complete.",
            percent: 1.0,
            processed: total,
            total: total);
    }
    
    
    // ------------ Progress helper ----------------

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
    
    // ---------- HashingHelper -----------
    private static class HashingRunner
    {
        internal sealed class Result
        {
            public Result(HashKey[] hashes, string?[] errors, bool[] ok)
            {
                Hashes = hashes;
                Errors = errors;
                Ok     = ok;
            }

            public HashKey[] Hashes { get; }
            public string?[] Errors { get; }
            public bool[]    Ok     { get; }

            // ReSharper disable once UnusedMember.Local
            public int Total => Hashes.Length;
        }

        public static async Task<Result> HashFilesAsync(
            IReadOnlyList<FileToHash> files,
            IChecksumPipeline pipeline,
            int hashDegreeOfParallelism,
            IProgress<DuplicateFileFinderProgressReport>? progress,
            CancellationToken token)
        {
            var total = files.Count;
            var hashes = new HashKey[total];
            var errors = new string?[total];
            var ok     = new bool[total];

            var dop       = Math.Max(1, hashDegreeOfParallelism);
            var semaphore = new SemaphoreSlim(dop);
            var tasks     = new Task[total];
            long processed = 0;

            for (int i = 0; i < total; i++)
            {
                int idx  = i;
                var file = files[idx];

                await semaphore.WaitAsync(token).ConfigureAwait(false);

                tasks[idx] = Task.Run(async () =>
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();

                        var hashKey = await pipeline
                            .ComputeFileHashAsync(file.Path, token)
                            .ConfigureAwait(false);

                        hashes[idx] = hashKey;
                        ok[idx]     = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        hashes[idx] = HashKey.CannotCompute;
                        errors[idx] = ex.Message;
                        ok[idx]     = false;
                    }
                    finally
                    {
                        var done = Interlocked.Increment(ref processed);
                        var pct  = Math.Min(1.0, (double)done / total);

                        DuplicateFileFinder.Report(
                            progress,
                            ScanPhase.Hashing,
                            done == total
                                ? "Finished hashing."
                                : $"Hashing files... ({done}/{total})",
                            pct,
                            processed: done,
                            total: total);

                        semaphore.Release();
                    }
                }, token);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            return new Result(hashes, errors, ok);
        }

        public static void ApplyHashResults(
            IReadOnlyList<FileToHash> files,
            Result result,
            IScanSession session)
        {
            for (int i = 0; i < files.Count; i++)
            {
                var file  = files[i];
                var hash  = result.Hashes[i];
                var error = result.Errors[i];

                if (result.Ok[i])
                {
                    session.AddOrUpdateFile(
                        fullFilePath: file.Path,
                        hash: hash,
                        status: ScanEntryStatus.Hashed);
                }
                else
                {
                    session.AddOrUpdateFile(
                        fullFilePath: file.Path,
                        hash: hash,
                        status: ScanEntryStatus.Error,
                        errorMessage: error);
                }
            }
        }

        public static void RecordHashingStats(
            IReadOnlyList<FileToHash> files,
            Result result)
        {
            long bytes = 0;
            int count  = 0;

            for (int i = 0; i < files.Count; i++)
            {
                if (!result.Ok[i]) continue;
                count++;
                bytes += files[i].Size;
            }

            TimingLog.Counter("files_hashed",  count);
            TimingLog.Counter("bytes_hashed",  bytes);
        }
    }

}