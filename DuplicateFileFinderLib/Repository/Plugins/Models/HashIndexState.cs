// DuplicateFileFinderLib/Repository/Plugins/Models/HashIndexState.cs

using DuplicateFileFinderLib.Repository.Core.Models;

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

[MemoryPackable(SerializeLayout.Sequential)]
public sealed partial record HashIndexState
{
    public long LastIndexedGeneration { get; init; }

    public int TotalDuplicateFileCount { get; init; }
    public long TotalSpaceTakenByDuplicates { get; init; }

    // Flat blob: concatenation of all groups’ handles
    public FileHandle[]? AllFiles { get; init; }

    // Dense descriptors (unsorted)
    public HashGroupDescriptor[]? Groups { get; init; }

    // Persisted sorted “views” (indices into Groups[])
    public int[]? BySizeDesc { get; init; }
    public int[]? ByCountDesc { get; init; }
}
