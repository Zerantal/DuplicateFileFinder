using MemoryPack;

namespace MemPackBench.FileDirIndexStateBench;

[MemoryPackable(SerializeLayout.Sequential)]
public readonly partial record struct DirHandle(int ScanRootId = -1, int Index = -1)
{
    public bool IsValid => Index >= 0;
}
