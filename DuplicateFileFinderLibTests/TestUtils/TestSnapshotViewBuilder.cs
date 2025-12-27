using System.Collections.Generic;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLibTests.TestUtils;

internal sealed class TestSnapshotViewBuilder
{
    private readonly PackedStringBuilder _strings = new();
    private readonly List<DirRecordV2> _dirs = new();
    private readonly List<FileRecordV2> _files = new();

    public TestSnapshotViewBuilder Dir(
        long dirId,
        long parentDirId,
        string? name,
        ScanEntryStatus status,
        long lastSeenScanSequence)
    {
        _dirs.Add(new DirRecordV2
        {
            DirId = dirId,
            ParentDirId = parentDirId,
            NameStrIdx = _strings.InternOrMinusOne(name),
            Status = status,
            LastSeenScanSequence = lastSeenScanSequence,
        });
        return this;
    }

    public TestSnapshotViewBuilder File(
        long fileId,
        long dirId,
        string? name,
        ScanEntryStatus status,
        long lastSeenScanSequence)
    {
        _files.Add(new FileRecordV2
        {
            FileId = fileId,
            DirId = dirId,
            NameStrIdx = _strings.InternOrMinusOne(name),
            Status = status,
            LastSeenScanSequence = lastSeenScanSequence,
        });
        return this;
    }

    public ScanRootSnapshotView Build(long scanRootId = 1)
        => new()
        {
            ScanRootId = scanRootId,
            StringPool = _strings.Build(),
            Dirs = _dirs,
            Files = _files,
        };
}