using System.Buffers;

namespace DuplicateFileFinderLib.Hashing;

public readonly struct PooledHash(byte[] buffer, int length) : IDisposable
{
    private readonly byte[]? _buffer = buffer;

    public ReadOnlyMemory<byte> Bytes => _buffer?.AsMemory(0, length) ?? ReadOnlyMemory<byte>.Empty;

    public void Dispose()
    {
        if (_buffer is not null)
            ArrayPool<byte>.Shared.Return(_buffer);
    }
}