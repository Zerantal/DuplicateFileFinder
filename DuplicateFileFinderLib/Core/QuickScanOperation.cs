using DuplicateFileFinderLib.Hashing;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Interfaces;

namespace DuplicateFileFinderLib.Core;

internal class QuickScanOperation(
    IRepoHost host,
    IFileEnumerator fs,
    IChecksumPipeline checksums,
    IVolumeInfoProvider? volumeInfoProvider)
{
    public Task ExecuteAsync(
        string rootPath,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}