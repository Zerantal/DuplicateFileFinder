namespace DuplicateFileFinderLib.Repository.Models;

// Not persisted - represents a copy of live snapshot data
public sealed class RepoViewSnapshot
{
    public required IReadOnlyDictionary<long, FileRecord> Files { get; init; }
    public required IReadOnlyDictionary<long, DirRecord> Dirs { get; init; }
}