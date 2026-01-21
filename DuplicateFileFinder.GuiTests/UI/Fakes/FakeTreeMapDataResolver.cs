using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public sealed class FakeTreeMapDataResolver : ITreeMapDataResolver
{
    public string DecodeDirName(DirHandle dir) => $"dir-{dir.Index}";
    public string DecodeFileName(FileHandle file) => $"file-{file.Index}";

    public DirRecordV2 GetDirRecord(DirHandle dir)
        => new() { DirId = 100 + dir.Index, ParentDirId = -1, Status = ScanEntryStatus.Enumerated };

    public FileRecordV2 GetFileRecord(FileHandle file)
        => new() { FileId = 200 + file.Index, DirId = 1, Status = ScanEntryStatus.Enumerated };

    public string GetRelativePath(long dirId) => $"rel/{dirId}";

    public DirAggregateStats GetDirStats(DirHandle dir) => new DirAggregateStats
    {
        TotalBytes = 0,
        FileCount = 0,
        DirCount = 0,
        DuplicateFiles = 0,
        DuplicateBytes = 0
    };
}
