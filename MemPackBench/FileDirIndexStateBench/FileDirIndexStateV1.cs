using MemoryPack;

namespace MemPackBench.FileDirIndexStateBench;

[MemoryPackable(SerializeLayout.Sequential)]
public sealed partial record FileDirIndexStateV1
{
    public long LastIndexedGeneration { get; init; }
    public long LastIndexedLogSequence { get; init; }
    public required Dictionary<long, DirHandle> DirsById { get; init; }
    public required Dictionary<long, FileHandle> FilesById { get; init; }
}
