using MemoryPack;

namespace MemPackBench.SegmentedDenseMapBench;

[MemoryPackable(SerializeLayout.Sequential)]
public sealed partial class SegmentDense<TKey, TValue>
    where TKey : struct, System.Numerics.IBinaryInteger<TKey>
    where TValue : struct
{
    public TKey StartKey { get; init; }

    public required TValue[] Values { get; init; }

    public required ulong[] PresentBits { get; init; }

    [MemoryPackIgnore]
    public TKey EndKey
        => StartKey + TKey.CreateChecked(Values.Length - 1);

    public bool IsPresent(int index)
    {
        if ((uint)index >= (uint)Values.Length)
            return false;

        int word = index >> 6;
        int bit = index & 63;
        return (PresentBits[word] & (1UL << bit)) != 0;
    }

    public static SegmentDense<TKey, TValue> BuildSegment(
        KeyValuePair<TKey, TValue>[] sorted,
        int fromInclusive,
        int toInclusive,
        TKey segStart,
        TKey segEnd)
    {
        // len = (segEnd - segStart + 1)
        var len = int.CreateChecked(segEnd - segStart + TKey.One);
        if (len <= 0)
            throw new InvalidOperationException($"MapSegmentLong length out of range: {len}");

        var values = new TValue[len];

        // ceil(len / 64)
        int words = (len + 63) >> 6;
        var bits = new ulong[words];

        for (int i = fromInclusive; i <= toInclusive; i++)
        {
            var (k, v) = (sorted[i].Key, sorted[i].Value);
            int offset = int.CreateChecked(k - segStart);

            values[offset] = v;
            SetBit(bits, offset);
        }

        return new SegmentDense<TKey, TValue>
        {
            StartKey = segStart,
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

