using System;
using System.Linq;
using DuplicateFileFinderLib.IO;
using Xunit;

namespace DuplicateFileFinderLibTests.TestUtils;

public static class AssertRows
{
    public static void ContainsFile(CsvRow[] rows, string path)
        => Assert.Contains(rows, r => r.Kind == KindEnum.File && r.Path == path);

    public static void ContainsFolder(CsvRow[] rows, string path)
        => Assert.Contains(rows, r => r.Kind == KindEnum.Folder && r.Path == path);

    public static void InSameGroup(CsvRow[] rows, string p1, string p2)
    {
        var g1 = rows.First(r => r.Path == p1 && r.Kind == KindEnum.File).Group;
        var g2 = rows.First(r => r.Path == p2 && r.Kind == KindEnum.File).Group;
        Assert.True(g1 >= 0);
        Assert.Equal(g1, g2);
    }

    public static void CreationTimeIs(CsvRow[] rows, string path, DateTimeOffset expectedUtc)
    {
        var row = rows.First(r => r.Path == path);
        Assert.Equal(expectedUtc, row.CreationTimeUtc);
    }
}