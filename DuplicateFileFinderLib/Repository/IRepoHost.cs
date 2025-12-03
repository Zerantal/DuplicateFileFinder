namespace DuplicateFileFinderLib.Repository;

public interface IRepoHost : IAsyncDisposable
{
    IRepo Repo { get; }
    IHashIndexReadModel HashIndex { get; }
}