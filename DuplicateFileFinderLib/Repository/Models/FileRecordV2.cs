using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable(SerializeLayout.Sequential)]
// ReSharper disable once RedundantExtendsListEntry
public readonly partial record struct FileRecordV2() : IEquatable<FileRecordV2>
{
    public long FileId { get; init; } = -1;
    public long DirId { get; init; } = -1;
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

    public static FileRecordV2 FromOldFileRecord(FileRecord oldFile, int nameIdx, int errorMsgIdx)
    {
        return new FileRecordV2
        {
            FileId = oldFile.FileId,
            DirId = oldFile.DirId,
            NameStrIdx = nameIdx,
            Size = oldFile.Size,
            Hash = oldFile.Hash,
            ModifiedTicks = oldFile.Modified?.Ticks ?? 0,
            CreatedTicks = oldFile.Created?.Ticks ?? 0,
            LastSeenScanSequence = oldFile.LastSeenScanSequence,
            Status = oldFile.Status,
            ErrorMessageStrIdx = errorMsgIdx
        };
    }
}