// DuplicateFileFinderLib/Repository/Plugins/Models/SegmentedDenseMap.cs

using MemoryPack;

namespace MemPackBench.SegmentedDenseMapBench;

/// <summary>
/// A TKey->TValue map optimized for fast (de)serialization:
/// persisted as a sorted set of dense-ish segments (arrays + presence bitmap),
/// avoiding Dictionary construction/insertion costs.
/// </summary>
[MemoryPackable(SerializeLayout.Sequential)]
public sealed partial class SegmentedDenseMap<TKey, TValue>
    where TKey : struct, System.Numerics.IBinaryInteger<TKey>
    where TValue : struct
{
    /// <summary>
    /// Sorted by StartKey ascending.
    /// </summary>
    public required SegmentDense<TKey, TValue>[] Segments { get; init; } = [];

    [MemoryPackIgnore]
    public int SegmentCount => Segments.Length;

    public static SegmentedDenseMap<TKey, TValue> Empty { get; }
        = new() { Segments = [] };

    /// <summary>
    /// Build from unique key/value pairs. Keys will be sorted. Segments may span small gaps
    /// up to <paramref name="gapThreshold"/> to reduce segment count (gaps tracked by bitmap).
    /// </summary>
    public static SegmentedDenseMap<TKey, TValue> Build(
        IEnumerable<KeyValuePair<TKey, TValue>> items,
        int gapThreshold = 64)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (gapThreshold < 0) throw new ArgumentOutOfRangeException(nameof(gapThreshold));

        var sorted = items as KeyValuePair<TKey, TValue>[] ?? items.ToArray();
        if (sorted.Length == 0)
            return Empty;

        Array.Sort(sorted, static (a, b) => Comparer<TKey>.Default.Compare(a.Key, b.Key));

        var segments = new List<SegmentDense<TKey, TValue>>(capacity: Math.Min(1024, sorted.Length));

        var segStart = sorted[0].Key;
        var segEnd = segStart;

        int segFirstIndex = 0;

        var gapThresholdKey = TKey.CreateChecked(gapThreshold);

        for (int i = 1; i < sorted.Length; i++)
        {
            var k = sorted[i].Key;

            // Guard against duplicates (Dictionary semantics).
            if (k == sorted[i - 1].Key)
                throw new InvalidOperationException($"Duplicate key detected: {k}");

            var gap = k - segEnd;
            if (gap <= TKey.Zero)
                throw new InvalidOperationException("Keys must be strictly increasing after sort.");

            // Split if gap is too large.
            if (gap > gapThresholdKey)
            {
                segments.Add(SegmentDense<TKey, TValue>.BuildSegment(sorted, segFirstIndex, i - 1, segStart, segEnd));
                segFirstIndex = i;
                segStart = k;
                segEnd = k;
            }
            else
            {
                // Allow spanning the gap (bitmap will mark missing holes).
                segEnd = k;
            }
        }

        segments.Add(SegmentDense<TKey, TValue>.BuildSegment(sorted, segFirstIndex, sorted.Length - 1, segStart, segEnd));

        return new SegmentedDenseMap<TKey, TValue> { Segments = segments.ToArray() };
    }

    /// <summary>
    /// Convenience: build from a Dictionary without changing callers.
    /// </summary>
    public static SegmentedDenseMap<TKey, TValue> FromDictionary(
        Dictionary<TKey, TValue> dict,
        int gapThreshold = 64)
    {
        if (dict is null) throw new ArgumentNullException(nameof(dict));
        return Build(dict, gapThreshold);
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        var idx = FindSegmentIndexForKey(key);
        if (idx < 0)
        {
            value = default;
            return false;
        }

        var seg = Segments[idx];
        var offsetKey = key - seg.StartKey;

        // Bounds checks in int space (span/array indexing).
        int offset;
        try
        {
            offset = int.CreateChecked(offsetKey);
        }
        catch
        {
            value = default;
            return false;
        }

        if ((uint)offset >= (uint)seg.Values.Length)
        {
            value = default;
            return false;
        }

        if (!seg.IsPresent(offset))
        {
            value = default;
            return false;
        }

        value = seg.Values[offset];
        return true;
    }

    /// <summary>
    /// Enumerate all present entries in ascending key order.
    /// </summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> Enumerate()
    {
        for (int s = 0; s < Segments.Length; s++)
        {
            var seg = Segments[s];
            var start = seg.StartKey;

            var bits = seg.PresentBits;
            var vals = seg.Values;

            for (int w = 0; w < bits.Length; w++)
            {
                ulong word = bits[w];
                while (word != 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(word);
                    int idx = (w << 6) + bit;
                    if (idx >= vals.Length)
                        break;

                    yield return new KeyValuePair<TKey, TValue>(start + TKey.CreateChecked(idx), vals[idx]);
                    word &= word - 1;
                }
            }
        }
    }

    private int FindSegmentIndexForKey(TKey key)
    {
        // Find greatest segment.StartKey <= key
        var segs = Segments;
        int lo = 0;
        int hi = segs.Length - 1;
        int best = -1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            var start = segs[mid].StartKey;

            if (start <= key)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (best < 0)
            return -1;

        if (key > segs[best].EndKey)
            return -1;

        return best;
    }
}

