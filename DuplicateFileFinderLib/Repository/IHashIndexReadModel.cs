using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository;

public interface IHashIndexReadModel
{
    /// <summary>
    /// Returns all duplicate groups as lists of FileIds.
    /// </summary>
    IReadOnlyList<IReadOnlyList<FileRecord>> GetDuplicateGroups(int minDuplicates = 2, long minSize = 1);

    int TotalDuplicateFileCount { get; }
    long TotalSpaceTakenByDuplicates { get; }
}