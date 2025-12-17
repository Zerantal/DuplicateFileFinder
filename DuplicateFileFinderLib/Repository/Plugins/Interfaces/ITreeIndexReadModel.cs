using System.Collections.Immutable;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public interface ITreeIndexReadModel : ITreeIndexStatsReadModel
{
    ImmutableArray<DirHandle> GetChildDirs(DirHandle dir);
    ImmutableArray<FileHandle> GetChildFiles(DirHandle dir);
}