// DuplicateFileFinderLib/Hashing/ChecksumPipeline.cs

using System.Diagnostics;
using System.Threading.Channels;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Tree;

namespace DuplicateFileFinderLib.Grouping;

public interface IChecksumPipeline
{
    Task ComputeAsync(FolderNode scope,
        Func<FileNode, bool> shouldHash,
        Action<int, string>? onProgress,
        CancellationToken ct);
}

public sealed class ChecksumPipeline : IChecksumPipeline
{
    public async Task ComputeAsync(FolderNode scope,
        Func<FileNode, bool> shouldHash,
        Action<int, string>? onProgress,
        CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var ch = Channel.CreateBounded<FileNode>(4000);

        // consumers
        var processed = 0;
        var workers = new Task[Math.Max(1, Environment.ProcessorCount)];
        for (var i = 0; i < workers.Length; i++)
            workers[i] = Task.Run(async () =>
            {
                while (await ch.Reader.WaitToReadAsync(ct))
                while (ch.Reader.TryRead(out var file))
                {
                    ct.ThrowIfCancellationRequested();
                    await file.ComputeChecksum(ct);
                    
                    TimingLog.Counter("AggregateSize", file.Size);
                    Interlocked.Increment(ref processed);
                    onProgress?.Invoke(processed, file.Path);
                }
            }, ct);

        // producer + progress
        await scope.TraverseFolders(async folder =>
        {
            foreach (var f in folder.Files)
            {
                ct.ThrowIfCancellationRequested();

                if (shouldHash(f))
                    await ch.Writer.WriteAsync(f, ct);
            }
        });

        ch.Writer.Complete();
        await Task.WhenAll(workers);

        // compute folder checksums upward
        await scope.TraverseFolders(up: f =>
        {
            ct.ThrowIfCancellationRequested();
            if (f.ChecksumBytes == null) f.ComputeChecksum(ct);
            return Task.CompletedTask;
        }).WaitAsync(ct);

        timer.Stop();
    }
}