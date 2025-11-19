namespace DuplicateFileFinderLib.Repository.Models;

// Not persisted - represents a copy of live snapshot data
public sealed class RepoViewSnapshot
{
    public required IReadOnlyDictionary<Guid, FileRecord> Files { get; init; }
    public required IReadOnlyDictionary<Guid, DirRecord> Dirs { get; init; }
    public required IReadOnlyDictionary<HashKey, IReadOnlyList<Guid>> HashIndex { get; init; }
}