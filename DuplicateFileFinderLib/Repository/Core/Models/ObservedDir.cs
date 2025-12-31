// DuplicateFileFinderLib/Core/Models/ObservedDir.cs
namespace DuplicateFileFinderLib.Repository.Core.Models;

public readonly record struct ObservedDir
{
    public required string Name { get; init; }
    public long CreatedTicks { get; init; }      // 0 if unknown
    public long ModifiedTicks { get; init; }     // 0 if unknown
    public string? ErrorMessage { get; init; }
}
