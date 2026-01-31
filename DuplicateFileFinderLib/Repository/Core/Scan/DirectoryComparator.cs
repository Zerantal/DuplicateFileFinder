using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Repository.Core.Scan;

internal sealed class DirectoryComparator(BaselineIndex baseline)
{
    public DirEnumerationContext Begin(DirCursor parent)
    {
        var parentId = parent.DirId;

        var expectedDirs = new Dictionary<string, BaseLineDirMapValue>(PathUtils.PathComparer);
        var expectedFiles = new Dictionary<string, BaseLineFileMapValue>(PathUtils.PathComparer);

        if (baseline.TryGetChildDirMap(parentId, out var dirs))
            foreach (var kv in dirs)
                expectedDirs[kv.Key] = kv.Value;

        if (baseline.TryGetChildFileMap(parentId, out var files))
            foreach (var kv in files)
                expectedFiles[kv.Key] = kv.Value;

        return new DirEnumerationContext(parentId, expectedDirs, expectedFiles);
    }

    public DirId TryConsumeExpectedDirId(ref DirEnumerationContext ctx, string name)
    {
        if (ctx.ExpectedDirs.Remove(name, out var existing))
        {
            return existing.dirId;
        }
        return -1;
    }

    public FileId TryConsumeExpectedFileId(ref DirEnumerationContext ctx, string name)
    {
        if (ctx.ExpectedFiles.Remove(name, out var existing))
        {
            return existing.fileId;
        }
        return -1;
    }

    public IEnumerable<BaseLineDirMapValue> ConsumeRemainingExpectedDirs(ref DirEnumerationContext ctx)
        => ctx.ExpectedDirs.Values;

    public IEnumerable<BaseLineFileMapValue> ConsumeRemainingExpectedFiles(ref DirEnumerationContext ctx)
        => ctx.ExpectedFiles.Values;

    public void Clear(ref DirEnumerationContext ctx)
    {
        ctx.ExpectedDirs.Clear();
        ctx.ExpectedFiles.Clear();
    }
}
