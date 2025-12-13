namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public interface ITreeIndexReadModel : ITreeIndexStatsReadModel
{
    IReadOnlyList<long> GetChildFileIds(long dirId);
    IReadOnlyList<long> GetChildDirIds(long dirId);
}