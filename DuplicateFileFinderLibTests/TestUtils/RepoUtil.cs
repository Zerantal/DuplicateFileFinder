using System;
using System.Collections.Generic;
using System.Linq;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLibTests.TestUtils;

public static class RepoUtil
{
    internal static RepoSnapshotView MakeSnapshotV2(
        long scanRootId,
        (string name, long parentDirId, long dirId)[] dirs,
        (string name, long dirId, long fileId, long size)[] files)
    {
        return MakeSnapshotV2(scanRootId, dirs,
            files.Select(f => (
                f.name,
                f.dirId,
                f.fileId,
                f.size,
                hash: HashKey.NotComputed)).ToArray());
    }

    internal static RepoSnapshotView MakeSnapshotV2(
        long scanRootId,
        (string name, long parentDirId, long dirId)[] dirs,
        (string name, long dirId, long fileId, long size, HashKey hash)[] files)
    {
        // String pool layout:
        // [dir0.name][dir0.err][dir1.name][dir1.err]...[file0.name][file0.err]...
        var strings = new string[dirs.Length * 2 + files.Length * 2];
        var w = 0;

        for (var i = 0; i < dirs.Length; i++)
        {
            strings[w++] = dirs[i].name;
            strings[w++] = string.Empty;
        }

        for (var i = 0; i < files.Length; i++)
        {
            strings[w++] = files[i].name;
            strings[w++] = string.Empty;
        }

        var pool = PackedStringPool.FromStrings(strings);

        var dirRecs = new DirRecordV2[dirs.Length];
        for (var i = 0; i < dirs.Length; i++)
        {
            dirRecs[i] = new DirRecordV2
            {
                DirId = dirs[i].dirId,
                ParentDirId = dirs[i].parentDirId,         // -1 means "root"
                NameStrIdx = i * 2,
                ErrorMessageStrIdx = i * 2 + 1,
                LastSeenScanSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                CreatedTicks = 0,
                ModifiedTicks = 0,
            };
        }

        var fileBase = dirs.Length * 2;
        var fileRecs = new FileRecordV2[files.Length];
        for (var i = 0; i < files.Length; i++)
        {
            fileRecs[i] = new FileRecordV2
            {
                FileId = files[i].fileId,
                DirId = files[i].dirId,
                NameStrIdx = fileBase + i * 2,
                ErrorMessageStrIdx = fileBase + i * 2 + 1,
                Size = files[i].size,
                Hash = files[i].hash,
                Status = ScanEntryStatus.Enumerated,
                LastSeenScanSequence = 1,
                CreatedTicks = 0,
                ModifiedTicks = 0,
            };
        }

        var rootSnapshot = new ScanRootSnapshotView
        {
            ScanRootId = scanRootId,
            StringPool = pool,
            Dirs = dirRecs,
            Files = fileRecs
        };

        var snapshots = new Dictionary<long, ScanRootSnapshotView>
        {
            [scanRootId] = rootSnapshot
        };

        return new RepoSnapshotView
        {
            Snapshots = snapshots,
            ScanRoots = MakeScanRootsFromSnapshots(snapshots)
        };
    }

    /// <summary>
    /// Builds a usable RepoSnapshotView.ScanRoots map from the provided snapshots.
    /// Suitable for plugin/unit-test scenarios where "deleted roots" are not being modeled.
    /// </summary>
    internal static Dictionary<long, ScanRoot> MakeScanRootsFromSnapshots(
        IReadOnlyDictionary<long, ScanRootSnapshotView> snapshots,
        Func<long, bool>? isDeleted = null,
        Func<long, long>? dirIdForRoot = null,
        Func<long, string>? rootPathForRoot = null,
        Func<long, string?>? volumePathForRoot = null,
        Func<long, string?>? volumeLabelForRoot = null,
        Func<long, string?>? displayNameForRoot = null)
    {
        var dict = new Dictionary<long, ScanRoot>(snapshots.Count);

        foreach (var (rootId, snapshot) in snapshots)
        {
            var deleted = isDeleted?.Invoke(rootId) ?? false;

            // Default: choose the first dir as the scan-root dir, if present.
            var dirId = dirIdForRoot?.Invoke(rootId)
                       ?? (snapshot.Dirs.Count > 0 ? snapshot.Dirs[0].DirId : 0);

            dict[rootId] = new ScanRoot
            {
                RootId = rootId,
                DirId = dirId,
                RootPath = rootPathForRoot?.Invoke(rootId) ?? $"root-{rootId}",
                VolumePath = volumePathForRoot?.Invoke(rootId),
                VolumeLabel = volumeLabelForRoot?.Invoke(rootId),
                DisplayName = displayNameForRoot?.Invoke(rootId),
                IsDeleted = deleted,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }

        return dict;
    }

    internal static DirHandle[] Sort(DirHandle[] a)
        => a.OrderBy(h => h.ScanRootId).ThenBy(h => h.Index).ToArray();

    internal static FileHandle[] Sort(FileHandle[] a)
        => a.OrderBy(h => h.ScanRootId).ThenBy(h => h.Index).ToArray();
}
