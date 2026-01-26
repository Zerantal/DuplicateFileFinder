using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

[MemoryPackable(SerializeLayout.Sequential)]
public sealed partial class Segment<T>
    where T : struct
{
    public long StartId { get; init; }

    public required T[] Values { get; init; }

    public required ulong[] PresentBits { get; init; }

    [MemoryPackIgnore]
    public long EndId => StartId + Values.Length - 1;

    public bool IsPresent(int index)
    {
        if ((uint)index >= (uint)Values.Length)
            return false;

        int word = index >> 6;
        int bit = index & 63;
        return (PresentBits[word] & (1UL << bit)) != 0;
    }

    public static Segment<T> BuildSegment(
        KeyValuePair<long, T>[] sorted,
        int fromInclusive,
        int toInclusive,
        long segStart,
        long segEnd)
    {
        long lenL = segEnd - segStart + 1;
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
            int offset = (int)(k - segStart);

            values[offset] = v;
            SetBit(bits, offset);
        }

        return new Segment<T>
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
