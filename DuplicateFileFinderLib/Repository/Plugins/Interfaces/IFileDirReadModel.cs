using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public interface IFileDirReadModel
{
    bool TryGetDir(long dirId, out DirHandle handle);
    bool TryGetFile(long fileId, out FileHandle handle);
    int FileCount { get; }
    int DirCount { get; }
}