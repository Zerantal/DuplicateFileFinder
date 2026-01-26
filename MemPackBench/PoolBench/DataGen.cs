namespace MemPackBench.PoolBench;

static class DataGen
{
    public static string[] MakeRandomStrings(int count, int maxLen, int seed)
    {
        var rng = new Random(seed);
        var arr = new string[count];

        // ASCII only: UTF-8 byte length == char length
        var buf = new char[maxLen];

        for (int i = 0; i < count; i++)
        {
            int len = rng.Next(1, maxLen + 1);
            for (int j = 0; j < len; j++)
                buf[j] = (char)('a' + rng.Next(0, 26));
            arr[i] = new string(buf, 0, len);
        }

        return arr;
    }
}
