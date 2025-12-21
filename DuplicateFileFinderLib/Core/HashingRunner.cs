// DuplicateFileFinderLib/Core/HashingRunner.cs

using DuplicateFileFinderLib.Hashing;
using DuplicateFileFinderLib.Logging;

namespace DuplicateFileFinderLib.Core;


internal class HashingRunner<T>(
    IChecksumPipeline pipeline)
    : IHashingRunner<T>
{
    private int _dop = 1;

    public int ReadBufferSize {
        get => pipeline.BufferSize;
        set => pipeline.BufferSize = value;
    }

    public int MaxDegreeOfParallelism
    {
        get => _dop;
        set => _dop = Math.Max(1, value);
    }

    public async Task HashFilesAsync(
        List<FileToHash<T>> filesToHash, 
        IProgress<DuplicateFileFinderProgressReport>? progress,
        Action<T, ReadOnlyMemory<byte>, string?> onFileHashed,
        CancellationToken ct)
    {
        DuplicateFileFinderHelpers.Report(progress, ScanPhase.Hashing, "Computing checksums...");

        var total = filesToHash.Count;
        if (total == 0)
        {
            DuplicateFileFinderHelpers.Report(progress, ScanPhase.Hashing, "No files to hash.", 1.0, processed: 0, total: 0);
            return;
        }
        
        long processed = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, total),
            new ParallelOptions { MaxDegreeOfParallelism = _dop, CancellationToken = ct },
            async (i, token) =>
            {
                var item = filesToHash[i];

                try
                {
                    using var h = await pipeline.ComputeFileHashAsync(item.FullPath, token).ConfigureAwait(false);
                    onFileHashed(item.Token, h.Bytes, null);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    onFileHashed(item.Token, ReadOnlyMemory<byte>.Empty, ex.Message);
                }
                finally
                {
                    var done = Interlocked.Increment(ref processed);
                    var pct = Math.Min(1.0, (double)done / total);

                    if ((done & 0x3FFF) == 0 || done == total)
                    {
                        DuplicateFileFinderHelpers.Report(
                            progress,
                            ScanPhase.Hashing,
                            done == total ? "Finished hashing." : $"Hashing files... ({done}/{total})",
                            pct,
                            running: true,
                            processed: done,
                            total: total);
                    }

                    TimingLog.Counter("files_hashed_attempted");
                }
            }).ConfigureAwait(false);

        DuplicateFileFinderHelpers.Report(
            progress,
            ScanPhase.Hashing,
            "Hashing complete.",
            1.0,
            running: true,
            processed: total,
            total: total);
    }
}