using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public interface IFileDirReadModel
{
    bool TryGetDir(long dirId, out DirHandle handle);
    bool TryGetFile(long fileId, out FileHandle handle);
    int FileCount { get; }
    int DirCount { get; }
    bool TryGetFilePathById(long fileId, out string relativePath);
    bool TryGetFilePathByHandle(FileHandle fileHandle, out string relativePath);
    bool TryGetDirPathById(long dirId, out string relativePath);
    bool TryGetDirPathByHandle(DirHandle dirHandle, out string relativePath);
}
