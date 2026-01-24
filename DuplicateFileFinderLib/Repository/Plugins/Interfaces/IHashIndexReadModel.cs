using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public enum DuplicateSort
{
    TotalSizeDesc,
    DuplicateCountDesc
}

public readonly record struct DuplicateQuery(
    int MinDuplicates = 2,
    long MinSize = 1,
    DuplicateSort Sort = DuplicateSort.TotalSizeDesc);

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
