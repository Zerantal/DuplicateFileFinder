using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace MemPackBench.PoolBench;

public static class Ser
{
    // -------------------------
    // String array custom binary
    // -------------------------
    //
    // Format:
    // [int32 count]
    // repeated: [int32 byteLen][byteLen bytes UTF-8]
    //
    // Preallocates exact output size to avoid buffer growth/copies and reduce GC noise.

    public static byte[] StringArrayToBytes(string[] strings)
    {
        int n = strings.Length;

        long totalUtf8 = 0;
        for (int i = 0; i < n; i++)
            totalUtf8 += Encoding.UTF8.GetByteCount(strings[i]);

        // 4 bytes count + for each string: 4 bytes length header
        long totalSize = 4 + (long)n * 4 + totalUtf8;
        if (totalSize > int.MaxValue) throw new InvalidOperationException("Payload too large for single byte[]");

        var buffer = new byte[(int)totalSize];
        int pos = 0;

        WriteInt32(buffer, ref pos, n);

        for (int i = 0; i < n; i++)
        {
            string s = strings[i];
            int byteLen = Encoding.UTF8.GetByteCount(s);

            WriteInt32(buffer, ref pos, byteLen);

            pos += Encoding.UTF8.GetBytes(s.AsSpan(), buffer.AsSpan(pos, byteLen));
        }

        return buffer;
    }

    public static string[] BytesToStringArray(byte[] bytes)
    {
        int pos = 0;
        int n = ReadInt32(bytes, ref pos);
        var arr = new string[n];

        for (int i = 0; i < n; i++)
        {
            int len = ReadInt32(bytes, ref pos);
            arr[i] = Encoding.UTF8.GetString(bytes, pos, len);
            pos += len;
        }

        return arr;
    }

    // -------------------------
    // PackedStringPool binary
    // -------------------------
    //
    // Format:
    // [int32 count]
    // [int32 offsetsLen (=count+1)]
    // [int32 dataLen]
    // [offsets as int32 * offsetsLen]
    // [data bytes]
    //
    // Prealloc exact size.

    public static byte[] PoolToBytes(PackedStringPool pool)
    {
        int count = pool.Count;
        int offsetsLen = pool.Offsets.Length; // == count + 1
        int dataLen = pool.Data.Length;

        long totalSize = 4 + 4 + 4 + (long)offsetsLen * 4 + dataLen;
        if (totalSize > int.MaxValue) throw new InvalidOperationException("Payload too large for single byte[]");

        var buffer = new byte[(int)totalSize];
        int pos = 0;

        WriteInt32(buffer, ref pos, count);
        WriteInt32(buffer, ref pos, offsetsLen);
        WriteInt32(buffer, ref pos, dataLen);

        // offsets
        for (int i = 0; i < offsetsLen; i++)
            WriteInt32(buffer, ref pos, pool.Offsets[i]);

        // data
        Buffer.BlockCopy(pool.Data, 0, buffer, pos, dataLen);
        pos += dataLen;

        return buffer;
    }

    public static PackedStringPool BytesToPool(byte[] bytes)
    {
        int pos = 0;

        int count = ReadInt32(bytes, ref pos);
        int offsetsLen = ReadInt32(bytes, ref pos);
        int dataLen = ReadInt32(bytes, ref pos);

        if (offsetsLen != count + 1)
            throw new InvalidOperationException("Corrupt pool payload (offsetsLen != count+1).");

        var offsets = new int[offsetsLen];
        for (int i = 0; i < offsetsLen; i++)
            offsets[i] = ReadInt32(bytes, ref pos);

        var data = new byte[dataLen];
        Buffer.BlockCopy(bytes, pos, data, 0, dataLen);
        pos += dataLen;

        // Optional sanity: sentinel must match data length
        if (offsets[^1] != dataLen)
            throw new InvalidOperationException("Corrupt pool payload (sentinel offset != dataLen).");

        return new PackedStringPool(data, offsets);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteInt32(byte[] buffer, ref int pos, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(pos, 4), value);
        pos += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadInt32(byte[] buffer, ref int pos)
    {
        int v = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(pos, 4));
        pos += 4;
        return v;
    }
}