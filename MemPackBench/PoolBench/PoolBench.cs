using BenchmarkDotNet.Attributes;
using MemoryPack;

namespace MemPackBench.PoolBench;

[MemoryDiagnoser]
public class PoolBench
{
    [Params(5_000_000)]
    public int Count { get; set; }

    [Params(50)]
    public int MaxLen { get; set; }

    // “Real usage” – what fraction of strings do we actually touch after loading?
    [Params(1, 5, 10)]
    public int AccessPercent { get; set; }

    private string[] _strings = default!;
    private StringArrayContainer _memPackContainer = default!;
    private PackedStringPool _pool = default!;

    private byte[] _serStringArray = default!;      // serialized raw string array
    private byte[] _serPool = default!;             // serialized string pool (custom serializer) 
    private byte[] _serMemPackStringArray = default!; // serialized container for string array (MemoryPacked)
    private byte[] _serMemPackPool = default!;      // Serialized string pool (MemoryPacked)

    private int[] _sampleIndices = default!;

    // Prevent dead-code elimination
    // ReSharper disable once NotAccessedField.Local
    private volatile int _sink;

    [GlobalSetup]
    public void Setup()
    {
        const int seed = 12345;

        _strings = DataGen.MakeRandomStrings(Count, MaxLen, seed);
        _pool = PackedStringPool.FromStrings(_strings);

        // PrecoPackedStrmpute so deserialize benchmarks don’t include serialize cost.
        _serStringArray = Ser.StringArrayToBytes(_strings);
        _serPool = Ser.PoolToBytes(_pool);

        // MemoryPack payloads (precomputed)
        _memPackContainer = new StringArrayContainer { Strings = _strings };
        _serMemPackStringArray = MemoryPackSerializer.Serialize(_memPackContainer);
        _serMemPackPool = MemoryPackSerializer.Serialize(_pool);

        BuildSampleIndices(seed: seed + 1);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // small perturbation to keep sink “live”
        _sink = 0;
    }

    // ----------------------------
    // Serialize (custom prealloc)
    // ----------------------------

    [Benchmark]
    public byte[] Serialize_PackedPool_CustomBinary_Prealloc() => Ser.PoolToBytes(_pool);

    [Benchmark]
    public byte[] Serialize_PackedPool_MemoryPack() => MemoryPackSerializer.Serialize(_pool);

    [Benchmark]
    public byte[] Serialize_StringArray_CustomBinary_Prealloc() => Ser.StringArrayToBytes(_strings);

    [Benchmark]
    public byte[] Serialize_StringArrayContainer_MemoryPack() => MemoryPackSerializer.Serialize(_memPackContainer);

    // ----------------------------
    // Deserialize (structure only)
    // ----------------------------

    [Benchmark]
    public PackedStringPool Deserialize_PackedPool_CustomBinary() => Ser.BytesToPool(_serPool);

    [Benchmark]
    public PackedStringPool Deserialize_PackedPool_MemoryPack() => MemoryPackSerializer.Deserialize<PackedStringPool>(_serMemPackPool)!;
    
    [Benchmark]
    public string[] Deserialize_StringArray_CustomBinary() => Ser.BytesToStringArray(_serStringArray);

    [Benchmark]
    public StringArrayContainer Deserialize_StringArrayContainer_MemoryPack()
        => MemoryPackSerializer.Deserialize<StringArrayContainer>(_serMemPackStringArray)!;

    // ---------------------------------------------------------
    // Realistic workloads: load, then touch only N% of strings
    // ---------------------------------------------------------

    [Benchmark]
    public int Deserialize_PackedPool_Custom_ThenDecodeSample()
    {
        var pool = Ser.BytesToPool(_serPool);

        int sum = 0;
        var idx = _sampleIndices;
        for (int i = 0; i < idx.Length; i++)
            sum += pool.GetString(idx[i]).Length;

        _sink = sum;
        return sum;
    }

    [Benchmark]
    public int Deserialize_PackedPool_MemoryPack_ThenDecodeSample()
    {
        var pool = MemoryPackSerializer.Deserialize<PackedStringPool>(_serMemPackPool)!;

        int sum = 0;
        var idx = _sampleIndices;
        for (int i = 0; i < idx.Length; i++)
            sum += pool.GetString(idx[i]).Length;

        _sink = sum;
        return sum;
    }

    [Benchmark]
    public int Deserialize_StringArray_CustomBinary_ThenReadSample()
    {
        var arr = Ser.BytesToStringArray(_serStringArray);

        int sum = 0;
        var idx = _sampleIndices;
        for (int i = 0; i < idx.Length; i++)
            sum += arr[idx[i]].Length;

        _sink = sum;
        return sum;
    }

    [Benchmark]
    public int Deserialize_StringArray_MemoryPack_ThenReadSample()
    {
        var arr = MemoryPackSerializer.Deserialize<StringArrayContainer>(_serMemPackStringArray)!.Strings;

        int sum = 0;
        var idx = _sampleIndices;
        for (int i = 0; i < idx.Length; i++)
            sum += arr[idx[i]].Length;

        _sink = sum;
        return sum;
    }

    private void BuildSampleIndices(int seed)
    {
        int sampleCount = checked((int)((long)Count * AccessPercent / 100));
        if (sampleCount <= 0) sampleCount = 1;

        var rng = new Random(seed);
        var idx = new int[sampleCount];

        for (int i = 0; i < idx.Length; i++)
            idx[i] = rng.Next(0, Count);

        _sampleIndices = idx;
    }
}