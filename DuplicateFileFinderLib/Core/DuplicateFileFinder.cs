// DuplicateFileFinderLib/Core/DuplicateFileFinder.cs

using DuplicateFileFinderLib.FileSystem;
using DuplicateFileFinderLib.Hashing;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Core;

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
            
            // 1) Enumerate filesystem and record into repo
            List<FileToHash> filesToHash;
            using (PhaseScope.Begin(ScanPhase.Enumerating))
            using (TimingLog.StartPhase(ScanPhase.Enumerating))
            {
                filesToHash = await EnumeratePhaseAsync(location, progress, session, token);
            }

            // 2) Hash all non-zero files
            using (PhaseScope.Begin(ScanPhase.Hashing))
            using (TimingLog.StartPhase(ScanPhase.Hashing))
            {
                await RunHashingAsync(filesToHash, progress, session, token);
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
        CancellationToken token)
    {
        var filesToHash   = new List<FileToHash>();
        var dirsToVisit   = new Stack<string>();
        long foldersVisited = 0;

        dirsToVisit.Push(rootPath);

        while (dirsToVisit.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            var dir = dirsToVisit.Pop();
            foldersVisited++;

            Report(progress,
                ScanPhase.Enumerating,
                $"Scanning {dir}",
                indeterminate: true,
                processed: foldersVisited);

            session.AddOrUpdateDirectory(dir);
            TimingLog.Counter("folders");

            foreach (var e in _fs.EnumerateChildren(dir, token))
            {
                if (e.IsDirectory)
                {
                    dirsToVisit.Push(e.FullPath);
                }
                else
                {
                    // Record file in repo as "enumerated, hash not computed yet"
                    session.AddOrUpdateFile(
                        fullFilePath: e.FullPath,
                        size: e.Length,
                        hash: HashKey.NotComputed,
                        modified: e.ModifiedTimeUtc,
                        created: e.CreationTimeUtc,
                        status: ScanEntryStatus.Enumerated);

                    TimingLog.Counter("files");

                    // Only non-zero files are hashed
                    if (e.Length > 0)
                    {
                        filesToHash.Add(new FileToHash(
                            e.FullPath,
                            e.Length,
                            e.CreationTimeUtc,
                            e.ModifiedTimeUtc));
                    }
                }
            }

            // Give the scheduler a chance occasionally in large trees
            if ((foldersVisited & 0xFF) == 0)
                await Task.Yield();
        }

        return filesToHash;
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

    
    // private async Task RunHashingAsync(
    //     IReadOnlyList<FileToHash> filesToHash,
    //     IProgress<DuplicateFileFinderProgressReport>? progress,
    //     IScanSession session,
    //     CancellationToken token)
    // {
    //     Report(progress, ScanPhase.Hashing, "Computing checksums...");
    //
    //     var totalToHash = filesToHash.Count;
    //     
    //     if (totalToHash == 0)
    //     {
    //         Report(progress, ScanPhase.Hashing, "No files to hash.", 1.0, processed: 0, total: 0);
    //         return;
    //     }
    //
    //     // Parallel hash results, session updates will be done serially afterwards.
    //     var hashes = new HashKey[totalToHash];
    //     var errors = new string?[totalToHash];
    //     var ok     = new bool[totalToHash];
    //
    //     var semaphore = new SemaphoreSlim(_hashDegreeOfParallelism);
    //     var tasks     = new List<Task>(totalToHash);
    //     long processed = 0;
    //
    //     for (int i = 0; i < totalToHash; i++)
    //     {
    //         var idx  = i;
    //         var file = filesToHash[idx];
    //
    //         await semaphore.WaitAsync(token).ConfigureAwait(false);
    //
    //         var task = Task.Run(async () =>
    //         {
    //             try
    //             {
    //                 token.ThrowIfCancellationRequested();
    //
    //                 // Compute hash for this file
    //                 var hashKey = await _checksums
    //                     .ComputeFileHashAsync(file.Path, token)
    //                     .ConfigureAwait(false);
    //
    //                 hashes[idx] = hashKey;
    //                 ok[idx]     = true;
    //             }
    //             catch (OperationCanceledException)
    //             {
    //                 // Let cancellation propagate; do not mark as error.
    //                 throw;
    //             }
    //             catch (Exception ex)
    //             {
    //                 // Hash failed for this file: mark CannotCompute + capture error string.
    //                 hashes[idx] = HashKey.CannotCompute;
    //                 errors[idx] = ex.Message;
    //                 ok[idx]     = false;
    //             }
    //             finally
    //             {
    //                 var done = Interlocked.Increment(ref processed);
    //         
    //                 var pct = Math.Min(1.0,
    //                     totalToHash == 0 ? 1.0 : (double)done / totalToHash);
    //
    //                 // Progress reporting can safely be done from multiple threads
    //                 Report(
    //                     progress,
    //                     ScanPhase.Hashing,
    //                     done == totalToHash
    //                         ? "Finished hashing."
    //                         : $"Hashing files... ({done}/{totalToHash})",
    //                     pct,
    //                     processed: done,
    //                     total: totalToHash);
    //
    //                 semaphore.Release();
    //             }
    //         }, token);
    //
    //         tasks.Add(task);
    //     }
    //
    //     await Task.WhenAll(tasks).ConfigureAwait(false);
    //
    //     // Now update the repo / session serially to avoid threading issues inside ScanSession.
    //     for (int i = 0; i < totalToHash; i++)
    //     {
    //         var file  = filesToHash[i];
    //         var hash  = hashes[i];
    //         var error = errors[i];
    //
    //         if (ok[i])
    //         {
    //             session.AddOrUpdateFile(
    //                 fullFilePath: file.Path,
    //                 hash: hash,
    //                 status: ScanEntryStatus.Hashed);
    //         }
    //         else
    //         {
    //             // Hash failed: mark in session with CannotCompute + Error status/message.
    //             session.AddOrUpdateFile(
    //                 fullFilePath: file.Path,
    //                 hash: hash,
    //                 status: ScanEntryStatus.Error,
    //                 errorMessage: error);
    //         }
    //     }
    //     
    //     // Final progress report (100%)
    //     Report(
    //         progress,
    //         ScanPhase.Hashing,
    //         "Hashing complete.",
    //         percent: 1.0,
    //         processed: totalToHash,
    //         total: totalToHash);
    //
    //     long bytesHashed = 0;
    //     int filesHashed = 0;
    //     for (var i = 0; i < totalToHash; i++)
    //     {
    //         if (!ok[i]) continue;
    //         filesHashed++;
    //         bytesHashed += filesToHash[i].Size;
    //     }
    //
    //     TimingLog.Counter("files_hashed", filesHashed );
    //     TimingLog.Counter("bytes_hashed", bytesHashed );
    // }
    
    
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