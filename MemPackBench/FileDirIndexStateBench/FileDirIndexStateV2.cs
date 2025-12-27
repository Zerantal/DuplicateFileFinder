using MemoryPack;

namespace MemPackBench.FileDirIndexStateBench;

[MemoryPackable(SerializeLayout.Sequential)]
public sealed partial record FileDirIndexStateV2
{
    public long LastIndexedGeneration { get; init; }
    public long LastIndexedLogSequence { get; init; }
    public required KeyValuePair<long, DirHandle>[] DirsById { get; init; }
    public required KeyValuePair<long, FileHandle>[] FilesById { get; init; }
}