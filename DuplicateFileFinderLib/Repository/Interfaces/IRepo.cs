using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Interfaces;

public interface IRepo : IDisposable, IAsyncDisposable
{
    RepoViewSnapshot GetSnapshot();
    public IReadOnlyList<ScanRun> ScanRunsView { get; }
    public IReadOnlyList<ScanRoot> ScanRootsView { get; }

    public IScanSession BeginScan(
        string rootPath,
        ScanMode scanMode = ScanMode.Full,
        VolumeInfo? volume = null,
        int maxFilesBeforeFlush = 50_000,
        int maxDirsBeforeFlush = 10_000);
    void CommitDelta(RepoDelta delta);
    public Task CommitDeltaAsync(RepoDelta delta, CancellationToken cancellationToken = default);
    public void SaveScanSnapshots();
    public Task CompactAsync(RepoCompactionPolicy? policy = null, CancellationToken ct = default);
    string GetFullDirPath(long dirId);
    
    void RemoveScanRoot(string rootPath);
}