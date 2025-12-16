using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Core;

public sealed class RepoSnapshotView
{
    public required IReadOnlyDictionary<long, ScanRootSnapshotView> Snapshots { get; init; }
    public required IReadOnlyDictionary<long, ScanRoot> ScanRoots { get; init; }
    
    public DirRecordV2 GetDir(DirHandle dir) => Snapshots[dir.ScanRootId].Dirs[dir.Index];
    public FileRecordV2 GetFile(FileHandle file) => Snapshots[file.ScanRootId].Files[file.Index];
    
    public string DecodeDirName(DirHandle dir)
    {
        var s = Snapshots[dir.ScanRootId];
        var rec = s.Dirs[dir.Index];
        return rec.NameStrIdx >= 0 ? s.StringPool.GetString(rec.NameStrIdx) : "";
    }

    public string DecodeFileName(FileHandle file)
    {
        var s = Snapshots[file.ScanRootId];
        var rec = s.Files[file.Index];
        return rec.NameStrIdx >= 0 ? s.StringPool.GetString(rec.NameStrIdx) : "";
    }
}