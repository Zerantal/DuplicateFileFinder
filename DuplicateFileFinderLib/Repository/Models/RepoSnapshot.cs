using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record RepoSnapshot
{
    [MemoryPackOrder(0)] public required RepoMeta Meta { get; init; }
    [MemoryPackOrder(1)] public required Dictionary<Guid, FileRecord> Files { get; init; }
    [MemoryPackOrder(2)] public required Dictionary<Guid, DirRecord> Dirs { get; init; }
    [MemoryPackOrder(3)] public required Dictionary<HashKey, List<Guid>> HashIndex { get; init; } // persist indexes
    [MemoryPackOrder(4)] public required List<ScanRun> ScanRuns { get; init; }
    [MemoryPackOrder(5)] public required List<ScanRoot> ScanRoots { get; init; }
}