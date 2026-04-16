using System.Runtime.CompilerServices;

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Hash;

public sealed partial class HashIndexPlugin : ChannelRepoPlugin, IHashIndexReadModel
{
    private const string StateFileName = "hash-index.bin";
    private readonly string _dataDirectory;

    // Published: concatenation of all group file handles
    private volatile FileHandle[] _allFiles = [];

    // Published: dense descriptors (unsorted)
    private volatile HashGroupDescriptor[] _groups = [];

    // Published: sorted “views” as indices into _groups[]
    private volatile int[] _bySizeDesc = [];
    private volatile int[] _byCountDesc = [];

    // Published stats snapshot
    private volatile HashIndexPlugin.StatsSnapshot _stats = HashIndexPlugin.StatsSnapshot.Empty;

    // Transient helper state for incremental delete handling.
    // Not persisted; rebuilt lazily on the first delete event after bootstrap/load.
    private volatile Dictionary<FileHandle, int> _groupIndexByFileHandle = new();

    private long _lastIndexedGeneration;

    private volatile bool _sortViewsDirty;

    // Needed for filtering duplicate files within a subtree
    private readonly ITreeIndexReadModel _treeIndex;

    private const int ImmediateSortMaterializationThreshold = 64;

    private bool _deferredSortSaveQueued;

    public HashIndexPlugin(string dataDirectory, ITreeIndexReadModel treeIndex)
        : base(4096)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory must be non-empty.", nameof(dataDirectory));

        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(_dataDirectory);

        _treeIndex = treeIndex ?? throw new ArgumentNullException(nameof(treeIndex));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long TotalBytes(in HashGroupDescriptor d) => d.FileSizeBytes * d.Count;

    // ---------------------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------------------

    protected override async ValueTask HandleEventAsync(RepoEvent evt, CancellationToken ct)
    {
        if (evt is HashIndexPlugin.MaterializeAndSaveEvent materialize)
        {
            using (TimingLog.Start($"Processing {evt.GetType().Name} ({nameof(HashIndexPlugin)})"))
            {
                _deferredSortSaveQueued = false;

                // Ignore stale queued work.
                if (materialize.Generation != _lastIndexedGeneration)
                    return;

                if (!_sortViewsDirty)
                    return;

                EnsureSortedViews();
                SaveState(materializeSortViews: false);
            }

            return;
        }

        await base.HandleEventAsync(evt, ct).ConfigureAwait(false);
    }

    protected override ValueTask OnBootstrapEventAsync(BootstrapEvent evt, CancellationToken ct)
    {
        if (TryLoadState(evt.Generation))
        {
            _lastIndexedGeneration = evt.Generation;
            return ValueTask.CompletedTask;
        }

        RebuildAndCommit(evt.Generation, () => RebuildFromSnapshot(evt.RepoSnapshotView));

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnScanRootSnapshotReplacedEventAsync(
        ScanRootSnapshotReplacedEvent evt,
        CancellationToken ct)
    {
        // Ignore stale/out-of-order events (channel may drop old items).
        if (evt.Generation <= _lastIndexedGeneration)
            return ValueTask.CompletedTask;

        RebuildAndCommit(evt.Generation, () => RebuildFromSnapshot(evt.RepoSnapshotView));
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnRepoFileDeletedEventAsync(RepoFileDeletedEvent evt, CancellationToken ct)
    {
        EnsureGroupIndexBuilt();

        if (!_groupIndexByFileHandle.TryGetValue(evt.File, out var groupIndex))
        {
            _lastIndexedGeneration = evt.Generation;
            SaveState(materializeSortViews: false);
            return ValueTask.CompletedTask;
        }

        var affectedGroupCount = RebuildSingleGroupExcludingFile(groupIndex, evt.File);

        _lastIndexedGeneration = evt.Generation;
        PersistAfterMutation(affectedGroupCount);

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnRepoDirDeletedEventAsync(RepoDirDeletedEvent evt, CancellationToken ct)
    {
        if (evt.Generation <= _lastIndexedGeneration)
            return ValueTask.CompletedTask;

        if (evt.DeletedFiles.Length == 0)
        {
            _lastIndexedGeneration = evt.Generation;
            SaveState(materializeSortViews: false);
            return ValueTask.CompletedTask;
        }

        var removedHandles = new FileHandle[evt.DeletedFiles.Length];
        for (var i = 0; i < evt.DeletedFiles.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            removedHandles[i] = evt.DeletedFiles[i].FileHandle;
        }

        var affectedGroupCount = RebuildExcludingRemovedHandles(removedHandles);

        _lastIndexedGeneration = evt.Generation;
        PersistAfterMutation(affectedGroupCount);

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnRepoScanRootRemovedEventAsync(
        RepoScanRootRemovedEvent evt,
        CancellationToken ct)
    {
        if (evt.Generation <= _lastIndexedGeneration)
            return ValueTask.CompletedTask;

        RebuildAndCommit(evt.Generation, () => RebuildExcludingScanRoot(evt.ScanRootIdValue));
        return ValueTask.CompletedTask;
    }

    private void RebuildAndCommit(long generation, Action rebuild)
    {
        rebuild();
        _lastIndexedGeneration = generation;
        SaveState();
    }
}
