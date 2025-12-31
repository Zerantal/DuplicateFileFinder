// Repository/Models/RepoMetaFile.cs

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Storage.Models;

[MemoryPackable]
public partial record RepoMetaFile
{
    [MemoryPackOrder(0)] public required RepoMeta Meta { get; init; }
    [MemoryPackOrder(1)] public required List<ScanRoot> ScanRoots { get; init; } = [];
    [MemoryPackOrder(2)] public required List<ScanRun> ScanRuns { get; init; } = [];
}
