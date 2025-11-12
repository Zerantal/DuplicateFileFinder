// Repo/Models/HashKey.cs

using System.Buffers.Binary;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial struct HashKey : IEquatable<HashKey>
{
    public ulong A; // first 8 bytes
    public ulong B; // next 8 bytes

    public static HashKey From(ReadOnlySpan<byte> md5)
    {
        if (md5.Length != 16) throw new ArgumentException("MD5 expected");
        return new HashKey
        {
            A = BinaryPrimitives.ReadUInt64LittleEndian(md5[..8]),
            B = BinaryPrimitives.ReadUInt64LittleEndian(md5.Slice(8, 8))
        };
    }

    public static void ToByteArray(HashKey key, Span<byte> buffer)
    {
        if (buffer.Length < 16) throw new ArgumentException("Destination must be at least 16 bytes.");
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[..8], key.A);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(8, 8), key.B);
    }

    public readonly bool Equals(HashKey other) => A == other.A && B == other.B;
    public override readonly bool Equals(object? obj) => obj is HashKey h && Equals(h);
    public override readonly int GetHashCode() => HashCode.Combine(A, B);
}