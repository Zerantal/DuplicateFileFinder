using System.Collections.Immutable;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

[MemoryPackable]
internal sealed partial record TreeIndexState
{
    [MemoryPackOrder(0)] public required long LastIndexedGeneration { get; init; }
    [MemoryPackOrder(1)] public required long LastIndexedLogSequence { get; init; }
    [MemoryPackOrder(2)] public required ImmutableDictionary<long, ImmutableArray<long>> ChildrenDirsByParentId { get; init; }
    [MemoryPackOrder(3)] public required ImmutableDictionary<long, ImmutableArray<long>> ChildrenFilesByDirId { get; init; }
    [MemoryPackOrder(4)] public required ImmutableDictionary<long, DirAggregateStats> DirStatsById { get; init; }

}