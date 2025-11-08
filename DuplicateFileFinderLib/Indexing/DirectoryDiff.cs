namespace DuplicateFileFinderLib.Indexing;

public sealed record DirectoryDiff(
    IReadOnlyList<FileEntryMeta> ToInsert,
    IReadOnlyList<(string DirPath, string Name)> ToDelete,
    IReadOnlyList<FileEntryMeta> ToUpdate);