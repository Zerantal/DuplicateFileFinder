using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

[MemoryPackable]
internal sealed partial record HashIndexState
{
    [MemoryPackOrder(0)] public required long LastIndexedGeneration { get; init; }
    [MemoryPackOrder(1)] public required long LastIndexedLogSequence { get; init; }
    [MemoryPackOrder(2)] public required Dictionary<HashKey, (long size, List<long> list)> Index { get; init; }
    [MemoryPackOrder(3)] public required int TotalDuplicateFileCount { get; init; }
    [MemoryPackOrder(4)] public required long TotalSpaceTakenByDuplicates { get; init; }
}