namespace DuplicateFileFinder.Gui.Features.Duplicates.Models;

public readonly record struct FileItem(
    FileId Id,
    string Name,
    string FullPath,
    long Size,
    DateTimeOffset Modified);
