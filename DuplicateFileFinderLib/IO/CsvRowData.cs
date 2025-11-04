namespace DuplicateFileFinderLib.IO;

public enum KindEnum
{
    Folder,
    File
}

internal record CsvRowData
{
    public KindEnum Kind { get; init; }
    public required string Path { get; init; }
    public long Size { get; init; }
    public int FileCount { get; init; }
    public string? Checksum { get; init; }
    public int Group { get; init; }
}