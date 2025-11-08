namespace DuplicateFileFinderLib.Scan;

public readonly record struct FsEntry(
    bool IsDirectory,
    string FullPath,
    long Length,
    DateTimeOffset CreationTimeUtc,
    DateTimeOffset LastWriteTimeUtc
);