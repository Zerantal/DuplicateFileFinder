using System.Collections.Immutable;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

[MemoryPackable]
internal sealed partial record TreeIndexState
{
    [MemoryPackOrder(0)] public required long LastIndexedGeneration { get; init; }
    [MemoryPackOrder(2)] public required ImmutableDictionary<DirHandle, ImmutableArray<DirHandle>> ChildrenDirsByParentId { get; init; }
    [MemoryPackOrder(3)] public required ImmutableDictionary<DirHandle, ImmutableArray<FileHandle>> ChildrenFilesByDirId { get; init; }
    [MemoryPackOrder(4)] public required ImmutableDictionary<DirHandle, DirAggregateStats> DirStatsById { get; init; }
}