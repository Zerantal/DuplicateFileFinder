// DuplicateFileFinderLibTests/Repository/Models/PackedStringPoolTests.cs

using System;
using System.Linq;
using Xunit;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLibTests.Repository.Models;

public sealed class PackedStringPoolTests
{
    [Fact]
    public void FromStrings_RoundTrips_AllStrings()
    {
        var input = new[]
        {
            "",                // empty
            "a",
            "hello",
            "The quick brown fox jumps over the lazy dog",
            "こんにちは",        // non-ASCII
            "😀😃😄😁",          // surrogate pairs
            "x\0y",             // embedded NUL
        };

        var pool = PackedStringPool.FromStrings(input);

        Assert.Equal(input.Length, pool.Count);

        for (int i = 0; i < input.Length; i++)
            Assert.Equal(input[i], pool.GetString(i));
    }

    [Fact]
    public void FromStrings_SentinelOffset_Equals_DataLength()
    {
        var input = new[] { "abc", "", "defg", "h" };
        var pool = PackedStringPool.FromStrings(input);

        Assert.NotNull(pool.Offsets);
        Assert.NotNull(pool.Data);

        Assert.Equal(pool.Count + 1, pool.Offsets.Length);
        Assert.Equal(pool.Data.Length, pool.Offsets[^1]);
    }

    [Fact]
    public void FromStrings_Offsets_AreNonDecreasing_AndWithinBounds()
    {
        var input = new[] { "abc", "", "defg", "h", "こんにちは", "😀" };
        var pool = PackedStringPool.FromStrings(input);

        // Offsets must be monotonic and inside [0, Data.Length]
        int last = -1;
        foreach (var off in pool.Offsets)
        {
            Assert.InRange(off, 0, pool.Data.Length);
            Assert.True(off >= last, $"Offsets must be nondecreasing (saw {off} after {last}).");
            last = off;
        }
    }

    [Fact]
    public void GetString_OutOfRange_Throws()
    {
        var pool = PackedStringPool.FromStrings(new[] { "a", "b" });

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.GetString(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.GetString(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.GetString(int.MaxValue));
    }

    [Fact]
    public void MaterializeAllStrings_Equals_Original()
    {
        var input = Enumerable.Range(0, 1000)
            .Select(i => i % 3 == 0 ? "" : $"s{i}")
            .ToArray();

        var pool = PackedStringPool.FromStrings(input);
        var roundTrip = pool.MaterializeAllStrings();

        Assert.Equal(input, roundTrip);
    }

    [Fact]
    public void EmptyInput_ProducesEmptyPoolWithSentinelOffsetOnly()
    {
        var pool = PackedStringPool.FromStrings(Array.Empty<string>());

        Assert.Equal(0, pool.Count);
        Assert.Empty(pool.Data);

        Assert.Single(pool.Offsets);
        Assert.Equal(0, pool.Offsets[0]);
    }

    [Fact]
    public void FromStrings_Throws_OnNullInputArrayElement()
    {
        // Current implementation will throw because Encoding.GetByteCount(null) throws.
        // If you later choose to treat null as "", update this test accordingly.
        var input = new string[] { "a", null!, "b" };

        Assert.ThrowsAny<Exception>(() => PackedStringPool.FromStrings(input));
    }
}
