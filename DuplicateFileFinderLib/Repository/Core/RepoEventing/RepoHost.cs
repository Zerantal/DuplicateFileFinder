// DuplicateFileFinderLib/Repository/Core/RepoHost.cs

using System.Threading.Channels;

using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinderLib.Repository.Core.RepoEventing;

public sealed class RepoHost : IRepoHost
{
    private readonly Dictionary<Type, object> _services = new();
    // Track disposables in deterministic order
    private readonly List<IAsyncDisposable> _disposables = new();

    // Explicit, ordered barriers
    private readonly List<IIndexGenerationBarrier> _generationBarriers = new();
    private readonly List<IReadyState> _readyStates = new();

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

        host.RegisterIndexPlugin<IHashIndexReadModel>(
            new HashIndexPlugin(indexDir),
            repo);

        host.RegisterIndexPlugin<ITreeIndexReadModel>(
            new TreeIndexPlugin(indexDir),
            repo);

        // Wait until all plugins processed bootstrap
        foreach (var ready in host._readyStates)
            await ready.WhenReadyAsync(ct).ConfigureAwait(false);

        // ---- Coordinator ----

        var coordinator = new IndexRebuildCoordinator(
            host._generationBarriers,
            (gen, scanRootId) =>
                host.IndexesRebuilt?.Invoke(host, new RepoIndexesRebuiltEventArgs(gen, scanRootId)));

        repo.RegisterEventSink(coordinator);
        host.Track(coordinator);

        return host;
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
            catch { /* swallow/log */ }
        }
    }

    // ---------------------------
    // Coordinator
    // ---------------------------

    private sealed class IndexRebuildCoordinator : IRepoEventSink, IAsyncDisposable
    {
        private readonly IReadOnlyList<IIndexGenerationBarrier> _barriers;
        private readonly Action<long, long?> _onIndexesRebuilt;

        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<RepoEvent> _channel;
        private readonly Task _worker;

        public IndexRebuildCoordinator(
            IReadOnlyList<IIndexGenerationBarrier> barriers,
            Action<long, long?> onIndexesRebuilt,
            int capacity = 256)
        {
            _barriers = barriers;
            _onIndexesRebuilt = onIndexesRebuilt;

            _channel = Channel.CreateBounded<RepoEvent>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            _worker = Task.Run(ProcessLoopAsync);
        }

        public void Post(RepoEvent evt) => _channel.Writer.TryWrite(evt);

        private async Task ProcessLoopAsync()
        {
            try
            {
                await foreach (var evt in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    long gen;
                    long? scanRootId;

                    switch (evt)
                    {
                        case ScanRootSnapshotReplacedEvent replaced:
                            gen = replaced.Generation;
                            scanRootId = replaced.ScanRootId;
                            break;

                        case RepoScanRootRemovedEvent removed:
                            gen = removed.Generation;
                            scanRootId = removed.ScanRootId;
                            break;

                        default:
                            continue;
                    }

                    foreach (var barrier in _barriers)
                        await barrier.WhenProcessedGenerationAsync(gen, _cts.Token).ConfigureAwait(false);

                    _onIndexesRebuilt(gen, scanRootId);
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _channel.Writer.TryComplete();
            await _worker.ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
