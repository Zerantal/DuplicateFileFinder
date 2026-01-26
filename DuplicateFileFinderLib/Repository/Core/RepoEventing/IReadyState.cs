namespace DuplicateFileFinderLib.Repository.Core.RepoEventing;

public interface IReadyState
{
    Task WhenReadyAsync(CancellationToken ct);
}
