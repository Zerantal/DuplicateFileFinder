using System.Text;

namespace DuplicateFileFinderLib.Repository.Storage;

/// <summary>
/// Builds a PackedStringPool by interning strings into a single UTF-8 byte buffer + offsets.
/// Thread-safe only if the caller serializes access.
/// </summary>
public sealed class PackedStringBuilder
{
    private readonly Dictionary<string, int> _indexByString;
    private readonly List<int> _offsets; // start offsets for each interned string
    private byte[] _buffer;
    private int _length;

    public PackedStringBuilder(int initialCapacityStrings = 1024, int initialCapacityBytes = 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacityStrings);
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacityBytes);

        _indexByString = new Dictionary<string, int>(initialCapacityStrings, StringComparer.Ordinal);
        _offsets = new List<int>(initialCapacityStrings);
        _buffer = initialCapacityBytes == 0 ? [] : new byte[initialCapacityBytes];
        _length = 0;
    }

    public int Count => _offsets.Count;

    public int Intern(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (_indexByString.TryGetValue(value, out var existing))
            return existing;

        var byteCount = Encoding.UTF8.GetByteCount(value);
        EnsureCapacity(_length + byteCount);

        // Record start offset
        var index = _offsets.Count;
        _offsets.Add(_length);

        // Encode UTF-8 directly into backing buffer
        Encoding.UTF8.GetBytes(value, 0, value.Length, _buffer, _length);
        _length += byteCount;

        _indexByString.Add(value, index);
        return index;
    }

    public int InternOrMinusOne(string? value)
        => value is null ? -1 : Intern(value);

    public Models.PackedStringPool Build()
    {
        // Offsets array has a sentinel at the end
        var n = _offsets.Count;
        var offsets = new int[n + 1];

        for (int i = 0; i < n; i++)
            offsets[i] = _offsets[i];

        offsets[n] = _length; // sentinel

        var data = _length == 0 ? [] : _buffer.AsSpan(0, _length).ToArray();

        return new Models.PackedStringPool(data, offsets);
    }

    public void Reset(bool keepCapacity = true)
    {
        _indexByString.Clear();
        _offsets.Clear();
        _length = 0;

        if (!keepCapacity)
            _buffer = [];
    }

    private void EnsureCapacity(int required)
    {
        if ((uint)required <= (uint)_buffer.Length)
            return;

        // Grow to next power-ish (2x) but at least required
        var newSize = _buffer.Length == 0 ? 256 : _buffer.Length * 2;
        if (newSize < required)
            newSize = required;

        var newBuf = new byte[newSize];
        if (_length > 0)
            Buffer.BlockCopy(_buffer, 0, newBuf, 0, _length);
        _buffer = newBuf;
    }
}
