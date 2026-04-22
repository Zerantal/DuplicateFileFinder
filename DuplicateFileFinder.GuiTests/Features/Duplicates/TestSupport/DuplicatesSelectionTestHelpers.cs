using System;
using System.Collections.Generic;
using System.Linq;

using DuplicateFileFinder.GuiTests.UI.Fakes;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.GuiTests.Features.Duplicates.TestSupport;

internal static class DuplicatesSelectionTestHelpers
{
    internal sealed record DirDef(DirId DirId, DirId ParentDirId, string Name);

    internal sealed record FileDef(FileId FileId, DirId DirId, string Name, long Size);

    internal static RepoSnapshotView BuildSnapshot(
        ScanRootId scanRootId,
        IReadOnlyList<DirDef> dirs,
        IReadOnlyList<FileDef> files,
        string rootPath = "/root")
    {
        var strings = new PackedStringBuilder();

        var dirRecords = dirs
            .Select(d => new DirRecordV2
            {
                DirId = d.DirId,
                ParentDirId = d.ParentDirId,
                NameStrIdx = strings.InternOrMinusOne(d.Name),
                Status = ScanEntryStatus.Enumerated,
                LastSeenScanSequence = 1,
            })
            .ToArray();

        var fileRecords = files
            .Select(f => new FileRecordV2
            {
                FileId = f.FileId,
                DirId = f.DirId,
                NameStrIdx = strings.InternOrMinusOne(f.Name),
                Status = ScanEntryStatus.Enumerated,
                LastSeenScanSequence = 1,
                Size = f.Size,
                Hash = new HashKey(1, 2)
            })
            .ToArray();

        var rootSnapshot = new ScanRootSnapshotView
        {
            ScanRootId = scanRootId,
            StringPool = strings.Build(),
            Dirs = dirRecords,
            Files = fileRecords
        };

        return new RepoSnapshotView
        {
            Snapshots = new Dictionary<ScanRootId, ScanRootSnapshotView> { [scanRootId] = rootSnapshot },
            ScanRoots = new Dictionary<ScanRootId, ScanRoot>
            {
                [scanRootId] = new()
                {
                    RootId = scanRootId,
                    DirId = dirs[0].DirId,
                    RootPath = rootPath,
                    IsDeleted = false,
                    CreatedAt = default
                }
            }
        };
    }

    internal static void SeedFileDir(FakeFileDirReadModel fileDir, RepoSnapshotView snapshot)
    {
        foreach (var (scanRootId, root) in snapshot.Snapshots)
        {
            for (var i = 0; i < root.Dirs.Count; i++)
            {
                var handle = new DirHandle(scanRootId, i);
                var rec = root.Dirs[i];
                var path = BuildDirPath(snapshot, handle);

                fileDir.DirHandlesById[rec.DirId] = handle;
                fileDir.DirPathsByHandle[handle] = path;
                fileDir.DirPathsById[rec.DirId] = path;
            }

            for (var i = 0; i < root.Files.Count; i++)
            {
                var handle = new FileHandle(scanRootId, i);
                var rec = root.Files[i];
                var path = rec.FileId.ToString();

                fileDir.FileHandlesById[rec.FileId] = handle;
                fileDir.FilePathsByHandle[handle] = path;
                fileDir.FilePathsById[rec.FileId] = path;
            }
        }
    }

    internal static void ResetAndSeedFileDir(FakeFileDirReadModel fileDir, RepoSnapshotView snapshot)
    {
        fileDir.Reset();
        SeedFileDir(fileDir, snapshot);
    }

    internal static void ConfigureTreeIndex(FakeTreeIndex treeIndex, RepoSnapshotView snapshot)
    {
        treeIndex.Reset();

        var childDirsByParent = new Dictionary<DirHandle, DirHandle[]>();
        var childFilesByParent = new Dictionary<DirHandle, FileHandle[]>();
        var statsByDir = new Dictionary<DirHandle, DirAggregateStats>();

        foreach (var (scanRootId, root) in snapshot.Snapshots)
        {
            for (var i = 0; i < root.Dirs.Count; i++)
            {
                var handle = new DirHandle(scanRootId, i);
                childDirsByParent[handle] = [];
                childFilesByParent[handle] = [];
            }

            for (var i = 0; i < root.Dirs.Count; i++)
            {
                var handle = new DirHandle(scanRootId, i);
                var rec = root.Dirs[i];

                if (rec.ParentDirId < 0)
                    continue;

                if (!TryGetDirHandle(snapshot, scanRootId, rec.ParentDirId, out var parent))
                    continue;

                childDirsByParent[parent] = [.. childDirsByParent[parent], handle];
            }

            for (var i = 0; i < root.Files.Count; i++)
            {
                var handle = new FileHandle(scanRootId, i);
                var rec = root.Files[i];

                if (!TryGetDirHandle(snapshot, scanRootId, rec.DirId, out var parent))
                    continue;

                childFilesByParent[parent] = [.. childFilesByParent[parent], handle];
            }
        }

        foreach (var dir in childDirsByParent.Keys)
            statsByDir[dir] = ComputeStats(dir);

        treeIndex.GetChildDirsImpl = dir =>
            childDirsByParent.TryGetValue(dir, out var children)
                ? children
                : ReadOnlySpan<DirHandle>.Empty;

        treeIndex.GetChildFilesImpl = dir =>
            childFilesByParent.TryGetValue(dir, out var files)
                ? files
                : ReadOnlySpan<FileHandle>.Empty;

        treeIndex.GetDirStatsImpl = dir =>
            statsByDir.TryGetValue(dir, out var stats)
                ? stats
                : new DirAggregateStats
                {
                    TotalBytes = 0,
                    FileCount = 0,
                    DirCount = 0,
                    DuplicateFiles = 0,
                    DuplicateBytes = 0
                };

        DirAggregateStats ComputeStats(DirHandle dir)
        {
            var childDirs = childDirsByParent.TryGetValue(dir, out var dirs) ? dirs : [];
            var childFiles = childFilesByParent.TryGetValue(dir, out var files) ? files : [];

            var totalDirs = childDirs.Length;
            var totalFiles = childFiles.Length;
            long totalBytes = childFiles.Length == 0 ? 0 : childFiles.Length * 100L;

            foreach (var child in childDirs)
            {
                var childStats = ComputeStats(child);
                totalDirs += childStats.DirCount;
                totalFiles += childStats.FileCount;
                totalBytes += childStats.TotalBytes;
            }

            return new DirAggregateStats
            {
                DirCount = totalDirs,
                FileCount = totalFiles,
                TotalBytes = totalBytes,
                DuplicateFiles = 0,
                DuplicateBytes = 0
            };
        }
    }

    internal static bool TryGetDirHandle(
        RepoSnapshotView snapshot,
        ScanRootId scanRootId,
        DirId dirId,
        out DirHandle handle)
    {
        var dirs = snapshot.Snapshots[scanRootId].Dirs;
        for (var i = 0; i < dirs.Count; i++)
        {
            if (dirs[i].DirId != dirId)
                continue;

            handle = new DirHandle(scanRootId, i);
            return true;
        }

        handle = DirHandle.Invalid;
        return false;
    }

    internal static string BuildDirPath(RepoSnapshotView snapshot, DirHandle handle)
    {
        var parts = new Stack<string>();
        var current = handle;

        while (true)
        {
            var rec = snapshot.GetDirRecord(current);
            var name = snapshot.DecodeDirName(current);
            if (!string.IsNullOrWhiteSpace(name))
                parts.Push(name);

            if (rec.ParentDirId < 0)
                break;

            if (!TryGetDirHandle(snapshot, current.ScanRootId, rec.ParentDirId, out current))
                break;
        }

        return string.Join("/", parts);
    }

    internal static FakeHashIndex BuildEmptyHashIndex()
    {
        return new FakeHashIndex
        {
            GetGroupsPageImpl = (_, _, _) => new DuplicateGroupPage(0, 0, ReadOnlyMemory<HashGroupDescriptor>.Empty),
            GetGroupsPageWithFilterImpl = (_, _, _, _) => new DuplicateGroupPage(0, 0, ReadOnlyMemory<HashGroupDescriptor>.Empty),
            GetGroupFilesImpl = _ => ReadOnlySpan<FileHandle>.Empty
        };
    }
}
