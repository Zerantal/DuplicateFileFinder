// DuplicateFileFinderLib/Core/Models/ObservedFile.cs
namespace DuplicateFileFinderLib.Repository.Core.Models;

public readonly record struct ObservedFile
{
    public required string Name { get; init; }
    public long Size { get; init; }
    public long CreatedTicks { get; init; }      // 0 if unknown
    public long ModifiedTicks { get; init; }     // 0 if unknown
    public string? ErrorMessage { get; init; }
}
