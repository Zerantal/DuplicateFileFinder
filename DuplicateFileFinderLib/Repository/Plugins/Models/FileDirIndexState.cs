using DuplicateFileFinderLib.Repository.Core.Models;

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

[MemoryPackable(SerializeLayout.Sequential)]
public sealed partial record FileDirIndexState
{
    public long LastIndexedGeneration { get; init; }
    public required Dictionary<long, DirHandle> DirsById { get; init; }
    public required Dictionary<long, FileHandle> FilesById { get; init; }
}
