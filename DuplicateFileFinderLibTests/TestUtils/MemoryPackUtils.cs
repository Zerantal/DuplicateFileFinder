using MemoryPack;

namespace DuplicateFileFinderLibTests.TestUtils;

public class MemoryPackUtils
{
    internal static T RoundTrip<T>(T value)
    {
        var bytes = MemoryPackSerializer.Serialize(value);
        return MemoryPackSerializer.Deserialize<T>(bytes)!;
    }
}