using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public interface ITreeIndexReadModel : ITreeIndexStatsReadModel
{
    ReadOnlySpan<DirHandle> GetChildDirs(DirHandle dir);
    ReadOnlySpan<FileHandle> GetChildFiles(DirHandle dir);

    // preorder subtree interval for a directory
    bool TryGetSubtreeRange(DirHandle dir, out SubtreeRange range);

    // preorder of the parent directory for a file (via file handle)
    bool TryGetFileDirPreorder(FileHandle file, out int preorder);
}

