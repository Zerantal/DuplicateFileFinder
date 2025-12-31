using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using DuplicateFileFinderLib.Repository.Storage.Models;

using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Models;

public sealed class HashKeyTests
{
    [Fact]
    public void HashKey_From16ByteSpan_PopulatesAandBCorrectly()
    {
        var bytes = new byte[16];
        // A = 0x0102030405060708, B = 0x1112131415161718 (little-endian)
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0, 8), 0x0102030405060708UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8, 8), 0x1112131415161718UL);

        var key = new HashKey(bytes);

        Assert.Equal(0x0102030405060708UL, key.A);
        Assert.Equal(0x1112131415161718UL, key.B);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(17)]
    public void HashKey_FromInvalidLength_ThrowsArgumentException(int length)
    {
        var bytes = new byte[length];
        Assert.Throws<ArgumentException>(() => new HashKey(bytes));
    }

    [Fact]
    public void HashKey_ToByteArray_RoundTripsOriginalBytes()
    {
        var original = new byte[16];
        new Random(123).NextBytes(original);

        var key = new HashKey(original);
        var roundTripped = new byte[16];
        key.ToByteArray(roundTripped);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void HashKey_EqualityAndGetHashCode_WorkForIdenticalAndDistinctValues()
    {
        var k1 = new HashKey(1, 2);
        var k2 = new HashKey(1, 2);
        var k3 = new HashKey(3, 4);

        Assert.True(k1 == k2);
        Assert.False(k1 != k2);
        Assert.True(k1.Equals(k2));
        Assert.False(k1.Equals(k3));

        Assert.Equal(k1.GetHashCode(), k2.GetHashCode());
        Assert.NotEqual(k1.GetHashCode(), k3.GetHashCode());

        var set = new HashSet<HashKey> { k1, k2, k3 };
        Assert.Equal(2, set.Count);
    }
}
