namespace DuplicateFileFinderLib.Repository.Core.Models;

public readonly record struct DeleteResult(
    bool Success,
    long Generation,
    long ScanRootId,
    int DeletedFileCount,
    int DeletedDirCount,
    string? Error)
{
    public static DeleteResult Ok(long gen, long rootId, int deletedFiles, int deletedDirs)
        => new(true, gen, rootId, deletedFiles, deletedDirs, null);

    public static DeleteResult Fail(long gen, long rootId, string error)
        => new(false, gen, rootId, 0, 0, error);
}
