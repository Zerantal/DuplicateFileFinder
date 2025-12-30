// DuplicateFileFinderLib/Repository/Models/ScanRoot.cs

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Storage.Models;

public enum ScanRunStatus : byte
{
    InProgress = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3
}

/// <summary>
/// Controls whether we reuse existing hashes for unchanged files, or force a rehash.
/// </summary>
public enum HashPolicyMode : byte
{
    Default = 0, // reuse hash if unchanged (size + mtime match and baseline hash exists)
    ForceRehash = 1  // hash all non-empty files regardless of baseline
}

[MemoryPackable]
public sealed partial record ScanRun
{
    [MemoryPackOrder(0)] public required long ScanSequence { get; init; }
    [MemoryPackOrder(1)] public required long ScanRootId { get; init; }
    [MemoryPackOrder(2)] public required string RootPath { get; init; } = string.Empty;
    [MemoryPackOrder(3)] public required DateTimeOffset StartedAt { get; init; }
    [MemoryPackOrder(4)] public DateTimeOffset? FinishedAt { get; init; }
    [MemoryPackOrder(5)] public required ScanRunStatus Status { get; init; }
    [MemoryPackOrder(6)] public string? ErrorMessage { get; init; }
    [MemoryPackOrder(8)] public HashPolicyMode HashPolicy { get; init; } = HashPolicyMode.Default;

}
