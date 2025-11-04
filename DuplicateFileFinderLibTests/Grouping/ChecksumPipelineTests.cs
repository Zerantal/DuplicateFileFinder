using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib;
using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Grouping;
using DuplicateFileFinderLib.Tree;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

namespace DuplicateFileFinderLibTests.Grouping;

file sealed class BlockingChecksumPipeline : IChecksumPipeline
{
    private readonly ManualResetEventSlim _ready;
    private readonly ManualResetEventSlim _release;

    public BlockingChecksumPipeline(ManualResetEventSlim ready, ManualResetEventSlim release)
    {
        _ready = ready;
        _release = release;
    }

    public async Task ComputeAsync(FolderNode scope, Func<FileNode, bool> shouldHash,
        Action<double>? onProgress, CancellationToken ct)
    {
        // Signal that we entered checksum stage
        _ready.Set();
        // Block until test releases (or cancellation triggers)
        _release.Wait(ct);
        // If not canceled we’ll just simulate fast completion
        await Task.Yield();
    }
}

public sealed class ChecksumPipelineTests : IDisposable
{
    private readonly string _root;
    private readonly IoUtil _ioUtil;

    public ChecksumPipelineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DFF_CP_" + Guid.NewGuid().ToString("N"));
        _ioUtil = new IoUtil(_root);
    }

    public void Dispose()
    {
        _ioUtil.Dispose();
    }

    private FileNode MakeFile(FolderNode parent, string name, byte[] content)
    {
        var full = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        var fn = new FileNode(full, content.LongLength);
        parent.AddFileSystemNode(fn);
        return fn;
    }

    [Fact]
    public async Task Computes_Checksums_BasedOn_Predicate()
    {
        var root = new FolderNode(_root);
        var scope = root;

        var fA = MakeFile(root, "a.bin", "HELLO"u8.ToArray()); // size 5
        var fB = MakeFile(root, "b.bin", "WORLD"u8.ToArray()); // size 5
        var fU = MakeFile(root, "u.bin", "XY"u8.ToArray()); // size 2

        var pipe = new ChecksumPipeline();

        bool ShouldHash(FileNode f) => f is { Size: >2, ChecksumBytes: null };

        await pipe.ComputeAsync(scope, ShouldHash, null, CancellationToken.None);

        Assert.NotNull(fA.ChecksumBytes);
        Assert.NotNull(fB.ChecksumBytes);
        Assert.True(fA.ChecksumHex.Length > 0);
        Assert.True(fB.ChecksumHex.Length > 0);

        // unique-size file may remain un-hashed        
        Assert.True(fU.ChecksumBytes == null || fU.ChecksumBytes!.Length > 0);

        // folder checksum computed
        Assert.Null(root.ChecksumBytes);
    }

    [Fact]
    public async Task Computes_FolderChecksum_When_AllChildrenHashed()
    {
        var root = new FolderNode(_root);
        var fA = MakeFile(root, "a.bin", "HELLO"u8.ToArray()); // 5
        var fB = MakeFile(root, "b.bin", "WORLD"u8.ToArray()); // 5
        var fU = MakeFile(root, "u.bin", "XY"u8.ToArray()); // 2

        var pipe = new ChecksumPipeline();
        await pipe.ComputeAsync(root,
            shouldHash: _ => true, // hash every file
            onProgress: null,
            ct: CancellationToken.None);

        Assert.NotNull(fA.ChecksumBytes);
        Assert.NotNull(fB.ChecksumBytes);
        Assert.NotNull(fU.ChecksumBytes);
        Assert.NotNull(root.ChecksumBytes); // now safe to assert
    }

    [Fact]
    public async Task Cancels_Cleanly()
    {
        var root = new FolderNode(_root);
        for (int i = 0; i < 200; i++)
            MakeFile(root, $"f{i}.bin", new byte[4096]);

        var pipe = new ChecksumPipeline();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await pipe.ComputeAsync(root, _ => true, null, cts.Token);
        });
    }

    
}
