namespace DuplicateFileFinderLib.Scan;

public readonly record struct ScanEntry(
    bool IsDirectory,
    string FullPath,
    long Length,
    DateTimeOffset CreationTimeUtc);