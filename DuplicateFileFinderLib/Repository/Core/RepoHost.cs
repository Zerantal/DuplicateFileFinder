// DuplicateFileFinderLib/Repository/Core/RepoHost.cs

using System.Threading.Channels;

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

    public event EventHandler<RepoIndexesRebuiltEventArgs>? IndexesRebuilt;

    // Track disposables in deterministic order
    private readonly List<IAsyncDisposable> _disposables = new();

    private RepoHost(IRepo repo, IFileDirReadModel fileDirIndex, IHashIndexReadModel hashIndex, ITreeIndexReadModel treeIndex)
    {
        Repo = repo;
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

        // 5. Index rebuild coordination (raise IndexesRebuilt only after all plugins processed generation)
        var coordinator = new IndexRebuildCoordinator(
            fileDirIndex,
            hashIndex,
            treeIndex,
            (gen, scanRootId) => host.IndexesRebuilt?.Invoke(host, new RepoIndexesRebuiltEventArgs(gen, scanRootId)));

        repo.RegisterEventSink(coordinator);
        host._disposables.Add(coordinator);

        // Dispose plugins + repo
        host._disposables.Add(fileDirIndex);
        host._disposables.Add(hashIndex);
        host._disposables.Add(treeIndex);
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

    private sealed class IndexRebuildCoordinator : IRepoEventSink, IAsyncDisposable
    {
        private readonly FileDirIndexPlugin _fileDir;
        private readonly HashIndexPlugin _hash;
        private readonly TreeIndexPlugin _tree;
        private readonly Action<long, long?> _onIndexesRebuilt;

        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<RepoEvent> _channel;
        private readonly Task _worker;

        public IndexRebuildCoordinator(
            FileDirIndexPlugin fileDir,
            HashIndexPlugin hash,
            TreeIndexPlugin tree,
            Action<long, long?> onIndexesRebuilt,
            int capacity = 256)
        {
            _fileDir = fileDir;
            _hash = hash;
            _tree = tree;
            _onIndexesRebuilt = onIndexesRebuilt;

            var options = new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = false,
                SingleReader = true
            };
            _channel = Channel.CreateBounded<RepoEvent>(options);
            _worker = Task.Run(ProcessLoopAsync);
        }

        public void Post(RepoEvent evt)
        {
            // Must be non-blocking
            _channel.Writer.TryWrite(evt);
        }

        private async Task ProcessLoopAsync()
        {
            try
            {
                await foreach (var evt in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    if (evt is not ScanRootSnapshotReplacedEvent committed)
                        continue;

                    var gen = committed.Generation;
                    var scanRootId = committed.ScanRootId;

                    // Wait for all plugins to reach this generation
                    await _fileDir.WhenProcessedGenerationAsync(gen, _cts.Token).ConfigureAwait(false);
                    await _hash.WhenProcessedGenerationAsync(gen, _cts.Token).ConfigureAwait(false);
                    await _tree.WhenProcessedGenerationAsync(gen, _cts.Token).ConfigureAwait(false);

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
        }
    }
}
