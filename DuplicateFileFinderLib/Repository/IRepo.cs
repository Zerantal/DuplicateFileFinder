using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository;

public interface IRepo
{
    RepoViewSnapshot GetSnapshot();
    public IReadOnlyList<ScanRun> ScanRunsView { get; }
    public IScanSession BeginScan(string rootPath, int maxFilesBeforeFlush = 10_000, int maxDirsBeforeFlush = 1_000);
    void CommitDelta(RepoDelta delta);
    public Task CommitDeltaAsync(RepoDelta delta, CancellationToken cancellationToken = default);
    void SaveSnapshot();
    void CompactIfNeeded(RepoCompactionPolicy? policy = null);
    void CompactNow();
    string GetFullDirPath(Guid dirId);
}