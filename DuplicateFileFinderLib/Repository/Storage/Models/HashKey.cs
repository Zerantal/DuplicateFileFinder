// Repo/Models/HashKey.cs

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Storage.Models;

[MemoryPackable(SerializeLayout.Sequential)]
public readonly partial struct HashKey : IEquatable<HashKey>
{
    public ulong A { get; } // first 8 bytes
    public ulong B { get; } // next 8 bytes

    /// <summary>
    /// Sentinel: hash has not been computed yet. This is also the default(HashKey) value.
    /// </summary>
    public static readonly HashKey NotComputed = new();

    /// <summary>
    /// Sentinel: hash could not be computed (I/O error, permission issue, etc.).
    /// </summary>
    public static readonly HashKey CannotCompute = new(ulong.MaxValue, ulong.MaxValue);

    /// <summary>
    /// True if this value is the <see cref="NotComputed"/> sentinel.
    /// </summary>
    [MemoryPackIgnore]
    public bool IsNotComputed => Equals(NotComputed);

    /// <summary>
    /// True if this value is the <see cref="CannotCompute"/> sentinel.
    /// </summary>
    [MemoryPackIgnore]
    public bool IsCannotCompute => Equals(CannotCompute);

    /// <summary>
    /// True if this represents a real hash value (neither NotComputed nor CannotCompute).
    /// </summary>
    [MemoryPackIgnore]
    public bool IsComputed => !IsNotComputed && !IsCannotCompute;

    /// <summary>
    /// Construct from a 16-byte hash. This is intended for real content hashes.
    /// </summary>
    public HashKey(ReadOnlySpan<byte> hashBytes)
    {
        if (hashBytes.Length != 16) throw new ArgumentException("16 byte span required");
        A = BinaryPrimitives.ReadUInt64LittleEndian(hashBytes[..8]);
        B = BinaryPrimitives.ReadUInt64LittleEndian(hashBytes.Slice(8, 8));
    }

    public HashKey(ReadOnlyMemory<byte> hashBytes)
    {
        if (hashBytes.Length != 16) throw new ArgumentException("16 byte span required");
        A = BinaryPrimitives.ReadUInt64LittleEndian(hashBytes[..8].Span);
        B = BinaryPrimitives.ReadUInt64LittleEndian(hashBytes.Slice(8,8).Span);
    }

    /// <summary>
    /// Construct from raw 64-bit halves. Use this for sentinels or custom values.
    /// </summary>
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        unchecked
        {
            ulong x = A ^ B;
            return (int)x ^ (int)(x >> 32);
        }
    }

    public static bool operator ==(HashKey left, HashKey right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(HashKey left, HashKey right)
    {
        return !(left == right);
    }

    public override string ToString()
    {
        // 32-hex-character canonical representation with sentinel hints.
        if (IsNotComputed) return "<NotComputed>";
        if (IsCannotCompute) return "<CannotCompute>";
        return $"{A:X16}{B:X16}";
    }
}