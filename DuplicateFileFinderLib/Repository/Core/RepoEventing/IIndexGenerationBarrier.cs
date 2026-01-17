namespace DuplicateFileFinderLib.Repository.Core.RepoEventing;

public interface IIndexGenerationBarrier
{
    Task WhenProcessedGenerationAsync(long generation, CancellationToken ct);
}
