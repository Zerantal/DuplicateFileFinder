using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public interface IFileDirReadModel
{
    bool TryGetDir(DirId dirId, out DirHandle handle);
    bool TryGetFile(FileId fileId, out FileHandle handle);
    int FileCount { get; }
    int DirCount { get; }
    bool TryGetFilePathById(FileId fileId, out string relativePath);
    bool TryGetFilePathByHandle(FileHandle fileHandle, out string relativePath);
    bool TryGetDirPathById(DirId dirId, out string relativePath);
    bool TryGetDirPathByHandle(DirHandle dirHandle, out string relativePath);
}
