namespace DuplicateFileFinder.Gui.Models;

public sealed class DuplicateFileModel : BaseObjectModel
{
    public required string FileName { get; init; }
    public long FileSize { get; init; }
    public DateTime CreationDate { get; init; }
    public int FileGroup { get; init; }
}