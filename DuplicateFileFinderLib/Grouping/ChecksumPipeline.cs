// DuplicateFileFinderLib/Hashing/ChecksumPipeline.cs

using System.Diagnostics;
using System.Threading.Channels;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Tree;

namespace DuplicateFileFinderLib.Grouping;

public interface IChecksumPipeline
{
    Task ComputeAsync(FolderNode scope,
        Func<FileNode, bool> predicate,
        Action<FileNode> onFileHashed,
        CancellationToken token);
}

public sealed class ChecksumPipeline : IChecksumPipeline
{
    public async Task ComputeAsync(FolderNode scope,
        Func<FileNode, bool> predicate,
        Action<FileNode>? onFileHashed,
        CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        var ch = Channel.CreateBounded<FileNode>(4000);

        // consumers
        var processed = 0;
        var workers = new Task[Math.Max(1, Environment.ProcessorCount)];
        for (var i = 0; i < workers.Length; i++)
            workers[i] = Task.Run(async () =>
            {
                while (await ch.Reader.WaitToReadAsync(token))
                while (ch.Reader.TryRead(out var file))
                {
                    token.ThrowIfCancellationRequested();
                    await file.ComputeChecksum(token);
                    
                    TimingLog.Counter("AggregateSize", file.Size);
                    Interlocked.Increment(ref processed);
                    onFileHashed?.Invoke(file);
                }
            }, token);

        // producer + progress
        await scope.TraverseFolders(async folder =>
        {
            foreach (var f in folder.Files)
            {
                token.ThrowIfCancellationRequested();

                if (predicate(f))
                    await ch.Writer.WriteAsync(f, token);
            }
        });

        ch.Writer.Complete();
        await Task.WhenAll(workers);

        // compute folder checksums upward
        await scope.TraverseFolders(up: f =>
        {
            token.ThrowIfCancellationRequested();
            if (f.ChecksumBytes == null) f.ComputeChecksum(token);
            return Task.CompletedTask;
        }).WaitAsync(token);

        timer.Stop();
    }
}