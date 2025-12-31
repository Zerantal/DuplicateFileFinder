// DuplicateFileFinderLib/Repository/Core/Models/ScanCheckpoint.cs

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Storage.Models;

[MemoryPackable()]
internal sealed partial class ScanCheckpoint
{
    public const int CurrentCheckpointVersion = 1;

    [MemoryPackOrder(0)] public int CheckpointVersion { get; init; } = CurrentCheckpointVersion;

    [MemoryPackOrder(1)] public long ScanRootId { get; init; }
    [MemoryPackOrder(2)] public long ScanSequence { get; init; } // run sequence that wrote this checkpoint
    [MemoryPackOrder(3)] public required string RootPath { get; init; }

    // Frontier: paths still to enumerate
    [MemoryPackOrder(4)] public required PendingDir[] PendingDirs { get; init; }

    // Delta snapshot since last checkpoint (MutationBuffer drains this)
    [MemoryPackOrder(5)] public required ScanRootSnapshotV2 PartialSnapshot { get; init; }

    [MemoryPackOrder(6)] public long CreatedAtUtcTicks { get; init; }
}

[MemoryPackable(SerializeLayout.Sequential)]
public readonly partial record struct PendingDir(
    long DirId,
    string FullPath);
