// Repo/Models/HashKey.cs

using System.Buffers.Binary;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public readonly partial struct HashKey : IEquatable<HashKey>
{
    [MemoryPackOrder(0)] public readonly ulong A; // first 8 bytes
    [MemoryPackOrder(1)] public readonly ulong B; // next 8 bytes

    public HashKey(ReadOnlySpan<byte> hashBytes)
    {
        if (hashBytes.Length != 16) throw new ArgumentException("16 byte span required");
        A = BinaryPrimitives.ReadUInt64LittleEndian(hashBytes[..8]);
        B = BinaryPrimitives.ReadUInt64LittleEndian(hashBytes.Slice(8, 8));
    }

    public HashKey(ulong a = 0, ulong b = 0)
    {
        A = a;
        B = b;
    }

    public void ToByteArray(Span<byte> buffer)
    {
        if (buffer.Length < 16) throw new ArgumentException("Destination must be at least 16 bytes.");
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[..8], A);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(8, 8), B);
    }

    public bool Equals(HashKey other)
    {
        return A == other.A && B == other.B;
    }

    public override bool Equals(object? obj)
    {
        return obj is HashKey h && Equals(h);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(A, B);
    }

    public static bool operator ==(HashKey left, HashKey right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(HashKey left, HashKey right)
    {
        return !(left == right);
    }
}