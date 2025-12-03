namespace DuplicateFileFinderLib.Repository.Interfaces;

public interface IRepoHost : IAsyncDisposable
{
    IRepo Repo { get; }
    IHashIndexReadModel HashIndex { get; }
}