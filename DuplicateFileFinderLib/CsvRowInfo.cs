#pragma warning disable CS8618
namespace DuplicateFileFinderLib;

public enum EntryType
{
    ScanRootFolder,
    Folder,
    File
}

internal record CsvRowData
{
    public EntryType Type { get; init; }
    public string Path { get; init; }
    public long Size { get; init; }
    public int FileCount { get; init; }
    public string Extension { get; init; }
    public string Checksum { get; init; }
}