using DuplicateFileFinderLib.Util;

using BaseLineMapValue = (long id, string name, DuplicateFileFinderLib.Repository.Core.Models.ScanEntryStatus status, long lastSeen);

namespace DuplicateFileFinderLib.Repository.Core.Scan;

internal sealed class DirectoryComparator(BaselineIndex baseline)
{
    public DirEnumerationContext Begin(DirCursor parent)
    {
        var parentId = parent.DirId;

        var expectedDirs = new Dictionary<string, BaseLineMapValue>(PathUtils.PathComparer);
        var expectedFiles = new Dictionary<string, BaseLineMapValue>(PathUtils.PathComparer);

        if (baseline.TryGetChildDirMap(parentId, out var dirs))
            foreach (var kv in dirs) expectedDirs[kv.Key] = kv.Value;

        if (baseline.TryGetChildFileMap(parentId, out var files))
            foreach (var kv in files) expectedFiles[kv.Key] = kv.Value;

        return new DirEnumerationContext(parentId, expectedDirs, expectedFiles);
    }

    public long TryConsumeExpectedDirId(ref DirEnumerationContext ctx, string name)
    {
        if (ctx.ExpectedDirs.Remove(name, out var existing))
        {
            return existing.id;
        }
        return -1;
    }

    public long TryConsumeExpectedFileId(ref DirEnumerationContext ctx, string name)
    {
        if (ctx.ExpectedFiles.Remove(name, out var existing))
        {
            return existing.id;
        }
        return -1;
    }

    public IEnumerable<BaseLineMapValue> ConsumeRemainingExpectedDirs(ref DirEnumerationContext ctx)
        => ctx.ExpectedDirs.Values;

    public IEnumerable<BaseLineMapValue> ConsumeRemainingExpectedFiles(ref DirEnumerationContext ctx)
        => ctx.ExpectedFiles.Values;

    public void Clear(ref DirEnumerationContext ctx)
    {
        ctx.ExpectedDirs.Clear();
        ctx.ExpectedFiles.Clear();
    }
}