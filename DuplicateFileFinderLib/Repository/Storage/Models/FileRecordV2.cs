using DuplicateFileFinderLib.Repository.Core.Models;

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Storage.Models;

[MemoryPackable(SerializeLayout.Sequential)]
// ReSharper disable once RedundantExtendsListEntry
public readonly partial record struct FileRecordV2() : IEquatable<FileRecordV2>
{
    public FileId FileId { get; init; } = -1;
    public DirId DirId { get; init; } = -1;
    public int NameStrIdx { get; init; } = -1;
    public long Size { get; init; } = 0;
    public HashKey Hash { get; init; } = HashKey.NotComputed;
    public long ModifiedTicks { get; init; } = 0;
    public long CreatedTicks { get; init; } = 0;
    public long LastSeenScanSequence { get; init; } = -1;
    public ScanEntryStatus Status { get; init; } = ScanEntryStatus.None;
    public int ErrorMessageStrIdx { get; init; } = -1;

    public bool Equals(FileRecordV2 other) => FileId == other.FileId;
    public override int GetHashCode() => FileId.GetHashCode();
}
