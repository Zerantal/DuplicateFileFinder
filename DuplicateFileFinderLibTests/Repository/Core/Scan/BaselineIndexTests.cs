// DuplicateFileFinderLibTests/Repository/Core/Scan/BaselineIndexTests.cs

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;

using DuplicateFileFinderLibTests.TestUtils;

using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Core.Scan;

public sealed class BaselineIndexTests
{
    [Fact]
    public void Ctor_NullView_ProducesEmptyIndex()
    {
        var idx = new BaselineIndex(view: null);

        Assert.False(idx.TryGetChildDirMap(0, out _));
        Assert.False(idx.TryGetChildFileMap(0, out _));
        Assert.False(idx.TryGetBaselineFile(123, out _));
    }

    [Fact]
    public void Ctor_IgnoresInvalidDirs_AndIndexesValidDirs()
    {
        // parent=10: include 3 invalid dirs and 1 valid
        var view = new TestSnapshotViewBuilder()
            .Dir(dirId: 1, parentDirId: 10, status: ScanEntryStatus.None, name: "Skip-None", lastSeenScanSequence: 1)  // ignored (Status None)
            .Dir(dirId: 2, parentDirId: -1, status: ScanEntryStatus.Enumerated, name: "Skip-Neg", lastSeenScanSequence: 1)  // ignored (ParentDirId < 0)
            .Dir(dirId: 3, parentDirId: 10, status: ScanEntryStatus.Enumerated, name: "", lastSeenScanSequence: 1)  // ignored (empty name)
            .Dir(dirId: 4, parentDirId: 10, status: ScanEntryStatus.Enumerated, name: "Keep-Me", lastSeenScanSequence: 7)  // kept
            .Build();

        var idx = new BaselineIndex(view);

        Assert.True(idx.TryGetChildDirMap(10, out var map));
        Assert.Single(map);

        Assert.True(map.TryGetValue("Keep-Me", out var v));
        Assert.Equal(4, v.dirId);
        Assert.Equal(ScanEntryStatus.Enumerated, v.status);
        Assert.Equal(7, v.lastSeen);

        Assert.False(idx.TryGetChildDirMap(-1, out _));
    }

    [Fact]
    public void Ctor_IndexesFilesById_EvenWhenNameEmpty_ButSkipsNameMap()
    {
        // file 100 has empty name => still indexed by id (BaselineIndex adds to _fileById),
        // but should not be present in per-dir name map.
        var view = new TestSnapshotViewBuilder()
            .File(fileId: 100, dirId: 20, status: ScanEntryStatus.Enumerated, name: "", lastSeenScanSequence: 5)
            .File(fileId: 101, dirId: 20, status: ScanEntryStatus.None, name: "Skip", lastSeenScanSequence: 5) // ignored entirely (Status None)
            .File(fileId: 102, dirId: 20, status: ScanEntryStatus.Enumerated, name: "A.txt", lastSeenScanSequence: 5)
            .Build();

        var idx = new BaselineIndex(view);

        Assert.True(idx.TryGetBaselineFile(100, out var f100));
        Assert.Equal(100, f100.FileId);

        Assert.False(idx.TryGetBaselineFile(101, out _));

        Assert.True(idx.TryGetChildFileMap(20, out var map));
        Assert.Single(map);
        Assert.True(map.ContainsKey("A.txt"));
        Assert.False(map.ContainsKey(""));
    }

    [Fact]
    public void Ctor_NameCollision_PrefersNonDeletedOverDeleted()
    {
        var view = new TestSnapshotViewBuilder()
            .Dir(dirId: 1, parentDirId: 10, status: ScanEntryStatus.Deleted, name: "X", lastSeenScanSequence: 100)
            .Dir(dirId: 2, parentDirId: 10, status: ScanEntryStatus.Enumerated, name: "X", lastSeenScanSequence: 1)
            .Build();

        var idx = new BaselineIndex(view);

        Assert.True(idx.TryGetChildDirMap(10, out var map));
        Assert.Single(map);

        Assert.True(map.TryGetValue("X", out var v));
        Assert.Equal(2, v.dirId);
        Assert.Equal(ScanEntryStatus.Enumerated, v.status);
    }

    [Fact]
    public void Ctor_NameCollision_WhenSameDeletionState_PrefersHigherLastSeen()
    {
        var view = new TestSnapshotViewBuilder()
            .File(fileId: 10, dirId: 77, status: ScanEntryStatus.Enumerated, name: "f.bin", lastSeenScanSequence: 5)
            .File(fileId: 11, dirId: 77, status: ScanEntryStatus.Enumerated, name: "f.bin", lastSeenScanSequence: 9)
            .Build();

        var idx = new BaselineIndex(view);

        Assert.True(idx.TryGetChildFileMap(77, out var map));
        Assert.Single(map);

        Assert.True(map.TryGetValue("f.bin", out var v));
        Assert.Equal(11, v.fileId);
        Assert.Equal(9, v.lastSeen);
    }

    [Fact]
    public void Ctor_NameCollision_Tie_KeepsExisting()
    {
        // Tie (same deleted-ness + same lastSeen) => PreferCandidate returns false => first wins.
        var view = new TestSnapshotViewBuilder()
            .Dir(dirId: 1, parentDirId: 10, status: ScanEntryStatus.Enumerated, name: "Tie", lastSeenScanSequence: 5)
            .Dir(dirId: 2, parentDirId: 10, status: ScanEntryStatus.Enumerated, name: "Tie", lastSeenScanSequence: 5)
            .Build();

        var idx = new BaselineIndex(view);

        Assert.True(idx.TryGetChildDirMap(10, out var map));
        Assert.Single(map);

        Assert.True(map.TryGetValue("Tie", out var v));
        Assert.Equal(1, v.dirId);
        Assert.Equal(5, v.lastSeen);
    }
}
