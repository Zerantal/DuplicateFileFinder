// DuplicateFileFinderLib/Repository/Plugins/Models/HashIndexState.cs

using DuplicateFileFinderLib.Repository.Storage.Models;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

[MemoryPackable(SerializeLayout.Sequential)]
public partial record struct HashGroupState
{
    public long Size { get; init; }
    public int Offset { get; init; }
    public int Count { get; init; }
}

[MemoryPackable(SerializeLayout.Sequential)]
public partial record struct HashIndexState
{
    public long LastIndexedGeneration { get; init; }

    // Group metadata (hash -> (size, offset, count))
    public KeyValuePair<HashKey, HashGroupState>[] Index { get; init; }

    // Flat blob: concatenation of all groups’ handles
    public FileHandle[] AllFiles { get; init; }

    public int TotalDuplicateFileCount { get; init; }
    public long TotalSpaceTakenByDuplicates { get; init; }
}
