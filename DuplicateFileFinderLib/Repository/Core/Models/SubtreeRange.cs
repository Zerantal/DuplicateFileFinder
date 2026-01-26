// DuplicateFileFinderLib/Repository/Core/Models/SubtreeRange.cs

namespace DuplicateFileFinderLib.Repository.Core.Models;

/// <summary>
/// Preorder interval representing a directory subtree.
/// A node is in the subtree iff its preorder is in [Start, EndExclusive).
/// </summary>
public readonly record struct SubtreeRange(int Start, int EndExclusive)
{
    public bool IsEmpty => EndExclusive <= Start;

    public bool Contains(int preorder)
        => (uint)(preorder - Start) < (uint)(EndExclusive - Start);
}
