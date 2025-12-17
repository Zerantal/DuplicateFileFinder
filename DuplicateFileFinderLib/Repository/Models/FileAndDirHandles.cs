namespace DuplicateFileFinderLib.Repository.Models;

public readonly record struct DirHandle(long ScanRootId = -1, int Index = -1)
{
    public bool IsValid =>  Index != -1;
};

public readonly record struct FileHandle(long ScanRootId = -1, int Index = -1)
{
    public bool IsValid =>  Index != -1;
};