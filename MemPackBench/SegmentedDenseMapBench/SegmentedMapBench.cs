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

    private KeyValuePair<int, int>[] _intItems = default!;

    private int[] _lookupIntKeys = default!;

    private SegmentedMap<int> _segLongMap = default!;
    private GenericSegmentedMap<int, int> _int = default!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(12345);
        var intItems = new KeyValuePair<int, int>[N];

        int kInt = 0;

        for (int i = 0; i < N; i++)
        {
            // Ensure strictly increasing keys, with optional gaps.
            var gap = MaxGap == 0 ? 1 : 1 + rng.Next(MaxGap + 1);
            kInt += gap;

            intItems[i] = new KeyValuePair<int, int>(kInt, i);
        }

        _intItems = intItems;

        // Build maps once for lookup/enumeration benchmarks.
        _segLongMap = SegmentedMap<int>.Build(_intItems, gapThreshold: 64);
        _int = GenericSegmentedMap<int, int>.Build(_intItems, gapThreshold: 64);

        // Choose a lookup set that is mostly hits.
        const int lookups = 10_000;
        _lookupIntKeys = new int[lookups];

        for (int i = 0; i < lookups; i++)
        {
            var idx = rng.Next(N);
            _lookupIntKeys[i] = _intItems[idx].Key;
        }
    }

    // -------------------------
    // Build benchmarks
    // -------------------------

    [Benchmark]
    public SegmentedMap<int> Build_SegmentedMap()
        => SegmentedMap<int>.Build(_intItems, gapThreshold: 64);

    [Benchmark]
    public GenericSegmentedMap<int, int> Build_GenericSegmentedMap()
        => GenericSegmentedMap<int, int>.Build(_intItems, gapThreshold: 64);

    // -------------------------
    // Lookup benchmarks
    // -------------------------

    [Benchmark]
    public int LookupHits_SegmentedMap()
    {
        int sum = 0;
        var keys = _lookupIntKeys;

        for (int i = 0; i < keys.Length; i++)
        {
            if (_segLongMap.TryGetValue(keys[i], out var v))
                sum += v;
        }

        return sum;
    }

    [Benchmark]
    public int LookupHits_GenericSegmentedMap()
    {
        int sum = 0;
        var keys = _lookupIntKeys;

        for (int i = 0; i < keys.Length; i++)
        {
            if (_int.TryGetValue(keys[i], out var v))
                sum += v;
        }

        return sum;
    }

    // -------------------------
    // Enumeration benchmarks
    // -------------------------

    [Benchmark]
    public long Enumerate_SegmentedMap()
    {
        long sum = 0;
        foreach (var kv in _segLongMap.Enumerate())
            sum += kv.Value;
        return sum;
    }

    [Benchmark]
    public long Enumerate_GenericSegmentedMap()
    {
        long sum = 0;
        foreach (var kv in _int.Enumerate())
            sum += kv.Value;
        return sum;
    }
}
