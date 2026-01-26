using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public enum DuplicateSort
{
    TotalSizeDesc,
    DuplicateCountDesc
}

public readonly record struct DuplicateQuery(int MinDuplicates, long MinSize, DuplicateSort Sort)
{
    public static DuplicateQuery Default => new();
    public DuplicateQuery() : this(MinDuplicates: 2, MinSize: 1, Sort: DuplicateSort.TotalSizeDesc) { }
}

public readonly record struct SubtreeFilter(
    DirHandle RootDir,
    SubtreeRange Range);

public readonly record struct DuplicateGroupPage(
    int Offset,
    int Count,
    ReadOnlyMemory<HashGroupDescriptor> Groups);

public interface IHashIndexReadModel
{
    DuplicateGroupPage GetGroupsPage(in DuplicateQuery query, int offset, int count);

    DuplicateGroupPage GetGroupsPage(in DuplicateQuery query, in SubtreeFilter filter, int offset, int count);

    ReadOnlySpan<FileHandle> GetGroupFiles(in HashGroupDescriptor group);

    int TotalDuplicateFileCount { get; }
    long TotalSpaceTakenByDuplicates { get; }
}
