namespace DuplicateFileFinderLib.Repository.Core.Models;

public readonly record struct FileHandle(ScanRootId ScanRootId, int Index)
{
    public FileHandle() : this(-1, -1) { }

    public static FileHandle Invalid => new(-1, -1);

    public bool IsValid => ScanRootId >= 0 && Index >= 0;
};
