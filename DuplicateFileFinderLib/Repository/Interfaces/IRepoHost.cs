using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinderLib.Repository.Interfaces;

public interface IRepoHost : IAsyncDisposable
{
    IRepo Repo { get; }
    IHashIndexReadModel HashIndex { get; }
    ITreeIndexReadModel TreeIndex { get; }
    IFileDirReadModel FileDirIndex { get; }

    long LastIndexedGeneration { get; }

    /// <summary>
    /// Fired after all index plugins have processed a repo generation change (e.g., after a scan commit).
    /// Consumers can reload views knowing indices are coherent.
    /// </summary>
    event EventHandler<RepoIndexesRebuiltEventArgs>? IndexesRebuilt;

    /// <summary>
    /// Wait until all index plugins have rebuilt through the specified generation.
    /// </summary>
    Task WhenIndexesRebuiltAsync(long generation, CancellationToken ct = default);
}

public sealed class RepoIndexesRebuiltEventArgs(long generation, long? scanRootId) : EventArgs
{
    public long Generation { get; } = generation;
    public long? ScanRootId { get; } = scanRootId;
}
