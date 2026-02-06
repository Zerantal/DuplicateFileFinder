// DuplicateFileFinderLib/Repository/Storage/StreamBufferWriter.cs

using System.Buffers;
using System.Runtime.CompilerServices;

namespace DuplicateFileFinderLib.Repository.Storage;

/// <summary>
/// Serialize MemoryPack payloads directly to a stream using a fixed-size buffer,
/// avoiding large SharedArrayPool rentals.
/// </summary>
internal sealed class StreamBufferWriter : IBufferWriter<byte>, IDisposable
{
    private readonly Stream _stream;
    private byte[] _buffer;
    private int _pos;

    public StreamBufferWriter(Stream stream, int initialBufferSize = 256 * 1024)
    {
        if (initialBufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(initialBufferSize));
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _buffer = GC.AllocateUninitializedArray<byte>(initialBufferSize);
        _pos = 0;
    }

    public void Advance(int count)
    {
        if ((uint)count > (uint)(_buffer.Length - _pos))
            throw new ArgumentOutOfRangeException(nameof(count));

        _pos += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_pos);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_pos);
    }

    public void Flush()
    {
        if (_pos == 0) return;
        _stream.Write(_buffer, 0, _pos);
        _pos = 0;
    }

    public void Dispose() => Flush();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int sizeHint)
    {
        if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
        if (sizeHint == 0) sizeHint = 1;

        int remaining = _buffer.Length - _pos;
        if (sizeHint <= remaining) return;

        // Flush buffered data first (cheap, and keeps growth logic simple).
        if (_pos != 0)
        {
            Flush();
            if (sizeHint <= _buffer.Length) return;
        }

        Grow(sizeHint);
    }

    private void Grow(int requiredSize)
    {
        // requiredSize is the contiguous span we must be able to return at position 0.
        // We allocate exactly what's required.
        if (requiredSize <= 0) throw new ArgumentOutOfRangeException(nameof(requiredSize));

        // If someone asks for something near int.MaxValue this can overflow elsewhere; fail early.
        // Let the runtime throw OOM if the allocation itself fails (do not wrap/translate).
        var newBuf = GC.AllocateUninitializedArray<byte>(requiredSize);

        // Normally _pos is 0 here because we flushed before growing, but keep correctness.
        if (_pos != 0)
            Buffer.BlockCopy(_buffer, 0, newBuf, 0, _pos);

        _buffer = newBuf;
    }
}
