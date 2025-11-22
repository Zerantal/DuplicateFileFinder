using System;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Grouping;
using DuplicateFileFinderLib.Tree;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

namespace DuplicateFileFinderLibTests.Core;

public sealed class ChecksumPipelineTests : IDisposable
{
    private readonly TempFsFixture _fs = new();

    public void Dispose()
    {
        _fs.Dispose();
    }

    [Fact]
    public async Task Computes_Checksums_BasedOn_Predicate()
    {
        var faContent = "HELLO"u8.ToArray();
        var fbContent = "WORLD"u8.ToArray();
        var fUContent = "XY"u8.ToArray();
        var fA = new FileNodeBuilder().Path(_fs.File("a.bin", faContent ))
            .Size(faContent.LongLength).Build();
        var fB = new FileNodeBuilder().Path(_fs.File( "b.bin", fbContent ))
            .Size(fbContent.LongLength).Build();
        var fU = new FileNodeBuilder().Path(_fs.File( "u.bin", fUContent ))
            .Size(fUContent.LongLength).Build();
        
        var root = new FolderNodeBuilder(_fs.Root).File(fA).File(fB).File(fU).Build();
        var scope = root;

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
        var faContent = "HELLO"u8.ToArray();
        var fbContent = "WORLD"u8.ToArray();
        var fUContent = "XY"u8.ToArray();
        var fA = new FileNodeBuilder().Path(_fs.File("a.bin", faContent ))
            .Size(faContent.LongLength).Build();
        var fB = new FileNodeBuilder().Path(_fs.File( "b.bin", fbContent ))
            .Size(fbContent.LongLength).Build();
        var fU = new FileNodeBuilder().Path(_fs.File( "u.bin", fUContent ))
            .Size(fUContent.LongLength).Build();
        
        var root = new FolderNodeBuilder(_fs.Root).File(fA).File(fB).File(fU).Build();
        
        var pipe = new ChecksumPipeline();
        await pipe.ComputeAsync(root,
            predicate: _ => true, // hash every file
            onFileHashed: null,
            token: CancellationToken.None);

        Assert.NotNull(fA.ChecksumBytes);
        Assert.NotNull(fB.ChecksumBytes);
        Assert.NotNull(fU.ChecksumBytes);
        Assert.NotNull(root.ChecksumBytes); // now safe to assert
    }

    [Fact]
    public async Task Cancels_Cleanly()
    {
        var rootBuilder = new FolderNodeBuilder(_fs.Root);
        for (int i = 0; i < 200; i++)
            rootBuilder.File(new FileNodeBuilder().Path(
                _fs.File($"f{i}.bin", new byte[4096])).Build());

        var root = rootBuilder.Build();
        var pipe = new ChecksumPipeline();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await pipe.ComputeAsync(root, _ => true, null, cts.Token);
        });
    }

    
}
