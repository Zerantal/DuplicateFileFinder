using System.Runtime.CompilerServices;
using System.Text;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Storage.Models;

[MemoryPackable(SerializeLayout.Sequential)]
public partial class PackedStringPool(byte[] data, int[] offsets)
{
    // UTF-8 bytes packed contiguously: [s0][s1]...[sN-1]
    [MemoryPackInclude] private readonly byte[] _data = data;
    
    // Sentinel offsets: length == Count + 1, with _offsets[Count] == _data.Length.
    [MemoryPackInclude] private readonly int[] _offsets = offsets;
    
    [MemoryPackIgnore] internal byte[] Data => _data;
    [MemoryPackIgnore] internal int[] Offsets => _offsets;
    
    [MemoryPackIgnore]
    public int Count => _offsets.Length - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetString(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        
        int off = _offsets[index];
        int len = _offsets[index + 1] - off; // no branch, sentinel makes this safe
        
        return Encoding.UTF8.GetString(_data, off, len);
    }

    internal string[] MaterializeAllStrings()
    {
        var n = Count;
        var arr = new string[n];
        for (int i = 0; i < n; i++)
            arr[i] = GetString(i);
        return arr;
    }

    internal static PackedStringPool FromStrings(string[] strings)
    {
        int n = strings.Length;

        // _offsets has sentinel slot
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

        offsets[n] = cursor; // sentinel == _data.Length
        return new PackedStringPool(data, offsets);
    }
}