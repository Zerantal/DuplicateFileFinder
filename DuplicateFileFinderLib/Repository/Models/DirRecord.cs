using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record DirRecord
{
    [MemoryPackOrder(0)] public required long DirId { get; init; }
    [MemoryPackOrder(1)] public required long? ParentDirId { get; init; }
    [MemoryPackOrder(2)] public required string Name { get; init; }
    [MemoryPackOrder(3)] public required long LastSeenScanSequence { get; init; }
    [MemoryPackOrder(4)] public required ScanEntryStatus Status { get; init; }
    [MemoryPackOrder(5)] public string? ErrorMessage { get; init; }

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