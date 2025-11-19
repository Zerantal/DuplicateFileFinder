using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

public enum ScanRunStatus : byte
{
    InProgress = 0,
    Completed  = 1,
    Failed     = 2,
    Cancelled  = 3
}

[MemoryPackable]
public sealed partial record ScanRun
{
    [MemoryPackOrder(0)] public required long ScanSequence          { get; init; }
    [MemoryPackOrder(2)] public required string RootPath            { get; init; } = string.Empty;
    [MemoryPackOrder(3)] public required DateTimeOffset StartedAt   { get; init; }
    [MemoryPackOrder(4)] public DateTimeOffset? FinishedAt          { get; init; }
    [MemoryPackOrder(5)] public required ScanRunStatus Status       { get; init; }
    [MemoryPackOrder(6)] public string? ErrorMessage                { get; init; }
}
