// DuplicateFileFinderLib/Hashing/ChecksumPipelineMD5.cs
using System.Buffers;
using System.Security.Cryptography;

namespace DuplicateFileFinderLib.Hashing;

public sealed class ChecksumPipelineMD5 : IChecksumPipeline
{
    public ChecksumPipelineMD5(int bufferSize = 128 * 1024)
    {
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        BufferSize = bufferSize;
    }

    public int BufferSize { get; set; }

    public async ValueTask<PooledHash> ComputeFileHashAsync(string fullPath, CancellationToken token)
    {
        byte[]? readBuffer = null;

        try
        {
            readBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

            await using var fs = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: BufferSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                token.ThrowIfCancellationRequested();

                var read = await fs.ReadAsync(readBuffer, 0, readBuffer.Length, token).ConfigureAwait(false);
                if (read <= 0)
                    break;

                hasher.AppendData(readBuffer, 0, read);
            }

            // digest allocation from framework:
            var digest = hasher.GetHashAndReset(); // 16 bytes

            var pooled = ArrayPool<byte>.Shared.Rent(digest.Length);
            Buffer.BlockCopy(digest, 0, pooled, 0, digest.Length);
        
            return new PooledHash(pooled, digest.Length);
        }
        finally
        {
            if (readBuffer is not null)
                ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }
}