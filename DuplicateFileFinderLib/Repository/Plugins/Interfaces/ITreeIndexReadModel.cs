using System.Collections.Immutable;

namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public interface ITreeIndexReadModel : ITreeIndexStatsReadModel
{
    ImmutableArray<long> GetChildDirIds(long dirId);
    ImmutableArray<long> GetChildFileIds(long dirId);
}