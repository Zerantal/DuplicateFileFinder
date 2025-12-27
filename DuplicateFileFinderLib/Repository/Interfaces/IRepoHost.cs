using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinderLib.Repository.Interfaces;

public interface IRepoHost : IAsyncDisposable
{
    IRepo Repo { get; }
    IHashIndexReadModel HashIndex { get; }
    ITreeIndexReadModel TreeIndex { get; }
    IFileDirReadModel FileDirIndex { get; }
}