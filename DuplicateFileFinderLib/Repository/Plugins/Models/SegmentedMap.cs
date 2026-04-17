// DuplicateFileFinderLib/Repository/Plugins/Models/SegmentedMap.cs

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

/// <summary>
/// A int->T map optimized for fast (de)serialization:
/// persisted as a sorted set of dense-ish segments (arrays + presence bitmap),
/// avoiding Dictionary construction/insertion costs.
/// </summary>
[MemoryPackable(SerializeLayout.Sequential)]
public sealed partial class SegmentedMap<T>
    where T : unmanaged
{
    /// <summary>
    /// Sorted by StartId ascending.
    /// </summary>
    public required MapSegment<T>[] Segments { get; init; } = [];

    [MemoryPackIgnore] public int SegmentCount => Segments.Length;

    public static SegmentedMap<T> Empty { get; } = new() { Segments = [] };

    /// <summary>
    /// Build from unique key/value pairs. Keys will be sorted. Segments may span small gaps
    /// up to <paramref name="gapThreshold"/> to reduce segment count (gaps tracked by bitmap).
    /// </summary>
    public static SegmentedMap<T> Build(
        IEnumerable<KeyValuePair<int, T>> items,
        int gapThreshold = 64)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (gapThreshold < 0) throw new ArgumentOutOfRangeException(nameof(gapThreshold));

        // Materialize + sort by key (ascending).
        var sorted = items as KeyValuePair<int, T>[] ?? items.ToArray();
        if (sorted.Length == 0)
            return Empty;

        Array.Sort(sorted, static (a, b) => a.Key.CompareTo(b.Key));

        var segments = new List<MapSegment<T>>(capacity: Math.Min(1024, sorted.Length));

        int segStart = sorted[0].Key;
        int segEnd = segStart;
        int segFirstIndex = 0;

        for (int i = 1; i < sorted.Length; i++)
        {
            int k = sorted[i].Key;

            // Guard against duplicates (Dictionary semantics).
            if (k == sorted[i - 1].Key)
                throw new InvalidOperationException($"Duplicate key detected: {k}");

            long gap = k - segEnd;
            if (gap <= 0)
                throw new InvalidOperationException("Keys must be strictly increasing after sort.");

            // Split if gap is too large.
            if (gap > gapThreshold)
            {
                segments.Add(MapSegment<T>.BuildSegment(sorted, segFirstIndex, i - 1, segStart, segEnd));
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

        segments.Add(MapSegment<T>.BuildSegment(sorted, segFirstIndex, sorted.Length - 1, segStart, segEnd));

        return new SegmentedMap<T> { Segments = segments.ToArray() };
    }

    /// <summary>
    /// Convenience: build from a Dictionary without changing callers.
    /// </summary>
    public static SegmentedMap<T> FromDictionary(
        Dictionary<int, T> dict,
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
    /// Returns a new map with <paramref name="key"/> removed.
    /// If the key is not present, returns the current instance.
    /// </summary>
    public SegmentedMap<T> Remove(int key) => Remove((long)key);

    /// <summary>
    /// Returns a new map with <paramref name="key"/> removed.
    /// If the key is not present, returns the current instance.
    /// </summary>
    public SegmentedMap<T> Remove(long key)
    {
        var segIndex = FindSegmentIndexForKey(key);
        if (segIndex < 0)
            return this;

        var seg = Segments[segIndex];
        long offsetL = key - seg.StartId;
        if ((ulong)offsetL >= (ulong)seg.Values.Length)
            return this;

        int offset = (int)offsetL;
        if (!seg.IsPresent(offset))
            return this;

        var newBits = (ulong[])seg.PresentBits.Clone();
        ClearBit(newBits, offset);

        // If that was the last live entry in the segment, drop the whole segment.
        if (!HasAnyBitSet(newBits))
        {
            if (Segments.Length == 1)
                return Empty;

            var newSegmentsWithoutTarget = new MapSegment<T>[Segments.Length - 1];
            if (segIndex > 0)
                Array.Copy(Segments, 0, newSegmentsWithoutTarget, 0, segIndex);

            if (segIndex < Segments.Length - 1)
            {
                Array.Copy(
                    Segments,
                    segIndex + 1,
                    newSegmentsWithoutTarget,
                    segIndex,
                    Segments.Length - segIndex - 1);
            }

            return new SegmentedMap<T> { Segments = newSegmentsWithoutTarget };
        }

        var newSeg = new MapSegment<T> { StartId = seg.StartId, Values = seg.Values, PresentBits = newBits };

        var newSegments = (MapSegment<T>[])Segments.Clone();
        newSegments[segIndex] = newSeg;

        return new SegmentedMap<T> { Segments = newSegments };
    }

    public SegmentedMap<T> RemoveMany(IEnumerable<int> keys)
    {
        if (keys is null) throw new ArgumentNullException(nameof(keys));

        var segs = Segments;
        if (segs.Length == 0)
            return this;

        Dictionary<int, HashSet<int>>? offsetsBySegment = null;

        foreach (var key in keys)
        {
            var segIndex = FindSegmentIndexForKey(key);
            if (segIndex < 0)
                continue;

            var seg = segs[segIndex];
            long offsetL = (long)key - seg.StartId;
            if ((ulong)offsetL >= (ulong)seg.Values.Length)
                continue;

            int offset = (int)offsetL;
            if (!seg.IsPresent(offset))
                continue;

            offsetsBySegment ??= new Dictionary<int, HashSet<int>>();
            if (!offsetsBySegment.TryGetValue(segIndex, out var offsets))
            {
                offsets = new HashSet<int>();
                offsetsBySegment[segIndex] = offsets;
            }

            offsets.Add(offset);
        }

        if (offsetsBySegment is null || offsetsBySegment.Count == 0)
            return this;

        var newSegments = new List<MapSegment<T>>(segs.Length);
        var anyChanged = false;

        for (int segIndex = 0; segIndex < segs.Length; segIndex++)
        {
            var seg = segs[segIndex];

            if (!offsetsBySegment.TryGetValue(segIndex, out var offsets))
            {
                newSegments.Add(seg);
                continue;
            }

            var newBits = (ulong[])seg.PresentBits.Clone();

            foreach (var offset in offsets)
                ClearBit(newBits, offset);

            if (AreEqual(seg.PresentBits, newBits))
            {
                newSegments.Add(seg);
                continue;
            }

            anyChanged = true;

            if (!HasAnyBitSet(newBits))
                continue;

            newSegments.Add(new MapSegment<T> { StartId = seg.StartId, Values = seg.Values, PresentBits = newBits });
        }

        if (!anyChanged)
            return this;

        if (newSegments.Count == 0)
            return Empty;

        return new SegmentedMap<T> { Segments = newSegments.ToArray() };
    }

    /// <summary>
    /// Enumerate all present entries in ascending key order.
    /// </summary>
    public IEnumerable<KeyValuePair<int, T>> Enumerate()
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

                    yield return new KeyValuePair<int, T>(start + idx, vals[idx]);
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

    private static void ClearBit(ulong[] bits, int index)
    {
        int word = index >> 6;
        int bit = index & 63;
        bits[word] &= ~(1UL << bit);
    }

    private static bool HasAnyBitSet(ulong[] bits)
    {
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i] != 0)
                return true;
        }

        return false;
    }

    private static bool AreEqual(ulong[] a, ulong[] b)
    {
        if (ReferenceEquals(a, b))
            return true;

        if (a.Length != b.Length)
            return false;

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }

        return true;
    }
}
