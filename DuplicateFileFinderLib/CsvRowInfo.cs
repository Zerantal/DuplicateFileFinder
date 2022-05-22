#pragma warning disable CS8618
namespace DuplicateFileFinderLib;

internal record CsvRowData
{
    public bool IsFile { get; init; }
    public string Path { get; init; }
    public long Size { get; init; }
    public int FileCount { get; init; }
    public string Extension { get; init; }
    public string Checksum { get; init; }
}