namespace DuplicateFileFinderLib.Core;

public sealed class DuplicateFileRow
{
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public DateTimeOffset CreationTimeUtc { get; init; }
    public string Checksum { get; init; } = "";
    public int Group { get; init; }
}