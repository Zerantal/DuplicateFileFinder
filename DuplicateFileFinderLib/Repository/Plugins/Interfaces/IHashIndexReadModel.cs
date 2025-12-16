using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public interface IHashIndexReadModel
{
    /// <summary>
    /// Returns all duplicate groups as lists of FileIds.
    /// </summary>
    IReadOnlyList<(long size, IReadOnlyList<FileHandle> list)> GetDuplicateGroups(int minDuplicates = 2, long minSize = 1);

    int TotalDuplicateFileCount { get; }
    long TotalSpaceTakenByDuplicates { get; }
}