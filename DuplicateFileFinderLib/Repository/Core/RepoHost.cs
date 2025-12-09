using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinderLib.Repository.Core;

public sealed class RepoHost : IRepoHost
{
    public IRepo Repo { get; }
    public IHashIndexReadModel HashIndex { get; }
    
    public ITreeIndexReadModel TreeIndex { get; }

    // Track disposables in deterministic order
    private readonly List<IAsyncDisposable> _disposables = new();

    private RepoHost(IRepo repo, IHashIndexReadModel hashIndex, ITreeIndexReadModel treeIndex)
    {
        Repo      = repo;
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
        var treeIndexDir = Path.Combine(repoDir, nameof(TreeIndexReadModel));
        var treeIndex = new TreeIndexReadModel(treeIndexDir);

        // 3. Bootstrap + subscribe plugins
        repo.RegisterEventSinkWithBootstrap(hashIndex);
        repo.RegisterEventSinkWithBootstrap(treeIndex);
        
        // 3.5 Wait until plugins have processed their bootstrap events
        await hashIndex.WhenReadyAsync(ct).ConfigureAwait(false);
        await treeIndex.WhenReadyAsync(ct).ConfigureAwait(false);

        // 4. Build host
        var host = new RepoHost(repo, hashIndex, treeIndex);
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