using BenchmarkDotNet.Attributes;

using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace MemPackBench.SegmentedDenseMapBench;

[MemoryDiagnoser]
public class SegmentedMapBench
{
    // Keep this moderate so it runs quickly; bump as needed.
    [Params(50_000, 200_000)]
    public int N;

    // Controls how "gappy" the keys are.
    [Params(0, 4, 32)]
    public int MaxGap;

    private KeyValuePair<long, int>[] _longItems = default!;
    private KeyValuePair<int, int>[] _intItems = default!;

    private long[] _lookupLongKeys = default!;
    private int[] _lookupIntKeys = default!;

    private SegmentedLongMap<int> _segLongMap = default!;
    private SegmentedDenseMap<long, int> _denseLong = default!;
    private SegmentedDenseMap<int, int> _denseInt = default!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(12345);

        var longItems = new KeyValuePair<long, int>[N];
        var intItems = new KeyValuePair<int, int>[N];

        long kLong = 0;
        int kInt = 0;

        for (int i = 0; i < N; i++)
        {
            // Ensure strictly increasing keys, with optional gaps.
            var gap = MaxGap == 0 ? 1 : 1 + rng.Next(MaxGap + 1);
            kLong += gap;
            kInt += gap;

            longItems[i] = new KeyValuePair<long, int>(kLong, i);
            intItems[i] = new KeyValuePair<int, int>(kInt, i);
        }

        _longItems = longItems;
        _intItems = intItems;

        // Build maps once for lookup/enumeration benchmarks.
        _segLongMap = SegmentedLongMap<int>.Build(_longItems, gapThreshold: 64);
        _denseLong = SegmentedDenseMap<long, int>.Build(_longItems, gapThreshold: 64);
        _denseInt = SegmentedDenseMap<int, int>.Build(_intItems, gapThreshold: 64);

        // Choose a lookup set that is mostly hits.
        const int lookups = 10_000;
        _lookupLongKeys = new long[lookups];
        _lookupIntKeys = new int[lookups];

        for (int i = 0; i < lookups; i++)
        {
            var idx = rng.Next(N);
            _lookupLongKeys[i] = _longItems[idx].Key;
            _lookupIntKeys[i] = _intItems[idx].Key;
        }
    }

    // -------------------------
    // Build benchmarks
    // -------------------------

    [Benchmark]
    public SegmentedLongMap<int> Build_SegmentedIdMap_longKey()
        => SegmentedLongMap<int>.Build(_longItems, gapThreshold: 64);

    [Benchmark]
    public SegmentedDenseMap<long, int> Build_SegmentedDenseMap_longKey()
        => SegmentedDenseMap<long, int>.Build(_longItems, gapThreshold: 64);

    [Benchmark]
    public SegmentedDenseMap<int, int> Build_SegmentedDenseMap_intKey()
        => SegmentedDenseMap<int, int>.Build(_intItems, gapThreshold: 64);

    // -------------------------
    // Lookup benchmarks
    // -------------------------

    [Benchmark]
    public int LookupHits_SegmentedIdMap()
    {
        int sum = 0;
        var keys = _lookupLongKeys;

        for (int i = 0; i < keys.Length; i++)
        {
            if (_segLongMap.TryGetValue(keys[i], out var v))
                sum += v;
        }

        return sum;
    }

    [Benchmark]
    public int LookupHits_Dense_long()
    {
        int sum = 0;
        var keys = _lookupLongKeys;

        for (int i = 0; i < keys.Length; i++)
        {
            if (_denseLong.TryGetValue(keys[i], out var v))
                sum += v;
        }

        return sum;
    }

    [Benchmark]
    public int LookupHits_Dense_int()
    {
        int sum = 0;
        var keys = _lookupIntKeys;

        for (int i = 0; i < keys.Length; i++)
        {
            if (_denseInt.TryGetValue(keys[i], out var v))
                sum += v;
        }

        return sum;
    }

    // -------------------------
    // Enumeration benchmarks
    // -------------------------

    [Benchmark]
    public long Enumerate_SegmentedIdMap()
    {
        long sum = 0;
        foreach (var kv in _segLongMap.Enumerate())
            sum += kv.Value;
        return sum;
    }

    [Benchmark]
    public long Enumerate_Dense_long()
    {
        long sum = 0;
        foreach (var kv in _denseLong.Enumerate())
            sum += kv.Value;
        return sum;
    }

    [Benchmark]
    public long Enumerate_Dense_int()
    {
        long sum = 0;
        foreach (var kv in _denseInt.Enumerate())
            sum += kv.Value;
        return sum;
    }
}
