// DuplicateFileFinderLib/Repository/Plugins/Models/SegmentedLongMap.cs

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

/// <summary>
/// A long->T map optimized for fast (de)serialization:
/// persisted as a sorted set of dense-ish segments (arrays + presence bitmap),
/// avoiding Dictionary construction/insertion costs.
/// </summary>
[MemoryPackable(SerializeLayout.Sequential)]
public sealed partial class SegmentedLongMap<T>
    where T : struct
{
    /// <summary>
    /// Sorted by StartId ascending.
    /// </summary>
    public required MapSegmentLong<T>[] Segments { get; init; } = [];

    [MemoryPackIgnore]
    public int SegmentCount => Segments.Length;

    public static SegmentedLongMap<T> Empty { get; } = new() { Segments = [] };

    /// <summary>
    /// Build from unique key/value pairs. Keys will be sorted. Segments may span small gaps
    /// up to <paramref name="gapThreshold"/> to reduce segment count (gaps tracked by bitmap).
    /// </summary>
    public static SegmentedLongMap<T> Build(
        IEnumerable<KeyValuePair<long, T>> items,
        int gapThreshold = 64)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (gapThreshold < 0) throw new ArgumentOutOfRangeException(nameof(gapThreshold));

        // Materialize + sort by key (ascending).
        var sorted = items as KeyValuePair<long, T>[] ?? items.ToArray();
        if (sorted.Length == 0)
            return Empty;

        Array.Sort(sorted, static (a, b) => a.Key.CompareTo(b.Key));

        var segments = new List<MapSegmentLong<T>>(capacity: Math.Min(1024, sorted.Length));

        long segStart = sorted[0].Key;
        long segEnd = segStart;

        int segFirstIndex = 0;

        for (int i = 1; i < sorted.Length; i++)
        {
            long k = sorted[i].Key;

            // Guard against duplicates (Dictionary semantics).
            if (k == sorted[i - 1].Key)
                throw new InvalidOperationException($"Duplicate key detected: {k}");

            long gap = k - segEnd;
            if (gap <= 0)
                throw new InvalidOperationException("Keys must be strictly increasing after sort.");

            // Split if gap is too large.
            if (gap > gapThreshold)
            {
                segments.Add(MapSegmentLong<T>.BuildSegment(sorted, segFirstIndex, i - 1, segStart, segEnd));
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

        segments.Add(MapSegmentLong<T>.BuildSegment(sorted, segFirstIndex, sorted.Length - 1, segStart, segEnd));

        return new SegmentedLongMap<T> { Segments = segments.ToArray() };
    }

    /// <summary>
    /// Convenience: build from a Dictionary without changing callers.
    /// </summary>
    public static SegmentedLongMap<T> FromDictionary(
        Dictionary<long, T> dict,
        int gapThreshold = 64)
    {
        if (dict is null) throw new ArgumentNullException(nameof(dict));
        return Build(dict, gapThreshold);
    }

    public bool TryGetValue(long key, out T value)
    {
        var idx = FindSegmentIndexForKey(key);
        if (idx < 0)
        {
            value = default;
            return false;
        }

        var seg = Segments[idx];
        long offsetL = key - seg.StartId;
        if ((ulong)offsetL >= (ulong)seg.Values.Length)
        {
            value = default;
            return false;
        }

        int offset = (int)offsetL;
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
    public IEnumerable<KeyValuePair<long, T>> Enumerate()
    {
        for (int s = 0; s < Segments.Length; s++)
        {
            var seg = Segments[s];
            var start = seg.StartId;

            // Walk bitmap words and emit set bits.
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

                    yield return new KeyValuePair<long, T>(start + idx, vals[idx]);
                    word &= word - 1; // clear lowest set bit
                }
            }
        }
    }

    private int FindSegmentIndexForKey(long key)
    {
        // Find greatest segment.StartId <= key
        var segs = Segments;
        int lo = 0;
        int hi = segs.Length - 1;
        int best = -1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            long start = segs[mid].StartId;

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

        // Quick end-bound check. If key is beyond end, it can't be in any later segment
        // because best is the last segment with start <= key.
        if (key > segs[best].EndId)
            return -1;

        return best;
    }
}
