using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public interface ITreeIndexReadModel : ITreeIndexStatsReadModel
{
    ReadOnlySpan<DirHandle> GetChildDirs(DirHandle dir);
    ReadOnlySpan<FileHandle> GetChildFiles(DirHandle dir);
}
