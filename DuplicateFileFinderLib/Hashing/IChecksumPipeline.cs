// DuplicateFileFinderLib/Hashing/IChecksumPipeline.cs

using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Hashing;

public interface IChecksumPipeline
{
    /// <summary>
    /// Compute a 128-bit content hash for the file at <paramref name="fullPath"/>.
    /// Throws on I/O failure.
    /// Caller decides how to handle errors (e.g. mark file as CannotCompute).
    /// </summary>
    Task<HashKey> ComputeFileHashAsync(string fullPath, CancellationToken token = default);
}
