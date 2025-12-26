using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Core.Scan;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public readonly record struct FileScanInput
{
    public long FileId { get; init; }                // 0 => allocate
    public long DirId { get; init; }                 // must be a valid dirId
    public required string Name { get; init; }       // never null
    public long Size { get; init; }
    public HashKey Hash { get; init; }
    public long CreatedTicks { get; init; }          // 0 if unknown
    public long ModifiedTicks { get; init; }         // 0 if unknown
    public ScanEntryStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
}