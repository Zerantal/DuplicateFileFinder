using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.FileDir;

public sealed partial class FileDirIndexPlugin
{
    public bool TryGetDir(DirId dirId, out DirHandle handle) => _dirsById.TryGetValue(dirId, out handle);

    public bool TryGetFile(FileId fileId, out FileHandle handle) => _filesById.TryGetValue(fileId, out handle);

    public int DirCount => _activeDirCount;
    public int FileCount => _activeFileCount;

    public bool TryGetFilePathById(FileId fileId, out string relativePath)
    {
        relativePath = string.Empty;
        return TryGetFile(fileId, out var handle) && TryGetFilePathByHandle(handle, out relativePath);
    }

    public bool TryGetFilePathByHandle(FileHandle fileHandle, out string relativePath)
    {
        relativePath = string.Empty;

        if (!_activeScanRoots.Contains(fileHandle.ScanRootId))
            return false;

        var view = _snapshotView;
        if (view is null)
            return false;

        if (!view.Snapshots.TryGetValue(fileHandle.ScanRootId, out var snapshot))
            return false;

        if ((uint)fileHandle.Index >= (uint)snapshot.Files.Count)
            return false;

        var file = snapshot.Files[fileHandle.Index];

        // Collect segments bottom-up: [fileName, dirName, dirName, ...]
        var segments = new List<string>(capacity: 8) { view.DecodeFileName(fileHandle) };

        // Snapshot dirs dictionary for stable lookups during traversal
        var dirsById = _dirsById;

        DirId dirId = file.DirId;
        while (dirId > 0)
        {
            if (!dirsById.TryGetValue(dirId, out var dh))
                return false;

            if (dh.ScanRootId != fileHandle.ScanRootId)
                return false; // should not happen

            if ((uint)dh.Index >= (uint)snapshot.Dirs.Count)
                return false;

            var dir = snapshot.Dirs[dh.Index];
            segments.Add(view.DecodeDirName(dh));
            dirId = dir.ParentDirId;
        }

        // Build relative path in correct order
        segments.Reverse();

        relativePath = segments.Count == 0 ? string.Empty : segments[0];
        for (int i = 1; i < segments.Count; i++)
            relativePath = Path.Combine(relativePath, segments[i]);

        return true;
    }

    public bool TryGetDirPathById(DirId dirId, out string relativePath)
    {
        relativePath = string.Empty;
        return TryGetDir(dirId, out var handle) && TryGetDirPathByHandle(handle, out relativePath);
    }

    public bool TryGetDirPathByHandle(DirHandle dirHandle, out string relativePath)
    {
        relativePath = string.Empty;

        if (!_activeScanRoots.Contains(dirHandle.ScanRootId))
            return false;

        var view = _snapshotView;
        if (view is null)
            return false;

        if (!view.Snapshots.TryGetValue(dirHandle.ScanRootId, out var snapshot))
            return false;

        if ((uint)dirHandle.Index >= (uint)snapshot.Dirs.Count)
            return false;

        var segments = new List<string>(capacity: 8);

        // Snapshot dirs dictionary for stable lookups during traversal
        var dirsById = _dirsById;

        var dir = snapshot.Dirs[dirHandle.Index];
        segments.Add(view.DecodeDirName(dirHandle));

        DirId parentId = dir.ParentDirId;
        while (parentId > 0)
        {
            if (!dirsById.TryGetValue(parentId, out var parentHandle))
                return false;

            if (parentHandle.ScanRootId != dirHandle.ScanRootId)
                return false;

            if ((uint)parentHandle.Index >= (uint)snapshot.Dirs.Count)
                return false;

            var parent = snapshot.Dirs[parentHandle.Index];
            segments.Add(view.DecodeDirName(parentHandle));
            parentId = parent.ParentDirId;
        }

        segments.Reverse();

        relativePath = segments.Count == 0 ? string.Empty : segments[0];
        for (int i = 1; i < segments.Count; i++)
            relativePath = Path.Combine(relativePath, segments[i]);

        return true;
    }
}
