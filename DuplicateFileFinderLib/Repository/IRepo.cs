using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository;

public interface IRepo
{
    RepoViewSnapshot GetSnapshot();
    public IReadOnlyList<ScanRun> ScanRunsView { get; }
    public IScanSession BeginScan(
        string rootPath,
        ScanMode scanMode = ScanMode.Full,
        VolumeInfo? volume = null,
        int maxFilesBeforeFlush = 10_000,
        int maxDirsBeforeFlush = 1_000);
    void CommitDelta(RepoDelta delta);
    public Task CommitDeltaAsync(RepoDelta delta, CancellationToken cancellationToken = default);
    void SaveSnapshot();
    void CompactIfNeeded(RepoCompactionPolicy? policy = null);
    void CompactNow();
    string GetFullDirPath(Guid dirId);
    
    void RemoveScanRoot(string rootPath);
    
    /// <summary>
    /// Returns all immediate child directories of the specified parent directory.
    /// </summary>
    IReadOnlyList<DirRecord> GetChildDirs(Guid parentDirId);

    /// <summary>
    /// Returns all immediate child files of the specified directory.
    /// </summary>
    IReadOnlyList<FileRecord> GetChildFiles(Guid parentDirId);

    /// <summary>
    /// Returns all duplicate groups in the repo:
    /// each group is a list of files that share the same hash and group size >= 2.
    /// </summary>
    IReadOnlyList<IReadOnlyList<FileRecord>> GetDuplicateGroups();
}