namespace DuplicateFileFinder.Gui.Models;

public readonly record struct FileItem(
    Guid Id,
    string Name,
    string FullPath,
    long Size,
    DateTimeOffset Modified);