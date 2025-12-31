// DuplicateFileFinderLibTests/Repository/Models/PackedStringBuilderTests.cs

using System;

using DuplicateFileFinderLib.Repository.Storage;

using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Models;

public sealed class PackedStringBuilderTests
{
    [Fact]
    public void Intern_SameString_ReturnsSameIndex()
    {
        var b = new PackedStringBuilder();

        var i1 = b.Intern("hello");
        var i2 = b.Intern("hello");

        Assert.Equal(i1, i2);
        Assert.Equal(1, b.Count);
    }

    [Fact]
    public void Intern_DifferentStrings_ReturnDifferentIndices_AndCountMatches()
    {
        var b = new PackedStringBuilder();

        var a = b.Intern("a");
        var b2 = b.Intern("b");
        var c = b.Intern("c");

        Assert.NotEqual(a, b2);
        Assert.NotEqual(b2, c);
        Assert.NotEqual(a, c);
        Assert.Equal(3, b.Count);
    }

    [Fact]
    public void Intern_EmptyString_Works()
    {
        var b = new PackedStringBuilder();

        var idx = b.Intern(string.Empty);

        Assert.Equal(0, idx);
        Assert.Equal(1, b.Count);

        var pool = b.Build();
        Assert.Equal(string.Empty, pool.GetString(idx));
    }

    [Fact]
    public void InternOrMinusOne_Null_ReturnsMinusOne_AndDoesNotIncreaseCount()
    {
        var b = new PackedStringBuilder();

        var idx = b.InternOrMinusOne(null);

        Assert.Equal(-1, idx);
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void InternOrMinusOne_NonNull_InternsAndReturnsIndex()
    {
        var b = new PackedStringBuilder();

        var idx = b.InternOrMinusOne("err");

        Assert.Equal(0, idx);
        Assert.Equal(1, b.Count);

        var pool = b.Build();
        Assert.Equal("err", pool.GetString(idx));
    }

    [Fact]
    public void Build_RoundTripsAllStrings_InInsertionOrder()
    {
        var b = new PackedStringBuilder();

        var i0 = b.Intern("alpha");
        var i1 = b.Intern("beta");
        var i2 = b.Intern("gamma");

        Assert.Equal(0, i0);
        Assert.Equal(1, i1);
        Assert.Equal(2, i2);

        var pool = b.Build();

        Assert.Equal("alpha", pool.GetString(0));
        Assert.Equal("beta", pool.GetString(1));
        Assert.Equal("gamma", pool.GetString(2));
    }

    [Fact]
    public void Build_UsesUtf8_CorrectlyForNonAscii()
    {
        var b = new PackedStringBuilder();

        var i0 = b.Intern("café");
        var i1 = b.Intern("東京");
        var i2 = b.Intern("🙂");

        var pool = b.Build();

        Assert.Equal("café", pool.GetString(i0));
        Assert.Equal("東京", pool.GetString(i1));
        Assert.Equal("🙂", pool.GetString(i2));
    }

    [Fact]
    public void Build_ReturnsIndependentPool_NotAffectedByFurtherInterns()
    {
        var b = new PackedStringBuilder();

        var i0 = b.Intern("one");
        var pool1 = b.Build();

        // mutate builder after building pool1
        var i1 = b.Intern("two");
        var pool2 = b.Build();

        Assert.Equal("one", pool1.GetString(i0));

        // pool1 must not be able to see "two" (would throw if accessed by index)
        Assert.Throws<ArgumentOutOfRangeException>(() => pool1.GetString(i1));

        Assert.Equal("one", pool2.GetString(i0));
        Assert.Equal("two", pool2.GetString(i1));
    }

    [Fact]
    public void Reset_ClearsAllState_WhenKeepCapacityTrue()
    {
        var b = new PackedStringBuilder();

        _ = b.Intern("a");
        _ = b.Intern("b");
        Assert.Equal(2, b.Count);

        b.Reset(keepCapacity: true);

        Assert.Equal(0, b.Count);

        var idx = b.Intern("a");
        Assert.Equal(0, idx);
        Assert.Equal(1, b.Count);

        var pool = b.Build();
        Assert.Equal("a", pool.GetString(0));
    }

    [Fact]
    public void Intern_Null_Throws()
    {
        var b = new PackedStringBuilder();
        Assert.Throws<ArgumentNullException>(() => b.Intern(null!));
    }
}
