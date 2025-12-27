using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinderLib.Repository.Core;

public sealed class RepoHost : IRepoHost
{
    public IRepo Repo { get; }
    public IHashIndexReadModel HashIndex { get; }
    
    public ITreeIndexReadModel TreeIndex { get; }
    
    public IFileDirReadModel FileDirIndex { get; }

    // Track disposables in deterministic order
    private readonly List<IAsyncDisposable> _disposables = new();

    private RepoHost(IRepo repo, IFileDirReadModel fileDirIndex, IHashIndexReadModel hashIndex, ITreeIndexReadModel treeIndex)
    {
        Repo      = repo;
        FileDirIndex = fileDirIndex;
        HashIndex = hashIndex;
        TreeIndex = treeIndex;
        
    }

    public static async Task<RepoHost> OpenAsync(string repoDir, CancellationToken ct = default)
    {
        // 1. Open the repo
        var repo = await Core.Repo.OpenAsync(repoDir, ct).ConfigureAwait(false);

        // 2. Create plugins
        var hashIndexDir = Path.Combine(repoDir, nameof(HashIndexPlugin));
        var hashIndex = new HashIndexPlugin(hashIndexDir);
        var treeIndexDir = Path.Combine(repoDir, nameof(TreeIndexPlugin));
        var treeIndex = new TreeIndexPlugin(treeIndexDir);
        var fileDirIndexDir = Path.Combine(repoDir, nameof(FileDirIndex));
        var fileDirIndex = new FileDirIndexPlugin(fileDirIndexDir);

        // 3. Bootstrap + subscribe plugins
        repo.RegisterEventSinkWithBootstrap(fileDirIndex);
        repo.RegisterEventSinkWithBootstrap(hashIndex);
        repo.RegisterEventSinkWithBootstrap(treeIndex);
        
        // 3.5 Wait until plugins have processed their bootstrap events
        await fileDirIndex.WhenReadyAsync(ct).ConfigureAwait(false);
        await hashIndex.WhenReadyAsync(ct).ConfigureAwait(false);
        await treeIndex.WhenReadyAsync(ct).ConfigureAwait(false);

        // 4. Build host
        var host = new RepoHost(repo, fileDirIndex, hashIndex, treeIndex);
        host._disposables.Add(hashIndex);
        if (repo is IAsyncDisposable asyncDisp)
            host._disposables.Add(asyncDisp);

        return host;
    }

    public async ValueTask DisposeAsync()
    {
        // Dispose in reverse order of registration
        for (var i = _disposables.Count - 1; i >= 0; i--)
        {
            try
            {
                await _disposables[i].DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // swallow/log
            }
        }
    }
}