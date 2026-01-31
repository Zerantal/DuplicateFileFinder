using System;
using System.Collections.Generic;
using System.Linq;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLibTests.TestUtils;

public static class RepoUtil
{
    internal static RepoSnapshotView MakeSnapshotV2(
        ScanRootId scanRootId,
        (string name, DirId parentDirId, DirId dirId)[] dirs,
        (string name, DirId dirId, FileId fileId, long size)[] files)
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
        ScanRootId scanRootId,
        (string name, DirId parentDirId, DirId dirId)[] dirs,
        (string name, DirId dirId, FileId fileId, long size, HashKey hash)[] files)
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

        var snapshots = new Dictionary<ScanRootId, ScanRootSnapshotView>
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
    internal static Dictionary<ScanRootId, ScanRoot> MakeScanRootsFromSnapshots(
        IReadOnlyDictionary<ScanRootId, ScanRootSnapshotView> snapshots,
        Func<DirId, bool>? isDeleted = null,
        Func<ScanRootId, DirId>? dirIdForRoot = null,
        Func<ScanRootId, string>? rootPathForRoot = null,
        Func<ScanRootId, string?>? volumePathForRoot = null,
        Func<ScanRootId, string?>? volumeLabelForRoot = null,
        Func<ScanRootId, string?>? displayNameForRoot = null)
    {
        var dict = new Dictionary<ScanRootId, ScanRoot>(snapshots.Count);

        foreach (var (rootId, snapshot) in snapshots)
        {
            // Default: not deleted
            var deleted = isDeleted?.Invoke(rootId) ?? false;

            // Default: use first dir's DirId as the scan-root dirId if present
            var dirId = dirIdForRoot?.Invoke(rootId)
                        ?? (snapshot.Dirs.Count > 0 ? snapshot.Dirs[0].DirId : -1);

            var rootPath = rootPathForRoot?.Invoke(rootId) ?? $"root-{rootId}";
            var volPath = volumePathForRoot?.Invoke(rootId);
            var volLabel = volumeLabelForRoot?.Invoke(rootId);
            var displayName = displayNameForRoot?.Invoke(rootId);

            dict[rootId] = new ScanRoot
            {
                RootId = rootId,
                DirId = dirId,
                RootPath = rootPath,
                VolumePath = volPath,
                VolumeLabel = volLabel,
                DisplayName = displayName,
                IsDeleted = deleted,
                CreatedAt = default
            };
        }

        return dict;
    }

    internal static DirHandle[] Sort(DirHandle[] a)
        => a.OrderBy(h => h.ScanRootId).ThenBy(h => h.Index).ToArray();

    internal static FileHandle[] Sort(FileHandle[] a)
        => a.OrderBy(h => h.ScanRootId).ThenBy(h => h.Index).ToArray();
}
