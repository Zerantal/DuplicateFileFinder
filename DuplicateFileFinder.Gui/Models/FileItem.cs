namespace DuplicateFileFinder.Gui.Models;

public readonly record struct FileItem(
    long Id,
    string Name,
    string FullPath,
    long Size,
    DateTimeOffset Modified);