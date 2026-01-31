// DuplicateFileFinderLib/Repository/Plugins/Models/TreeIndexState.cs

using DuplicateFileFinderLib.Repository.Core.Models;

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

/// <summary>
///     Packed representation of the tree index:
///     - Per-root: a single backing array for all child-dir handles and all child-file handles
///     - Per-dir: a Slice (Offset, Length) into the backing array
///     This avoids deserialising large numbers of ImmutableArray instances.
/// </summary>
[MemoryPackable(SerializeLayout.Sequential)]
internal sealed partial record TreeIndexState
{
    public required long LastIndexedGeneration { get; init; }

    // Keyed by ScanRootId
    public required Dictionary<long, RootTreeIndexState> Roots { get; init; }
}

[MemoryPackable(SerializeLayout.Sequential)]
internal sealed partial record RootTreeIndexState
{
    // One backing pool per element type per root.
    public required DirHandle[] ChildDirsPool { get; init; }
    public required FileHandle[] ChildFilesPool { get; init; }

    // Keyed by DirHandle.Index (per root) -> slice into the corresponding pool.
    public required SegmentedLongMap<Slice> ChildDirSliceByDirIndex { get; init; }
    public required SegmentedLongMap<Slice> ChildFileSliceByDirIndex { get; init; }

    public required SegmentedLongMap<DirAggregateStats> StatsByDirIndex { get; init; }

    // keyed by DirHandle.Index -> subtree preorder interval
    public required SegmentedLongMap<SubtreeRange> SubtreeRangeByDirIndex { get; init; }

    // per-file (FileHandle.Index) -> preorder of parent directory (or -1 if unknown)
    public required int[] DirPreorderByFileIndex { get; init; }
}

[MemoryPackable(SerializeLayout.Sequential)]
public readonly partial record struct Slice
{
    public int Offset { get; init; }
    public int Length { get; init; }

    public Slice(int offset, int length)
    {
        Offset = offset;
        Length = length;
    }

    public bool IsEmpty => Length <= 0;
}
