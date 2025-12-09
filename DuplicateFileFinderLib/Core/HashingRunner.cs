using DuplicateFileFinderLib.Hashing;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Core;

internal static class HashingRunner
{
    /// <summary>
    /// Internal representation of a file that needs hashing.
    /// </summary>
    internal readonly record struct FileToHash(
        string FullPath,
        FileRecord FileRecord);
     
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
     
    public static async Task RunAsync(
        List<FileToHash> filesToHash, 
        IScanSession session,
        IChecksumPipeline pipeline,
        int hashDegreeOfParallelism = 1,
        IProgress<DuplicateFileFinderProgressReport>? progress = null,
        CancellationToken ct = default)
    {
        DuplicateFileFinderHelpers.Report(progress, ScanPhase.Hashing, "Computing checksums...");

        var total = filesToHash.Count;
        if (total == 0)
        {
            DuplicateFileFinderHelpers.Report(progress, ScanPhase.Hashing, "No files to hash.", 1.0, processed: 0, total: 0);
            return;
        }
         
        var result = await HashFilesAsync(
            filesToHash,
            pipeline,
            hashDegreeOfParallelism,
            progress,
            ct).ConfigureAwait(false);

        ApplyHashResults(filesToHash, result, session);
        RecordHashingStats(filesToHash, result);

        DuplicateFileFinderHelpers.Report(
            progress,
            ScanPhase.Hashing,
            "Hashing complete.",
            percent: 1.0,
            processed: total,
            total: total);
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
                         .ComputeFileHashAsync(file.FullPath, token)
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

                     DuplicateFileFinderHelpers.Report(
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
                var updatedFile = file.FileRecord with { Hash = hash, Status = ScanEntryStatus.Hashed };
                    
                session.AddOrUpdateFile(ref updatedFile);
            }
            else
            {
                var updatedFile = file.FileRecord with { Hash = hash, Status = ScanEntryStatus.Error, ErrorMessage = error };
                    
                session.AddOrUpdateFile(ref  updatedFile);
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
            bytes += files[i].FileRecord.Size;
        }

        TimingLog.Counter("files_hashed",  count);
        TimingLog.Counter("bytes_hashed",  bytes);
    }
}