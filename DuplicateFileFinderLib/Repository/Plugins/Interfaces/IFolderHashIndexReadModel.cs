// DuplicateFileFinderLib/Repository/Plugins/Interfaces/IFolderHashIndexReadModel.cs

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public enum FolderDuplicateSort
{
    DuplicateCountDesc
}

public readonly record struct FolderDuplicateGroupPage(
    int Offset,
    int Count,
    ReadOnlyMemory<FolderGroupDescriptor> Groups);

public interface IFolderHashIndexReadModel
{
    FolderDuplicateGroupPage GetGroupsPage(int offset, int count, FolderDuplicateSort sort);

    ReadOnlySpan<DirHandle> GetGroupDirs(in FolderGroupDescriptor group);

    int TotalDuplicateFolderCount { get; }
}

