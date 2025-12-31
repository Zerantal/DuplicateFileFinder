namespace DuplicateFileFinderLib.Repository.Core.Scan;

using BaseLineMapValue = (long id, string name, Models.ScanEntryStatus status, long lastSeen);

public struct DirEnumerationContext
{
    internal readonly long ParentDirId;

    // Expected remaining entries that were present in baseline but not yet seen in this enumeration.
    internal readonly Dictionary<string, BaseLineMapValue> ExpectedDirs;
    internal readonly Dictionary<string, BaseLineMapValue> ExpectedFiles;

    internal DirEnumerationContext(
        long parentDirId,
        Dictionary<string, BaseLineMapValue> expectedDirs,
        Dictionary<string, BaseLineMapValue> expectedFiles)
    {
        ParentDirId = parentDirId;
        ExpectedDirs = expectedDirs;
        ExpectedFiles = expectedFiles;
    }
}
