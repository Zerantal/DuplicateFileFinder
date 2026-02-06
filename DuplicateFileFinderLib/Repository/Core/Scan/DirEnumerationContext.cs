namespace DuplicateFileFinderLib.Repository.Core.Scan;

public struct DirEnumerationContext
{
    internal readonly DirId ParentDirId;

    // Expected remaining entries that were present in baseline but not yet seen in this enumeration.
    internal readonly Dictionary<string, BaseLineDirMapValue> ExpectedDirs;
    internal readonly Dictionary<string, BaseLineFileMapValue> ExpectedFiles;

    internal DirEnumerationContext(
        DirId parentDirId,
        Dictionary<string, BaseLineDirMapValue> expectedDirs,
        Dictionary<string, BaseLineFileMapValue> expectedFiles)
    {
        ParentDirId = parentDirId;
        ExpectedDirs = expectedDirs;
        ExpectedFiles = expectedFiles;
    }
}
