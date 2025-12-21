namespace DuplicateFileFinderLib.Repository.Core.Scan;

public readonly record struct FileHashToken(long DirId, string Name, long Size, long CreatedTicks, long ModifiedTicks);

public readonly record struct FileHashDecision(bool ShouldHash, FileHashToken Token)
{
    public static FileHashDecision NoHash => new(false, default);
}