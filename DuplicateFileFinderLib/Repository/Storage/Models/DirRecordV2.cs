using DuplicateFileFinderLib.Repository.Core.Models;

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Storage.Models;

[MemoryPackable(SerializeLayout.Sequential)]
// ReSharper disable once RedundantExtendsListEntry
public readonly partial record struct DirRecordV2() : IEquatable<DirRecordV2>
{
    public long DirId { get; init; } = -1;
    public long ParentDirId { get; init; } = -1;
    public int NameStrIdx { get; init; } = -1;
    public long LastSeenScanSequence { get; init; } = -1;
    public ScanEntryStatus Status { get; init; } = ScanEntryStatus.None;
    public int ErrorMessageStrIdx { get; init; } = -1;
    public long ModifiedTicks { get; init; } = 0;
    public long CreatedTicks { get; init; } = 0;

    public bool Equals(DirRecordV2 other) => DirId == other.DirId;
    public override int GetHashCode() => DirId.GetHashCode();
}
