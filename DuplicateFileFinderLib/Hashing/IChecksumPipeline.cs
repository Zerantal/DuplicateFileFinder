// DuplicateFileFinderLib/Hashing/IChecksumPipeline.cs

namespace DuplicateFileFinderLib.Hashing;

public interface IChecksumPipeline
{
    int BufferSize { get; set; }
    ValueTask<PooledHash> ComputeFileHashAsync(string fullPath, CancellationToken token);

}
