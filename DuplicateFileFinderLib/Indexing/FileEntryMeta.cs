namespace DuplicateFileFinderLib.Indexing;

public sealed record FileEntryMeta(
    string DirPath, 
    string Name,
    long SizeBytes,
    DateTimeOffset MTimeUtc,
    DateTimeOffset CTimeUtc,
    ulong? Inode,
    int Mode,
    bool IsDirectory
    );