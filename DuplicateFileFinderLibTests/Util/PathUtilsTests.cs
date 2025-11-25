// DuplicateFileFinderLibTests/Util/PathUtilsTests.cs

using System;
using System.IO;
using DuplicateFileFinderLib.Util;
using Xunit;

namespace DuplicateFileFinderLibTests.Util;

public sealed class PathUtilsTests
{
    [Fact]
    public void NormalizePath_Unix_PreservesBackslashInFileName()
    {
        if (Path.DirectorySeparatorChar == '\\')
            return; // Windows: '\' cannot appear in file names, test not applicable.

        const string input = "/mnt/data/net9.0/TestData\\ScanLocationTest_actual.csv";
        var result = PathUtils.NormalizePath(input);

        // Only '/' segments should be normalised; the '\' must remain.
        Assert.Equal(input, result);
    }

    [Fact]
    public void NormalizePath_Unix_CollapsesDotAndDotDot()
    {
        if (Path.DirectorySeparatorChar == '\\')
            return;

        var input = "/a/./b/../c//d/";
        var result = PathUtils.NormalizePath(input);

        // /a/c/d is the canonical collapsed path
        Assert.Equal("/a/c/d", result);
    }

    [Fact]
    public void NormalizePath_Unix_RelativePath_IsCollapsedWithoutLeadingSlash()
    {
        if (Path.DirectorySeparatorChar == '\\')
            return;

        var input = "foo/./bar/../baz";
        var result = PathUtils.NormalizePath(input);

        Assert.Equal("foo/baz", result);
    }

    [Fact]
    public void NormalizePath_Unix_ForceTrailingSlash_AppendsSlash()
    {
        if (Path.DirectorySeparatorChar == '\\')
            return;

        var input = "/a/b/c";
        var result = PathUtils.NormalizePath(input, true);

        Assert.Equal("/a/b/c/", result);
    }

    [Fact]
    public void NormalizePath_Windows_NormalizesSeparatorsAndCollapsesSegments()
    {
        if (Path.DirectorySeparatorChar != '\\')
            return; // Unix: irrelevant

        var input = @"C:\foo\.\bar\..\baz//qux\";
        var result = PathUtils.NormalizePath(input);

        // On Windows we normalise to C:/foo/baz/qux
        Assert.Equal("C:/foo/baz/qux", result);
    }

    [Fact]
    public void NormalizePath_Windows_AbsolutePathWithoutDrive_IsRooted()
    {
        if (Path.DirectorySeparatorChar != '\\')
            return;

        var input = @"\foo\bar";
        var result = PathUtils.NormalizePath(input);

        Assert.Equal("/foo/bar", result);
    }

    [Fact]
    public void NormalizePath_Windows_ForceTrailingSlash_AppendsSlash()
    {
        if (Path.DirectorySeparatorChar != '\\')
            return;

        var input = @"C:\foo\bar";
        var result = PathUtils.NormalizePath(input, true);

        Assert.Equal("C:/foo/bar/", result);
    }

    [Fact]
    public void NormalizePath_ThrowsOnNullOrWhitespace()
    {
        Assert.Throws<ArgumentNullException>(() => PathUtils.NormalizePath(null!));
        Assert.Throws<ArgumentNullException>(() => PathUtils.NormalizePath(""));
        Assert.Throws<ArgumentNullException>(() => PathUtils.NormalizePath("   "));
    }
}