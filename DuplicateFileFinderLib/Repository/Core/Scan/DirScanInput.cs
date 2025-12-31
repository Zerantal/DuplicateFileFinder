using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinderLib.Repository.Core.Scan;

public readonly record struct DirScanInput()
{
    public long DirId { get; init; } = -1;                // -1 previously unsee dir                 
    public long ParentDirId { get; init; } = -1;         // -1 => no parent (scan-root dir)
    public string Name { get; init; } = String.Empty;    // never null
    public long CreatedTicks { get; init; } = 0;         // 0 if unknown
    public long ModifiedTicks { get; init; } = 0;        // 0 if unknown
    public ScanEntryStatus Status { get; init; } = ScanEntryStatus.None;
    public string? ErrorMessage { get; init; } = null;
}