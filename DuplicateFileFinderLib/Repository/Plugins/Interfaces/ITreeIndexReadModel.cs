using System.Collections.Immutable;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public interface ITreeIndexReadModel : ITreeIndexStatsReadModel
{
    ImmutableArray<DirHandle> GetChildDirIds(DirHandle dir);
    ImmutableArray<FileHandle> GetChildFileIds(DirHandle dir);
}