// DuplicateFileFinderLib/Core/DuplicateFileFinder.cs

using System.Runtime.InteropServices;
using DuplicateFileFinderLib.Hashing;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.IO.Platforms;
using DuplicateFileFinderLib.Repository.Interfaces;

namespace DuplicateFileFinderLib.Core;

public sealed class DuplicateFileFinder
{
    private readonly FullScanOperation _fullScan;
    private readonly QuickScanOperation _quickScan;
    private readonly RemoveRootOperation _removeRoot;

    private readonly bool _throttleProgress = true;

    public DuplicateFileFinder(IRepoHost host) : this(host, null)
    {
    }

    internal DuplicateFileFinder(
        IRepoHost host,
        IVolumeInfoProvider? volumeInfoProvider = null,
        IFileEnumerator? fs = null,
        IChecksumPipeline? checksums = null)
    {
        fs ??= new FileEnumerator();
        checksums ??= new ChecksumPipelineMD5();

        if (volumeInfoProvider is null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            volumeInfoProvider = new WindowsVolumeInfoProvider();
        if (volumeInfoProvider is null && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            volumeInfoProvider = new LinuxVolumeInfoProvider();

        _fullScan = new FullScanOperation(host, fs, checksums, volumeInfoProvider);
        _quickScan = new QuickScanOperation(host, fs, checksums, volumeInfoProvider);
        _removeRoot = new RemoveRootOperation(host);
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

    public Task QuickScanAsync(
        string rootPath,
        IProgress<DuplicateFileFinderProgressReport>? progress = null,
        bool skipUnchangedDirectories = true,
        CancellationToken ct = default)
    {
        return _quickScan.ExecuteAsync(rootPath, ThrottledProgress(progress), ct);
    }

    public void RemoveScanRoot(long scanRootId)
    {
        _removeRoot.Execute(scanRootId);
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