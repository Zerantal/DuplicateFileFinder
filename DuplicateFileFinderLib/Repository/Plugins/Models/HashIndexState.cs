using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

[MemoryPackable(SerializeLayout.Sequential)]
public sealed partial class HashGroupState
{
    public long Size { get; set; }
    public FileHandle[] Files { get; set; } = [];
}

[MemoryPackable(SerializeLayout.Sequential)]
public sealed partial class HashIndexState
{
    public long LastIndexedGeneration { get; init; }
    public long LastIndexedLogSequence { get; init; }
    public Dictionary<HashKey, HashGroupState> Index { get; init; } = new();
    public int TotalDuplicateFileCount { get; init; }
    public long TotalSpaceTakenByDuplicates { get; init; }
}