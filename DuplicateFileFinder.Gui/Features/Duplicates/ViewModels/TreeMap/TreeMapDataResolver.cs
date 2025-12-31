using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public sealed class TreeMapDataResolver : ITreeMapDataResolver
{
    private readonly RepoSnapshotView _snapshot;
    private readonly ITreeIndexReadModel _treeIndex;
    private readonly Func<long, string> _dirRelativePathResolver;

    private long _lastScanRootId = -1;
    private ScanRootSnapshotView? _lastRootSnapshot;

    public TreeMapDataResolver(
        RepoSnapshotView snapshot,
        ITreeIndexReadModel treeIndex,
        Func<long, string> dirRelativePathResolver)
    {
        _snapshot = snapshot;
        _treeIndex = treeIndex;
        _dirRelativePathResolver = dirRelativePathResolver;
    }

    public string DecodeDirName(DirHandle dir)
    {
        try
        { return _snapshot.DecodeDirName(dir); }
        catch { return string.Empty; }
    }

    public string DecodeFileName(FileHandle file)
    {
        try
        { return _snapshot.DecodeFileName(file); }
        catch { return string.Empty; }
    }

    public DirRecordV2 GetDirRecord(DirHandle dir)
        => GetRootSnapshot(dir.ScanRootId).Dirs[dir.Index];

    public FileRecordV2 GetFileRecord(FileHandle file)
        => GetRootSnapshot(file.ScanRootId).Files[file.Index];

    public string GetRelativePath(long dirId)
    {
        try
        { return _dirRelativePathResolver(dirId); }
        catch { return string.Empty; }
    }

    public DirAggregateStats GetDirStats(DirHandle dir)
        => _treeIndex.GetDirStats(dir);

    private ScanRootSnapshotView GetRootSnapshot(long scanRootId)
    {
        if (_lastRootSnapshot is not null && _lastScanRootId == scanRootId)
            return _lastRootSnapshot;

        if (!_snapshot.Snapshots.TryGetValue(scanRootId, out var snap))
            throw new KeyNotFoundException($"Missing scan-root snapshot: {scanRootId}");

        _lastScanRootId = scanRootId;
        _lastRootSnapshot = snap;
        return snap;
    }
}
