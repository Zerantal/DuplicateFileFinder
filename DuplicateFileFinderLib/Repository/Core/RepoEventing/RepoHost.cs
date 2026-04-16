// DuplicateFileFinderLib/Repository/Core/RepoHost.cs

using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins;
using DuplicateFileFinderLib.Repository.Plugins.FileDir;
using DuplicateFileFinderLib.Repository.Plugins.Hash;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Tree;

using NLog;

namespace DuplicateFileFinderLib.Repository.Core.RepoEventing;

public sealed class RepoHost : IRepoHost
{
    private static readonly Logger s_log = LogManager.GetCurrentClassLogger();

    private readonly Dictionary<Type, object> _services = new();
    private readonly List<IAsyncDisposable> _disposables = new();

    // Explicit, ordered barriers
    private readonly List<IIndexGenerationBarrier> _generationBarriers = new();
    private readonly List<IReadyState> _readyStates = new();

    private IndexRebuildCoordinator? _indexRebuildCoordinator;

    public long LastIndexedGeneration { get; private set; }

    public IRepo Repo { get; }

    public IHashIndexReadModel HashIndex => Get<IHashIndexReadModel>();
    public ITreeIndexReadModel TreeIndex => Get<ITreeIndexReadModel>();
    public IFileDirReadModel FileDirIndex => Get<IFileDirReadModel>();

    public event EventHandler<RepoIndexesRebuiltEventArgs>? IndexesRebuilt;

    private RepoHost(IRepo repo)
    {
        Repo = repo;
    }

    public static async Task<RepoHost> OpenAsync(string repoDir, CancellationToken ct = default)
    {
        var repo = await Core.Repo.OpenAsync(repoDir, ct).ConfigureAwait(false);
        var host = new RepoHost(repo);
        var indexDir = Path.Combine(repoDir, "indexes");

        // Ensure repo disposed last
        if (repo is IAsyncDisposable ad)
            host.Track(ad);

        // ---- Index plugins ----

        host.RegisterIndexPlugin<IFileDirReadModel>(
            new FileDirIndexPlugin(indexDir),
            repo);

        var treeIndex = new TreeIndexPlugin(indexDir);
        host.RegisterIndexPlugin<ITreeIndexReadModel>(
            treeIndex,
            repo);

        host.RegisterIndexPlugin<IHashIndexReadModel>(
            new HashIndexPlugin(indexDir, treeIndex),
            repo);


        // Wait until all plugins processed bootstrap
        foreach (var ready in host._readyStates)
            await ready.WhenReadyAsync(ct).ConfigureAwait(false);

        host.LastIndexedGeneration = repo.Generation;

        // ---- Coordinator ----

        var coordinator = new IndexRebuildCoordinator(
            host._generationBarriers,
            initialCompletedGeneration: host.LastIndexedGeneration,
            onIndexesRebuilt: (gen, scanRootId) =>
            {
                var evt = new RepoIndexesRebuiltEventArgs(gen, scanRootId);
                s_log.Info("RepoIndexesRebuilt event: " + evt);
                host.LastIndexedGeneration = gen;
                host.IndexesRebuilt?.Invoke(host, evt);
            });

        host._indexRebuildCoordinator = coordinator;

        repo.RegisterEventSink(coordinator);
        host.Track(coordinator);

        return host;
    }

    public Task WhenIndexesRebuiltAsync(long generation, CancellationToken ct = default)
    {
        if (LastIndexedGeneration >= generation)
            return Task.CompletedTask;

        return _indexRebuildCoordinator?.WhenIndexesRebuiltAsync(generation, ct)
               ?? Task.CompletedTask;
    }

    // -------------------------------------------------
    // Registration helpers
    // -------------------------------------------------

    private void RegisterIndexPlugin<TService>(
        ChannelRepoPlugin plugin,
        Repo repo)
        where TService : class
    {
        Track(plugin);

        // Register sink WITH bootstrap
        repo.RegisterEventSinkWithBootstrap(plugin);

        // Expose as read model
        RegisterService((TService)(object)plugin);

        // Track lifecycle interfaces
        if (plugin is IReadyState ready)
            _readyStates.Add(ready);

        if (plugin is IIndexGenerationBarrier barrier)
            _generationBarriers.Add(barrier);
    }

    public void RegisterEventSink(IRepoEventSink sink)
        => ((Repo)Repo).RegisterEventSink(sink);

    public TService RegisterService<TService>(TService instance)
        where TService : notnull
    {
        _services[typeof(TService)] = instance;
        return instance;
    }

    public TService Get<TService>()
        => (TService)_services[typeof(TService)];

    public T Track<T>(T disposable) where T : IAsyncDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    public async ValueTask DisposeAsync()
    {
        // Dispose in reverse order of registration
        for (var i = _disposables.Count - 1; i >= 0; i--)
        {
            try { await _disposables[i].DisposeAsync().ConfigureAwait(false); }
            catch
            {
                /* swallow/log */
            }
        }
    }
}
