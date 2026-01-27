// DuplicateFileFinderLib/Repository/Plugins/Models/FolderHashIndexState.cs

using DuplicateFileFinderLib.Repository.Core.Models;

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

[MemoryPackable(SerializeLayout.Sequential)]
public sealed partial record FolderHashIndexState
{
    public long LastIndexedGeneration { get; init; }

    public int TotalDuplicateFolderCount { get; init; }

    public DirHandle[]? AllDirs { get; init; }

    public FolderGroupDescriptor[]? Groups { get; init; }

    public int[]? ByCountDesc { get; init; }
}

