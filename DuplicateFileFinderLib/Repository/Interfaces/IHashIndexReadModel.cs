namespace DuplicateFileFinderLib.Repository.Interfaces;

public interface IHashIndexReadModel
{
    /// <summary>
    /// Returns all duplicate groups as lists of FileIds.
    /// </summary>
    IReadOnlyList<(long size, IReadOnlyList<long> list)> GetDuplicateGroups(int minDuplicates = 2, long minSize = 1);

    int TotalDuplicateFileCount { get; }
    long TotalSpaceTakenByDuplicates { get; }
}