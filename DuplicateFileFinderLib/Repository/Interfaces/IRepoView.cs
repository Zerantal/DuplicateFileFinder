using DuplicateFileFinderLib.Repository.Models;
using FileRecord = DuplicateFileFinderLib.Repository.Storage.Models.FileRecord;

namespace DuplicateFileFinderLib.Repository.Interfaces;

public interface IRepoView
{
    IReadOnlyDictionary<long, DirRecord> Dirs { get; }
    IReadOnlyDictionary<long, FileRecord> Files { get; }

    DirRecord? TryGetDir(long dirId);
    FileRecord? TryGetFile(long fileId);
}