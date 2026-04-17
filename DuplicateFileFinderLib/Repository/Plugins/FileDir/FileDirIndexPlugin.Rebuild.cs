using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.FileDir;

public sealed partial class FileDirIndexPlugin
{
    private void RebuildFromSnapshot(RepoSnapshotView repoSnapshot)
    {
        var activeRootIds = repoSnapshot.ScanRoots.Values
            .Where(r => !r.IsDeleted)
            .Select(r => r.RootId)
            .ToHashSet();

        var liveSnapshots = new Dictionary<ScanRootId, ScanRootSnapshotView>(
            repoSnapshot.Snapshots.Where(kvp => activeRootIds.Contains(kvp.Key)));

        // Capacity hints based on live entries only.
        var totalDirs = 0;
        var totalFiles = 0;
        foreach (var (_, s) in liveSnapshots)
        {
            totalDirs += s.Dirs.Count(d => d.Status is not (ScanEntryStatus.Deleted or ScanEntryStatus.None));
            totalFiles += s.Files.Count(f => f.Status is not (ScanEntryStatus.Deleted or ScanEntryStatus.None));
        }

        var newDirs = new Dictionary<DirId, DirHandle>(capacity: totalDirs);
        var newFiles = new Dictionary<FileId, FileHandle>(capacity: totalFiles);

        var newDirCounts = new Dictionary<ScanRootId, int>(capacity: liveSnapshots.Count);
        var newFileCounts = new Dictionary<ScanRootId, int>(capacity: liveSnapshots.Count);

        var activeDirCount = 0;
        var activeFileCount = 0;

        foreach (var (rootId, snapshot) in liveSnapshots)
        {
            var rootDirCount = 0;
            var rootFileCount = 0;

            for (int i = 0; i < snapshot.Dirs.Count; i++)
            {
                var dir = snapshot.Dirs[i];
                if (dir.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                    continue;

                if (!newDirs.TryAdd(dir.DirId, new DirHandle(rootId, i)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate dirId {dir.DirId} encountered while rebuilding FileDirIndexPlugin.");
                }

                rootDirCount++;
                activeDirCount++;
            }

            for (int i = 0; i < snapshot.Files.Count; i++)
            {
                var file = snapshot.Files[i];
                if (file.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                    continue;

                if (!newFiles.TryAdd(file.FileId, new FileHandle(rootId, i)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate FileId {file.FileId} encountered while rebuilding FileDirIndexPlugin.");
                }

                rootFileCount++;
                activeFileCount++;
            }

            newDirCounts[rootId] = rootDirCount;
            newFileCounts[rootId] = rootFileCount;
        }

        // Publish in a coherent order:
        // 1) publish dictionaries
        // 2) publish counts + active roots
        // 3) publish snapshot view last so readers see coherent state for Decode* usage
        _dirsById = SegmentedMap<DirHandle>.FromDictionary(newDirs);
        _filesById = SegmentedMap<FileHandle>.FromDictionary(newFiles);

        _activeScanRoots = activeRootIds;
        _dirCountByRootId = newDirCounts;
        _fileCountByRootId = newFileCounts;
        _activeDirCount = activeDirCount;
        _activeFileCount = activeFileCount;

        _snapshotView = repoSnapshot;
    }

    private void EnsureCountsFromSnapshotIfMissing(RepoSnapshotView snapshot)
    {
        // If counts dictionaries are empty (e.g., older persisted state), recompute quickly.
        if (_dirCountByRootId.Count != 0 || _fileCountByRootId.Count != 0)
            return;

        var activeRootIds = snapshot.ScanRoots.Values
            .Where(r => !r.IsDeleted)
            .Select(r => r.RootId)
            .ToHashSet();

        var dirCounts = new Dictionary<ScanRootId, int>(capacity: activeRootIds.Count);
        var fileCounts = new Dictionary<ScanRootId, int>(capacity: activeRootIds.Count);

        var activeDirCount = 0;
        var activeFileCount = 0;

        foreach (var rootId in activeRootIds)
        {
            if (!snapshot.Snapshots.TryGetValue(rootId, out var sr))
                continue;

            var rootDirCount = 0;
            for (int i = 0; i < sr.Dirs.Count; i++)
            {
                if (sr.Dirs[i].Status is not (ScanEntryStatus.Deleted or ScanEntryStatus.None))
                    rootDirCount++;
            }

            var rootFileCount = 0;
            for (int i = 0; i < sr.Files.Count; i++)
            {
                if (sr.Files[i].Status is not (ScanEntryStatus.Deleted or ScanEntryStatus.None))
                    rootFileCount++;
            }

            dirCounts[rootId] = rootDirCount;
            fileCounts[rootId] = rootFileCount;

            activeDirCount += rootDirCount;
            activeFileCount += rootFileCount;
        }

        _activeScanRoots = activeRootIds;
        _dirCountByRootId = dirCounts;
        _fileCountByRootId = fileCounts;
        _activeDirCount = activeDirCount;
        _activeFileCount = activeFileCount;
    }
}
