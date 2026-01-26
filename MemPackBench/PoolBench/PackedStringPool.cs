using System.Runtime.CompilerServices;
using System.Text;

using MemoryPack;

namespace MemPackBench.PoolBench;

[MemoryPackable(SerializeLayout.Sequential)]
public partial class PackedStringPool
{
    // UTF-8 bytes packed contiguously: [s0][s1]...[sN-1]
    public byte[] Data { get; }

    // Sentinel offsets: length == Count + 1, with Offsets[Count] == Data.Length.
    public int[] Offsets { get; }

    [MemoryPackIgnore]
    public int Count => Offsets.Length - 1;

    public PackedStringPool(byte[] data, int[] offsets)
    {
        Data = data;
        Offsets = offsets;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetString(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        int off = Offsets[index];
        int len = Offsets[index + 1] - off; // no branch, sentinel makes this safe

        return Encoding.UTF8.GetString(Data, off, len);
    }

    public string[] MaterializeAllStrings()
    {
        var n = Count;
        var arr = new string[n];
        for (int i = 0; i < n; i++)
            arr[i] = GetString(i);
        return arr;
    }

    public static PackedStringPool FromStrings(string[] strings)
    {
        int n = strings.Length;

        // Offsets has sentinel slot
        var offsets = new int[n + 1];

        long total = 0;
        for (int i = 0; i < n; i++)
            total += Encoding.UTF8.GetByteCount(strings[i]);

        if (total > int.MaxValue) throw new InvalidOperationException("Pool data too large for single byte[]");

        var data = new byte[(int)total];

        // Second pass: fill
        int cursor = 0;
        for (int i = 0; i < n; i++)
        {
            offsets[i] = cursor;
            cursor += Encoding.UTF8.GetBytes(strings[i], 0, strings[i].Length, data, cursor);
        }

        offsets[n] = cursor; // sentinel == Data.Length
        return new PackedStringPool(data, offsets);
    }
}
