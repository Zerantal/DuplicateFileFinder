namespace DuplicateFileFinderLib.Repository.Interfaces;

public interface IRepoPlugin : IRepoEventSink, IAsyncDisposable
{
    /// <summary>
    /// Completed when the plugin has processed its initial bootstrap
    /// and is ready for queries.
    /// </summary>
    Task WhenReadyAsync(CancellationToken ct = default);
}