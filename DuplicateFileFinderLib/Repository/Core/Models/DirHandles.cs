namespace DuplicateFileFinderLib.Repository.Core.Models;

public readonly record struct DirHandle(long ScanRootId, int Index)
{
    public DirHandle() : this(-1, -1) { }

    public static DirHandle Invalid => new(-1, -1);

    public bool IsValid => ScanRootId >= 0 && Index >= 0;
};
