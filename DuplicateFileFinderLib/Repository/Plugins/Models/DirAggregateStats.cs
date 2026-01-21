using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

[MemoryPackable(SerializeLayout.Sequential)]
public partial record struct DirAggregateStats
{
    public required long TotalBytes { get; init; }
    public required int FileCount { get; init; }
    public required int DirCount { get; init; } // descendant dirs, excluding self
    public required long DuplicateFiles { get; init; }
    public required long DuplicateBytes { get; init; }
}
