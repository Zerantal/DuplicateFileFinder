namespace DuplicateFileFinderLib.Repository.Plugins.Interfaces;

public interface ITreeIndexReadModel
{
    IReadOnlyList<long> GetChildFileIds(long dirId);
    IReadOnlyList<long> GetChildDirIds(long dirId);
}