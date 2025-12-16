using System.Collections.Generic;
using System.Linq;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Models;
using Xunit;

namespace DuplicateFileFinderLibTests.TestUtils;

public class RepoUtil
{
    internal static RepoSnapshotView MakeSnapshot(long scanRootId, DirRecord[] dirs,
        FileRecord[] files)
    {
        // String pool layout:
        //   [dir0.name][dir0.err][dir1.name][dir1.err]...[file0.name][file0.err]...
        var strings = new string[dirs.Length * 2 + files.Length * 2];
        var w = 0;

        for (var i = 0; i < dirs.Length; i++)
        {
            strings[w++] = dirs[i].Name;
            strings[w++] = dirs[i].ErrorMessage ?? string.Empty;
        }

        for (var i = 0; i < files.Length; i++)
        {
            strings[w++] = files[i].Name;
            strings[w++] = files[i].ErrorMessage ?? string.Empty;
        }

        var stringPool = PackedStringPool.FromStrings(strings);

        var newDirs = new DirRecordV2[dirs.Length];
        for (var i = 0; i < dirs.Length; i++)
        {
            var d = dirs[i];
            newDirs[i] = new DirRecordV2
            {
                DirId = d.DirId,
                ParentDirId = d.ParentDirId ?? -1,
                NameStrIdx = i * 2,
                ErrorMessageStrIdx = i * 2 + 1,
                LastSeenScanSequence = d.LastSeenScanSequence,
                Status = d.Status,
                ModifiedTicks = d.Modified?.UtcTicks ?? 0,
                CreatedTicks = d.Created?.UtcTicks ?? 0
            };
        }

        var fileBase = dirs.Length * 2;
        var newFiles = new FileRecordV2[files.Length];
        for (var i = 0; i < files.Length; i++)
        {
            var f = files[i];
            newFiles[i] = new FileRecordV2
            {
                FileId = f.FileId,
                DirId = f.DirId,
                NameStrIdx = fileBase + i * 2,
                ErrorMessageStrIdx = fileBase + i * 2 + 1,
                Size = f.Size,
                Hash = f.Hash,
                ModifiedTicks = f.Modified?.UtcTicks ?? 0,
                CreatedTicks = f.Created?.UtcTicks ?? 0,
                LastSeenScanSequence = f.LastSeenScanSequence,
                Status = f.Status
            };
        }

        return new RepoSnapshotView
        {
            Snapshots = new Dictionary<long, ScanRootSnapshotView>
            {
                [scanRootId] = new()
                {
                    ScanRootId = scanRootId,
                    StringPool = stringPool,
                    Dirs = newDirs,
                    Files = newFiles
                }
            },
            ScanRoots = null
        };
            
    }

    internal static void AssertSetEqual<T>(T[] expected, T[] actual)
        where T : struct
    {
        Assert.Equal(
            expected.OrderBy(x => x).ToArray(),
            actual.OrderBy(x => x).ToArray());
    }

    internal static DirHandle[] Sort(DirHandle[] a)
    {
        return a.OrderBy(h => h.ScanRootId).ThenBy(h => h.Index).ToArray();
    }

    internal static FileHandle[] Sort(FileHandle[] a)
    {
        return a.OrderBy(h => h.ScanRootId).ThenBy(h => h.Index).ToArray();
    }
}