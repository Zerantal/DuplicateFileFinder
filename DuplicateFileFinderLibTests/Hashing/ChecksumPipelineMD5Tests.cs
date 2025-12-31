// DuplicateFileFinderLibTests/Hashing/ChecksumPipelineMD5Tests.cs

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Hashing;

using Xunit;

namespace DuplicateFileFinderLibTests.Hashing;

public sealed class ChecksumPipelineMD5Tests
{
    [Fact]
    public void Ctor_BufferSizeMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChecksumPipelineMD5(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChecksumPipelineMD5(-1));
    }

    [Fact]
    public async Task ComputeFileHashAsync_EmptyFile_MatchesFrameworkMD5()
    {
        var path = CreateTempFile(Array.Empty<byte>());

        var pipeline = new ChecksumPipelineMD5(bufferSize: 4096);

        using var pooled = await pipeline.ComputeFileHashAsync(path, CancellationToken.None);
        var expected = MD5.HashData(Array.Empty<byte>());

        Assert.Equal(16, pooled.Bytes.Length);
        Assert.Equal(expected, pooled.Bytes.ToArray());
    }

    [Fact]
    public async Task ComputeFileHashAsync_SmallFile_MatchesFrameworkMD5()
    {
        var data = Encoding.UTF8.GetBytes("hello world");
        var path = CreateTempFile(data);

        var pipeline = new ChecksumPipelineMD5(bufferSize: 4096);

        using var pooled = await pipeline.ComputeFileHashAsync(path, CancellationToken.None);
        var expected = MD5.HashData(data);

        Assert.Equal(expected, pooled.Bytes.ToArray());
    }

    [Fact]
    public async Task ComputeFileHashAsync_LargeFile_MatchesFrameworkMD5()
    {
        // Ensure multiple reads (larger than buffer)
        var data = new byte[1_500_000];
        new Random(123).NextBytes(data);

        var path = CreateTempFile(data);

        var pipeline = new ChecksumPipelineMD5(bufferSize: 64 * 1024);

        using var pooled = await pipeline.ComputeFileHashAsync(path, CancellationToken.None);
        var expected = MD5.HashData(data);

        Assert.Equal(expected, pooled.Bytes.ToArray());
    }

    [Fact]
    public async Task ComputeFileHashAsync_RespectsCancellation_BeforeReadLoop()
    {
        var data = new byte[512 * 1024];
        new Random(1).NextBytes(data);

        var path = CreateTempFile(data);

        var pipeline = new ChecksumPipelineMD5(bufferSize: 64 * 1024);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            using var _ = await pipeline.ComputeFileHashAsync(path, cts.Token);
        });
    }

    [Fact]
    public async Task ComputeFileHashAsync_ThrowsFileNotFound_ForMissingPath()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".missing");

        var pipeline = new ChecksumPipelineMD5(bufferSize: 4096);

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            using var _ = await pipeline.ComputeFileHashAsync(missing, CancellationToken.None);
        });
    }

    [Fact]
    public async Task ComputeFileHashAsync_BufferSizeProperty_IsRespected()
    {
        var data = new byte[200_000];
        new Random(7).NextBytes(data);
        var path = CreateTempFile(data);

        var pipeline = new ChecksumPipelineMD5(bufferSize: 128 * 1024)
        {
            BufferSize = 8 * 1024 // override after construction
        };

        using var pooled = await pipeline.ComputeFileHashAsync(path, CancellationToken.None);
        var expected = MD5.HashData(data);

        Assert.Equal(expected, pooled.Bytes.ToArray());
    }

    [Fact]
    public async Task PooledHash_Dispose_IsIdempotent()
    {
        var data = Encoding.UTF8.GetBytes("dispose test");
        var path = CreateTempFile(data);

        var pipeline = new ChecksumPipelineMD5(bufferSize: 4096);
        var pooled = await pipeline.ComputeFileHashAsync(path, CancellationToken.None);

        // Should not throw if called multiple times (ArrayPool tolerates double-return poorly in general,
        // but our struct protects with nullable backing field.)
        pooled.Dispose();
        pooled.Dispose();
    }

    private static string CreateTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "dff_md5_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
