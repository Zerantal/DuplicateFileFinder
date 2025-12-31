// DuplicateFileFinderLib/Core/DuplicateFileFinder.cs

using System.Runtime.InteropServices;

using DuplicateFileFinderLib.Hashing;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.IO.Platforms;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;

namespace DuplicateFileFinderLib.Core;

public sealed class DuplicateFileFinder
{
    private readonly FullScanOperation _fullScan;

    private readonly bool _throttleProgress = true;

    public DuplicateFileFinder(IRepoHost host) : this(host, null)
    {
    }

    // ReSharper disable once MemberCanBePrivate.Global
    internal DuplicateFileFinder(
        IRepoHost host,
        IVolumeInfoProvider? volumeInfoProvider = null,
        IFileEnumerator? fs = null,
        IHashingRunner<FileHashToken>? hashingRunner = null)
    {
        fs ??= new FileEnumerator();

        if (volumeInfoProvider is null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            volumeInfoProvider = new WindowsVolumeInfoProvider();
        if (volumeInfoProvider is null && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            volumeInfoProvider = new LinuxVolumeInfoProvider();

        if (hashingRunner is null)
        {
            var checksumPipeline = new ChecksumPipelineMD5();
            hashingRunner = new HashingRunner<FileHashToken>(checksumPipeline);
        }

        _fullScan = new FullScanOperation(host, fs, hashingRunner, volumeInfoProvider);
    }

    internal DuplicateFileFinder(IRepoHost host, bool throttleProgress)
        : this(host)
    {
        _throttleProgress = throttleProgress;
    }

    public Task FullScanAsync(
        string rootPath,
        IProgress<DuplicateFileFinderProgressReport>? progress = null,
        CancellationToken ct = default)
    {
        return _fullScan.ExecuteAsync(rootPath, ThrottledProgress(progress), ct);
    }

    // helper
    private IProgress<DuplicateFileFinderProgressReport>? ThrottledProgress(
        IProgress<DuplicateFileFinderProgressReport>? progress = null)
    {
        return _throttleProgress && progress is not null
            ? new ThrottledProgress(progress)
            : progress;
    }
}
