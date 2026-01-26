
using MemoryPack;

namespace MemPackBench.PoolBench;

[MemoryPackable]
public partial class StringArrayContainer
{
    [MemoryPackOrder(0)]
    public string[] Strings { get; set; } = Array.Empty<string>();
}
