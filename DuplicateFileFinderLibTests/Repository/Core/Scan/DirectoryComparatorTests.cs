// DuplicateFileFinderLibTests/Repository/Core/Scan/DirectoryComparatorTests.cs

using System;
using System.Collections.Generic;
using System.Linq;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Core.Scan;

public sealed class DirectoryComparatorTests
{
    [Fact]
    public void Begin_CopiesBaselineMaps_AndDoesNotMutateBaseline()
    {
        // Baseline: 2 dirs + 2 files under parent dirId=50
        var view = new TestSnapshotViewBuilder()
            .Dir(dirId: 50, parentDirId: -1, status: ScanEntryStatus.Enumerated, name: "ROOT", lastSeenScanSequence: 1) // ignored by BaselineIndex (parent < 0)
            .Dir(dirId: 101, parentDirId: 50, status: ScanEntryStatus.Enumerated, name: "D1", lastSeenScanSequence: 1)
            .Dir(dirId: 102, parentDirId: 50, status: ScanEntryStatus.Enumerated, name: "D2", lastSeenScanSequence: 2)
            .File(fileId: 201, dirId: 50, status: ScanEntryStatus.Enumerated, name: "F1", lastSeenScanSequence: 3)
            .File(fileId: 202, dirId: 50, status: ScanEntryStatus.Deleted, name: "F2", lastSeenScanSequence: 4)
            .Build();

        var baseline = new BaselineIndex(view);
        var comparator = new DirectoryComparator(baseline);

        var parent = new DirCursor(50);

        // Begin #1: consume a couple from the working context
        var ctx1 = comparator.Begin(parent);

        var d1 = comparator.TryConsumeExpectedDirId(ref ctx1, "D1");
        var f2 = comparator.TryConsumeExpectedFileId(ref ctx1, "F2");

        var remainingDirs1 = comparator.ConsumeRemainingExpectedDirs(ref ctx1).Select(x => x.name).ToArray();
        var remainingFiles1 = comparator.ConsumeRemainingExpectedFiles(ref ctx1).Select(x => x.name).ToArray();

        // Begin #2: should still see full baseline set (i.e., baseline was not mutated)
        var ctx2 = comparator.Begin(parent);
        var remainingDirs2 = comparator.ConsumeRemainingExpectedDirs(ref ctx2).Select(x => x.name).OrderBy(x => x).ToArray();
        var remainingFiles2 = comparator.ConsumeRemainingExpectedFiles(ref ctx2).Select(x => x.name).OrderBy(x => x).ToArray();

        Assert.Equal(101, d1);
        Assert.Equal(202, f2);

        Assert.Equal(new[] { "D2" }, remainingDirs1);
        Assert.Equal(new[] { "F1" }, remainingFiles1);

        Assert.Equal(new[] { "D1", "D2" }, remainingDirs2);
        Assert.Equal(new[] { "F1", "F2" }, remainingFiles2);
    }

    [Fact]
    public void TryConsumeExpectedDirId_RemovesAndReturnsId_WhenPresent()
    {
        var expectedDirs = new Dictionary<string, (long id, string name, ScanEntryStatus status, long lastSeen)>(StringComparer.Ordinal)
        {
            ["A"] = (1, "A", ScanEntryStatus.Enumerated, 10),
        };

        var expectedFiles = new Dictionary<string, (long id, string name, ScanEntryStatus status, long lastSeen)>(StringComparer.Ordinal);

        var ctx = new DirEnumerationContext(parentDirId: 42, expectedDirs, expectedFiles);
        var comp = new DirectoryComparator(baseline: new BaselineIndex(view: null));

        var id = comp.TryConsumeExpectedDirId(ref ctx, "A");

        Assert.Equal(1, id);
        Assert.Empty(ctx.ExpectedDirs);
    }

    [Fact]
    public void TryConsumeExpectedFileId_RemovesAndReturnsId_WhenPresent()
    {
        var expectedDirs = new Dictionary<string, (long id, string name, ScanEntryStatus status, long lastSeen)>(StringComparer.Ordinal);

        var expectedFiles = new Dictionary<string, (long id, string name, ScanEntryStatus status, long lastSeen)>(StringComparer.Ordinal)
        {
            ["b.txt"] = (2, "b.txt", ScanEntryStatus.Enumerated, 10),
        };

        var ctx = new DirEnumerationContext(parentDirId: 42, expectedDirs, expectedFiles);
        var comp = new DirectoryComparator(baseline: new BaselineIndex(view: null));

        var id = comp.TryConsumeExpectedFileId(ref ctx, "b.txt");

        Assert.Equal(2, id);
        Assert.Empty(ctx.ExpectedFiles);
    }

    [Fact]
    public void ConsumeRemainingExpected_ReturnsRemainingValues()
    {
        var expectedDirs = new Dictionary<string, (long id, string name, ScanEntryStatus status, long lastSeen)>(StringComparer.Ordinal)
        {
            ["D1"] = (101, "D1", ScanEntryStatus.Enumerated, 1),
            ["D2"] = (102, "D2", ScanEntryStatus.Enumerated, 2),
        };

        var expectedFiles = new Dictionary<string, (long id, string name, ScanEntryStatus status, long lastSeen)>(StringComparer.Ordinal)
        {
            ["F1"] = (201, "F1", ScanEntryStatus.Enumerated, 3),
        };

        var ctx = new DirEnumerationContext(parentDirId: 50, expectedDirs, expectedFiles);
        var comp = new DirectoryComparator(baseline: new BaselineIndex(view: null));

        var dirs = comp.ConsumeRemainingExpectedDirs(ref ctx).Select(x => x.name).OrderBy(x => x).ToArray();
        var files = comp.ConsumeRemainingExpectedFiles(ref ctx).Select(x => x.name).OrderBy(x => x).ToArray();

        Assert.Equal(new[] { "D1", "D2" }, dirs);
        Assert.Equal(new[] { "F1" }, files);
    }

    [Fact]
    public void Clear_EmptiesContext()
    {
        var expectedDirs = new Dictionary<string, (long id, string name, ScanEntryStatus status, long lastSeen)>(StringComparer.Ordinal)
        {
            ["A"] = (1, "A", ScanEntryStatus.Enumerated, 1),
        };

        var expectedFiles = new Dictionary<string, (long id, string name, ScanEntryStatus status, long lastSeen)>(StringComparer.Ordinal)
        {
            ["b.txt"] = (2, "b.txt", ScanEntryStatus.Enumerated, 1),
        };

        var ctx = new DirEnumerationContext(parentDirId: 7, expectedDirs, expectedFiles);
        var comp = new DirectoryComparator(baseline: new BaselineIndex(view: null));

        comp.Clear(ref ctx);

        Assert.Empty(ctx.ExpectedDirs);
        Assert.Empty(ctx.ExpectedFiles);

        Assert.Equal(-1, comp.TryConsumeExpectedDirId(ref ctx, "A"));
        Assert.Equal(-1, comp.TryConsumeExpectedFileId(ref ctx, "b.txt"));
    }
}
