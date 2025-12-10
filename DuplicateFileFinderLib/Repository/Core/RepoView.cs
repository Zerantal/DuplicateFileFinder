using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Core;

/// <summary>
/// Immutable, read-only view of repo state at a point in time.
/// Backed by copies of the repo's internal dictionaries, so it is stable
/// even if the repo continues to mutate after creation.
/// </summary>
internal sealed class RepoView(
    IReadOnlyDictionary<long, DirRecord> dirs,
    IReadOnlyDictionary<long, FileRecord> files)
    : IRepoView
{
    public IReadOnlyDictionary<long, DirRecord> Dirs  => dirs;
    public IReadOnlyDictionary<long, FileRecord> Files => files;

    public DirRecord? TryGetDir(long dirId) => dirs.GetValueOrDefault(dirId);

    public FileRecord? TryGetFile(long fileId) => files.GetValueOrDefault(fileId);
}