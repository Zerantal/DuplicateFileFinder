using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public interface ITreeMapDataResolver
{
    // Names
    string DecodeDirName(DirHandle dir);
    string DecodeFileName(FileHandle file);

    // Records (for tooltips / optional label / etc)
    DirRecordV2 GetDirRecord(DirHandle dir);
    FileRecordV2 GetFileRecord(FileHandle file);

    // Paths
    string GetRelativePath(long dirId);

    // Optional aggregates (for tooltips)
    DirAggregateStats GetDirStats(DirHandle dir);
}
