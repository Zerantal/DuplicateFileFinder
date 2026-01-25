using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public enum DuplicateSort
{
    TotalSizeDesc,
    DuplicateCountDesc
}

public readonly record struct DuplicateQuery()
{
    public int MinDuplicates { get; init; } = 2;
    public long MinSize { get; init; } = 1;
    public DuplicateSort Sort { get; init; } = DuplicateSort.TotalSizeDesc;
}

public readonly record struct DuplicateGroupPage(
    int Total,
    int Offset,
    int Count,
    ReadOnlyMemory<HashGroupDescriptor> Groups);

public interface IHashIndexReadModel
{
    DuplicateGroupPage GetGroupsPage(in DuplicateQuery query, int offset, int count);

    ReadOnlySpan<FileHandle> GetGroupFiles(in HashGroupDescriptor group);

    int TotalDuplicateFileCount { get; }
    long TotalSpaceTakenByDuplicates { get; }
}
