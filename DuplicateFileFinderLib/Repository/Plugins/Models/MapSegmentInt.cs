// DuplicateFileFinderLib/Repository/Plugins/Models/MapSegmentInt.cs

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

[MemoryPackable(SerializeLayout.Sequential)]
public sealed partial class MapSegmentInt<T>
    where T : struct
{
    public int StartId { get; init; }

    public required T[] Values { get; init; }

    public required ulong[] PresentBits { get; init; }

    [MemoryPackIgnore]
    public int EndId => StartId + Values.Length - 1;

    public bool IsPresent(int index)
    {
        if ((uint)index >= (uint)Values.Length)
            return false;

        int word = index >> 6;
        int bit = index & 63;
        return (PresentBits[word] & (1UL << bit)) != 0;
    }

    public static MapSegmentInt<T> BuildSegment(
        KeyValuePair<int, T>[] sorted,
        int fromInclusive,
        int toInclusive,
        int segStart,
        int segEnd)
    {
        long lenL = (long)segEnd - segStart + 1;
        if (lenL <= 0 || lenL > int.MaxValue)
            throw new InvalidOperationException($"Segment length out of range: {lenL}");

        int len = (int)lenL;

        var values = new T[len];

        // ceil(len / 64)
        int words = (len + 63) >> 6;
        var bits = new ulong[words];

        for (int i = fromInclusive; i <= toInclusive; i++)
        {
            var (k, v) = (sorted[i].Key, sorted[i].Value);
            int offset = k - segStart;

            values[offset] = v;
            SetBit(bits, offset);
        }

        return new MapSegmentInt<T>
        {
            StartId = segStart,
            Values = values,
            PresentBits = bits
        };
    }

    private static void SetBit(ulong[] bits, int index)
    {
        int word = index >> 6;
        int bit = index & 63;
        bits[word] |= 1UL << bit;
    }
}

