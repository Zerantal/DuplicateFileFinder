// DuplicateFileFinderLib/Repository/Core/Models/ScanCheckpoint.cs

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Storage.Models;

[MemoryPackable(SerializeLayout.Sequential)]
internal sealed partial class ScanCheckpoint
{
    public long ScanRootId { get; init; }
    public long ScanSequence { get; init; }
    public required string RootPath { get; init; }

    // Frontier: paths still to enumerate
    public required string[] PendingDirPaths { get; init; }

    // Session snapshot of in-flight buffers
    public required ScanRootSnapshotV2 PartialSnapshot { get; init; }
    
    public long CreatedAtUtcTicks { get; init; }
}