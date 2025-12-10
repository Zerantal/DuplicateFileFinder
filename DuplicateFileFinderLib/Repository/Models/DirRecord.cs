using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record DirRecord
{
    [MemoryPackOrder(0)] public long DirId { get; init; } = -1;
    [MemoryPackOrder(1)] public long? ParentDirId { get; init; } = -1;
    [MemoryPackOrder(2)] public string Name { get; init; } = string.Empty;
    [MemoryPackOrder(3)] public long LastSeenScanSequence { get; init; } = -1;
    [MemoryPackOrder(4)] public required ScanEntryStatus Status { get; init; }
    [MemoryPackOrder(5)] public string? ErrorMessage { get; init; }
    [MemoryPackOrder(6)] public DateTimeOffset? Modified { get; init; }
    [MemoryPackOrder(7)] public DateTimeOffset? Created { get; init; }


    // Possible Extensions:
    // [MemoryPackOrder(7)] ulong? INode {get; init;}   // or FileId on Windows (might need to be byte[])
    // [MemoryPackOrder(8)] ulong? DeviceId ErrorMessage { get; init; }
    
    public virtual bool Equals(DirRecord? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return DirId == other.DirId;
    }

    public override int GetHashCode()
    {
        return DirId.GetHashCode();
    }
}