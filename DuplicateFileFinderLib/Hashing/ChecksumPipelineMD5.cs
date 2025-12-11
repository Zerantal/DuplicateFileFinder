// DuplicateFileFinderLib/Hashing/ChecksumPipeline.cs

using System.Security.Cryptography;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Hashing;

public sealed class ChecksumPipelineMD5 : IChecksumPipeline
{
    public ChecksumPipelineMD5(int bufferSize = 128 * 1024)
    {
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        BufferSize = bufferSize;
    }

    public async Task<HashKey> ComputeFileHashAsync(string fullPath,  CancellationToken token = default)
    {
        using var md5 = MD5.Create();
        await using var fs = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: BufferSize,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hashBytes = await md5.ComputeHashAsync(fs, token).ConfigureAwait(false);

        // HashKey expects exactly 16 bytes
        return new HashKey(hashBytes);
    }

    public int BufferSize { get; set; }
}