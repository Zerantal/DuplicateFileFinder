namespace DuplicateFileFinderLib.Repository.Core.Scan;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public readonly record struct FileHashToken(DirId DirId, string Name, long Size);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public readonly record struct FileHashDecision(bool ShouldHash, FileHashToken Token)
{
    public static FileHashDecision NoHash => new(false, default);
}
