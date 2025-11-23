// DuplicateFileFinderLib/Hashing/ChecksumPipeline.cs

using System.Security.Cryptography;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Hashing;

public sealed class ChecksumPipelineMD5 : IChecksumPipeline
{
    /// <summary>
    /// Computes a 128-bit hash for the file, using MD5.
    /// Returns a HashKey constructed from the 16-byte digest.
    /// </summary>
    public async Task<HashKey> ComputeFileHashAsync(string fullPath, CancellationToken token = default)
    {
        using var md5 = MD5.Create();
        await using var fs = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);

        var hashBytes = await md5.ComputeHashAsync(fs, token).ConfigureAwait(false);

        // HashKey expects exactly 16 bytes
        return new HashKey(hashBytes);
    }
}