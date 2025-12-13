using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public interface ITreeIndexStatsReadModel
{
    DirAggregateStats GetDirStats(long dirId);
}