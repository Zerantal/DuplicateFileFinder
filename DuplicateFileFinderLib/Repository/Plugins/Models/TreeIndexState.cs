using System.Collections.Immutable;

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

[MemoryPackable]
internal sealed partial record TreeIndexState
{
    [MemoryPackOrder(0)] public required long LastIndexedGeneration { get; init; }

    // Regular dictionaries for fast load + low allocation overhead.
    // Values are ImmutableArray<T> so reads are allocation-free and safe to share.
    [MemoryPackOrder(1)] public required Dictionary<DirHandle, ImmutableArray<DirHandle>> ChildrenDirsByParentId { get; init; }
    [MemoryPackOrder(2)] public required Dictionary<DirHandle, ImmutableArray<FileHandle>> ChildrenFilesByDirId { get; init; }
    [MemoryPackOrder(3)] public required Dictionary<DirHandle, DirAggregateStats> DirStatsById { get; init; }
}
