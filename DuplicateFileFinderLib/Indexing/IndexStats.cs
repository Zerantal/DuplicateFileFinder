namespace DuplicateFileFinderLib.Indexing;

public sealed record IndexStats(
    long FileCount,
    long DirCount,
    DateTimeOffset LastSyncUtc
);