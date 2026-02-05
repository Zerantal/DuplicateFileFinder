// DuplicateFileFinderLibTests/Repository/Plugins/Models/SegmentedMapTests.cs

using System;
using System.Collections.Generic;
using System.Linq;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;

using MemoryPack;

using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Plugins.Models;

public sealed class SegmentedMapTests
{
    [Fact]
    public void Empty_TryGetValue_ReturnsFalse()
    {
        var map = SegmentedMap<DirHandle>.Empty;

        Assert.False(map.TryGetValue(123, out _));
        Assert.False(map.TryGetValue(-1, out _));
        Assert.Equal(0, map.SegmentCount);
        Assert.Empty(map.Enumerate());
    }

    [Fact]
    public void Build_SingleItem_CreatesSingleSegment_AndLookupWorks()
    {
        var items = new[]
        {
            new KeyValuePair<int, DirHandle>(42, new DirHandle(5, 7))
        };

        var map = SegmentedMap<DirHandle>.Build(items, gapThreshold: 0);

        Assert.Equal(1, map.SegmentCount);
        Assert.True(map.TryGetValue(42, out var v));
        Assert.Equal(new DirHandle(5, 7), v);

        Assert.False(map.TryGetValue(41, out _));
        Assert.False(map.TryGetValue(43, out _));

        var all = map.Enumerate().ToArray();
        Assert.Single(all);
        Assert.Equal(42, all[0].Key);
        Assert.Equal(new DirHandle(5, 7), all[0].Value);
    }

    [Fact]
    public void Build_DuplicateKeys_Throws()
    {
        var items = new[]
        {
            new KeyValuePair<int, DirHandle>(10, new DirHandle(1, 1)),
            new KeyValuePair<int, DirHandle>(10, new DirHandle(1, 2)),
        };

        Assert.Throws<InvalidOperationException>(() => SegmentedMap<DirHandle>.Build(items));
    }

    [Fact]
    public void Build_UnsortedInput_IsSortedAndLookupWorks()
    {
        var items = new[]
        {
            new KeyValuePair<int, FileHandle>(100, new FileHandle(8, 1)),
            new KeyValuePair<int, FileHandle>(1,   new FileHandle(8, 2)),
            new KeyValuePair<int, FileHandle>(50,  new FileHandle(8, 3)),
        };

        var map = SegmentedMap<FileHandle>.Build(items, gapThreshold: 1000);

        Assert.True(map.TryGetValue(1, out var v1));
        Assert.Equal(new FileHandle(8, 2), v1);

        Assert.True(map.TryGetValue(50, out var v50));
        Assert.Equal(new FileHandle(8, 3), v50);

        Assert.True(map.TryGetValue(100, out var v100));
        Assert.Equal(new FileHandle(8, 1), v100);

        var keys = map.Enumerate().Select(kv => kv.Key).ToArray();
        Assert.Equal(new[] { 1, 50, 100 }, keys);
    }

    [Fact]
    public void Build_GapThreshold_SplitsSegments_WhenGapTooLarge()
    {
        var items = new[]
        {
            new KeyValuePair<int, DirHandle>(10,  new DirHandle(1, 10)),
            new KeyValuePair<int, DirHandle>(20,  new DirHandle(1, 20)),
            new KeyValuePair<int, DirHandle>(200, new DirHandle(1, 200)),
        };

        // With gapThreshold=5, 10->20 gap=10 => split; 20->200 gap=180 => split
        var map = SegmentedMap<DirHandle>.Build(items, gapThreshold: 5);

        Assert.Equal(3, map.SegmentCount);

        Assert.True(map.TryGetValue(10, out _));
        Assert.True(map.TryGetValue(20, out _));
        Assert.True(map.TryGetValue(200, out _));
    }

    [Fact]
    public void Build_GapThreshold_AllowsSmallGaps_AndMissingKeysReturnFalse()
    {
        var items = new[]
        {
            new KeyValuePair<int, DirHandle>(10, new DirHandle(1, 10)),
            new KeyValuePair<int, DirHandle>(12, new DirHandle(1, 12)), // gap 2
        };

        // gapThreshold >=2 keeps in one segment spanning [10..12]
        var map = SegmentedMap<DirHandle>.Build(items, gapThreshold: 2);

        Assert.Equal(1, map.SegmentCount);

        Assert.True(map.TryGetValue(10, out var v10));
        Assert.Equal(new DirHandle(1, 10), v10);

        Assert.True(map.TryGetValue(12, out var v12));
        Assert.Equal(new DirHandle(1, 12), v12);

        // Hole at 11 should be absent (bitmap)
        Assert.False(map.TryGetValue(11, out _));
    }

    [Fact]
    public void TryGetValue_KeyBeforeFirstSegment_ReturnsFalse()
    {
        var items = new[]
        {
            new KeyValuePair<int, DirHandle>(100, new DirHandle(1, 1)),
            new KeyValuePair<int, DirHandle>(101, new DirHandle(1, 2)),
        };

        var map = SegmentedMap<DirHandle>.Build(items, gapThreshold: 0);

        Assert.False(map.TryGetValue(99, out _));
        Assert.True(map.TryGetValue(100, out _));
    }

    [Fact]
    public void TryGetValue_KeyAfterSegmentEnd_ReturnsFalse()
    {
        var items = new[]
        {
            new KeyValuePair<int, DirHandle>(100, new DirHandle(1, 1)),
            new KeyValuePair<int, DirHandle>(101, new DirHandle(1, 2)),
        };

        var map = SegmentedMap<DirHandle>.Build(items, gapThreshold: 0);

        Assert.False(map.TryGetValue(102, out _));
    }

    [Fact]
    public void Enumerate_ReturnsAllEntries_Once_InAscendingKeyOrder()
    {
        var items = new[]
        {
            new KeyValuePair<int, FileHandle>(100, new FileHandle(9, 1)),
            new KeyValuePair<int, FileHandle>(10,  new FileHandle(9, 2)),
            new KeyValuePair<int, FileHandle>(12,  new FileHandle(9, 3)),
            new KeyValuePair<int, FileHandle>(200, new FileHandle(9, 4)),
        };

        var map = SegmentedMap<FileHandle>.Build(items, gapThreshold: 2);

        var enumerated = map.Enumerate().ToArray();

        Assert.Equal(items.Length, enumerated.Length);
        Assert.Equal(new[] { 10, 12, 100, 200 }, enumerated.Select(e => e.Key).ToArray());

        // Ensure each key appears once
        Assert.Equal(enumerated.Length, enumerated.Select(e => e.Key).Distinct().Count());

        // Values match
        var dict = items.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (k, v) in enumerated)
            Assert.Equal(dict[k], v);
    }

    [Fact]
    public void FromDictionary_BuildsEquivalentMap()
    {
        var dict = new Dictionary<int, DirHandle>
        {
            [5] = new DirHandle(1, 5),
            [1] = new DirHandle(1, 1),
            [100] = new DirHandle(1, 100),
        };

        var map = SegmentedMap<DirHandle>.FromDictionary(dict, gapThreshold: 1000);

        foreach (var (k, v) in dict)
        {
            Assert.True(map.TryGetValue(k, out var got));
            Assert.Equal(v, got);
        }

        Assert.Equal(dict.Count, map.Enumerate().Count());
    }

    [Fact]
    public void MemoryPack_RoundTrip_PreservesSegmentsAndLookups()
    {
        var items = new[]
        {
            new KeyValuePair<int, DirHandle>(10,  new DirHandle(5, 10)),
            new KeyValuePair<int, DirHandle>(12,  new DirHandle(5, 12)),
            new KeyValuePair<int, DirHandle>(200, new DirHandle(5, 200)),
        };

        var map = SegmentedMap<DirHandle>.Build(items, gapThreshold: 2);
        Assert.True(map.SegmentCount >= 1);

        var bytes = MemoryPackSerializer.Serialize(map);
        var clone = MemoryPackSerializer.Deserialize<SegmentedMap<DirHandle>>(bytes);

        Assert.NotNull(clone);
        Assert.Equal(map.SegmentCount, clone.SegmentCount);

        // Lookups
        foreach (var (k, v) in items)
        {
            Assert.True(clone.TryGetValue(k, out var got));
            Assert.Equal(v, got);
        }

        // Holes should remain holes (11 is missing in first segment span [10..12])
        Assert.False(clone.TryGetValue(11, out _));

        // Enumeration equivalence
        var a = map.Enumerate().ToArray();
        var b = clone.Enumerate().ToArray();
        Assert.Equal(a.Length, b.Length);
        Assert.Equal(a.Select(x => x.Key), b.Select(x => x.Key));
        Assert.Equal(a.Select(x => x.Value), b.Select(x => x.Value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64)]
    public void Build_WithVariousGapThresholds_MaintainsCorrectness(int gapThreshold)
    {
        // Mix of clusters and gaps.
        var items = new List<KeyValuePair<int, FileHandle>>();
        for (int i = 1000; i < 1100; i++) items.Add(new(i, new FileHandle(1, i - 1000)));
        for (int i = 5000; i < 5100; i++) items.Add(new(i, new FileHandle(2, i - 5000)));
        items.Add(new(9999, new FileHandle(3, 9)));

        var map = SegmentedMap<FileHandle>.Build(items, gapThreshold);

        // All keys present
        foreach (var (k, v) in items)
        {
            Assert.True(map.TryGetValue(k, out var got));
            Assert.Equal(v, got);
        }

        // Some absent keys
        Assert.False(map.TryGetValue(1, out _));
        Assert.False(map.TryGetValue(2000, out _));
        Assert.False(map.TryGetValue(5200, out _));

        // Enumeration matches
        var dict = items.ToDictionary(kv => kv.Key, kv => kv.Value);
        var enumerated = map.Enumerate().ToArray();

        Assert.Equal(dict.Count, enumerated.Length);
        Assert.True(enumerated.Select(e => e.Key).SequenceEqual(enumerated.Select(e => e.Key).OrderBy(x => x)));

        foreach (var (k, v) in enumerated)
            Assert.Equal(dict[k], v);
    }

    [Fact]
    public void Stress_RandomizedKeys_AreAllPresent_AbsentOnMissing_EnumerationMatches()
    {
        const int seed = 1337;
        var rng = new Random(seed);

        // Run multiple trials to cover different densities/shapes.
        for (int trial = 0; trial < 25; trial++)
        {
            // Generate a random sparse-ish set of unique keys.
            // Range is intentionally large compared to count to create holes and multiple segments.
            int count = rng.Next(1_000, 6_000);
            int maxKey = rng.Next(50_000, 500_000);

            var keys = new HashSet<int>();
            while (keys.Count < count)
            {
                // Create clustered keys sometimes, to form bands like your real data.
                if (rng.NextDouble() < 0.65 && keys.Count > 0)
                {
                    var baseKey = keys.ElementAt(rng.Next(keys.Count));
                    var delta = rng.Next(-128, 129);
                    int k = baseKey + delta;
                    if (k >= 0 && k <= maxKey) keys.Add(k);
                }
                else
                {
                    keys.Add(rng.Next(0, maxKey + 1));
                }
            }

            // Assign deterministic values.
            // We only care about exact key->value preservation.
            var items = keys
                .Select(k => new KeyValuePair<int, DirHandle>(k, new DirHandle(ScanRootId: 5, Index: k)))
                .ToArray();

            // Try several thresholds per trial.
            var thresholds = new[] { 0, 1, 2, 8, 32, 64, 256 };

            foreach (var gapThreshold in thresholds)
            {
                var map = SegmentedMap<DirHandle>.Build(items, gapThreshold);

                // 1) Every input key must be retrievable with exact value.
                foreach (var (k, v) in items)
                {
                    Assert.True(map.TryGetValue(k, out var got));
                    Assert.Equal(v, got);
                }

                // 2) Probe absent keys: pick keys not in the set and ensure TryGetValue returns false.
                // We test a mix of random probes and near-miss probes around existing keys.
                for (int i = 0; i < 2_000; i++)
                {
                    int probe;
                    if (rng.NextDouble() < 0.5)
                    {
                        probe = rng.Next(0, maxKey + 1);
                    }
                    else
                    {
                        // near-miss around an existing key
                        var baseKey = items[rng.Next(items.Length)].Key;
                        probe = baseKey + rng.Next(-3, 4);
                        if (probe < 0) probe = 0;
                        if (probe > maxKey) probe = maxKey;
                    }

                    if (keys.Contains(probe))
                        continue;

                    Assert.False(map.TryGetValue(probe, out _));
                }

                // 3) Enumerate returns exactly the set of items, in ascending key order, no duplicates.
                var enumerated = map.Enumerate().ToArray();

                Assert.Equal(keys.Count, enumerated.Length);

                var enumKeys = enumerated.Select(e => e.Key).ToArray();
                Assert.True(enumKeys.SequenceEqual(enumKeys.OrderBy(x => x)));

                Assert.Equal(enumKeys.Length, enumKeys.Distinct().Count());

                // Exact set equality vs source keys.
                Assert.True(keys.SetEquals(enumKeys));

                // Values match for enumeration too.
                var dict = items.ToDictionary(kv => kv.Key, kv => kv.Value);
                foreach (var (k, v) in enumerated)
                    Assert.Equal(dict[k], v);

                // 4) MemoryPack round-trip should preserve behavior (lookup + enumeration).
                var bytes = MemoryPackSerializer.Serialize(map);
                var clone = MemoryPackSerializer.Deserialize<SegmentedMap<DirHandle>>(bytes);
                Assert.NotNull(clone);

                foreach (var (k, v) in items)
                {
                    Assert.True(clone.TryGetValue(k, out var got));
                    Assert.Equal(v, got);
                }

                var cloneEnum = clone.Enumerate().ToArray();
                Assert.Equal(enumerated.Length, cloneEnum.Length);
                Assert.Equal(enumerated.Select(x => x.Key), cloneEnum.Select(x => x.Key));
                Assert.Equal(enumerated.Select(x => x.Value), cloneEnum.Select(x => x.Value));
            }
        }
    }

    [Fact]
    public void Stress_ExtremeClustersAndGaps_CorrectWithTinyAndHugeThresholds()
    {
        // Deterministic: emulate your real-world "bands with small holes + big gaps".
        const int seed = 424242;
        var rng = new Random(seed);

        var items = new List<KeyValuePair<int, FileHandle>>(capacity: 50_000);

        // Band A: dense with random holes
        AddBand(items, rng, start: 100_000, length: 20_000, holeEvery: 37, scanRootId: 5);

        // Gap
        // Band B: another dense band
        AddBand(items, rng, start: 250_000, length: 30_000, holeEvery: 53, scanRootId: 6);

        // Singletons far away
        items.Add(new KeyValuePair<int, FileHandle>(999_999, new FileHandle(8, 1)));
        items.Add(new KeyValuePair<int, FileHandle>(1_500_000, new FileHandle(8, 2)));

        // Ensure keys unique
        items = items
            .GroupBy(kv => kv.Key)
            .Select(g => g.First())
            .ToList();

        var thresholds = new[] { 0, 1, 8, 64, 1024, 100_000 };

        foreach (var gapThreshold in thresholds)
        {
            var map = SegmentedMap<FileHandle>.Build(items, gapThreshold);

            // All present
            foreach (var (k, v) in items)
            {
                Assert.True(map.TryGetValue(k, out var got));
                Assert.Equal(v, got);
            }

            // Some guaranteed absent probes
            Assert.False(map.TryGetValue(0, out _));
            Assert.False(map.TryGetValue(200_000 - 1, out _)); // likely in gap/hole region
            Assert.False(map.TryGetValue(220_000, out _));     // gap between bands

            // Enumerate matches set
            var enumerated = map.Enumerate().ToArray();
            Assert.Equal(items.Count, enumerated.Length);

            var itemKeys = items.Select(x => x.Key).OrderBy(x => x).ToArray();
            var enumKeys = enumerated.Select(x => x.Key).ToArray();
            Assert.Equal(itemKeys, enumKeys);

            // MemoryPack roundtrip
            var bytes = MemoryPackSerializer.Serialize(map);
            var clone = MemoryPackSerializer.Deserialize<SegmentedMap<FileHandle>>(bytes);
            Assert.NotNull(clone);

            foreach (var (k, v) in items)
            {
                Assert.True(clone.TryGetValue(k, out var got));
                Assert.Equal(v, got);
            }
        }

        static void AddBand(
            List<KeyValuePair<int, FileHandle>> dst,
            Random rng,
            int start,
            int length,
            int holeEvery,
            ScanRootId scanRootId)
        {
            for (int i = 0; i < length; i++)
            {
                if (holeEvery > 0 && (i % holeEvery) == 0)
                    continue; // hole

                int key = start + i;

                // Make index non-trivial and non-monotonic (still deterministic).
                int idx = key ^ (key >> 7) ^ rng.Next(0, 10);

                dst.Add(new KeyValuePair<int, FileHandle>(key, new FileHandle(scanRootId, idx)));
            }
        }
    }
}

