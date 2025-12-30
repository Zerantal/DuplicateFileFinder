// DuplicateFileFinderLib/Repository/Interfaces/IRepoHost.cs

using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinderLib.Repository.Interfaces;

public interface IRepoHost : IAsyncDisposable
{
    IRepo Repo { get; }
    IHashIndexReadModel HashIndex { get; }
    ITreeIndexReadModel TreeIndex { get; }
    IFileDirReadModel FileDirIndex { get; }

    /// <summary>
    /// Fired after all index plugins have processed a repo generation change (e.g., after a scan commit).
    /// Consumers can reload views knowing indices are coherent.
    /// </summary>
    event EventHandler<RepoIndexesRebuiltEventArgs>? IndexesRebuilt;
}

public sealed class RepoIndexesRebuiltEventArgs(long generation, long? scanRootId) : EventArgs
{
    public long Generation { get; } = generation;
    public long? ScanRootId { get; } = scanRootId;
}
