// DuplicateFileFinderLib/Repository/Plugins/TreeIndexPlugin.cs

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;

using NLog;

namespace DuplicateFileFinderLib.Repository.Plugins.Tree;

public sealed partial class TreeIndexPlugin : ChannelRepoPlugin, ITreeIndexReadModel
{
    private static readonly Logger s_log = LogManager.GetCurrentClassLogger();

    private const string StateFileName = "tree-index.bin";

    // Dir indices are mostly dense per scan root, but some holes exist (Deleted/None).
    // Allow modest gaps to stay within a single segment.
    private const int SegmentGapThreshold = 64;

    // Published, read-only snapshots (never mutate after publishing).
    // Rebuilt on plugin worker thread; swapped atomically for readers.
    private volatile Dictionary<ScanRootId, RootTreeIndexState> _roots = new();

    private readonly string _dataDirectory;
    private long _lastIndexedGeneration;

    private volatile RepoSnapshotView? _snapshotView;

    public TreeIndexPlugin(string dataDirectory)
        : base(4096)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory must be non-empty.", nameof(dataDirectory));

        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(_dataDirectory);
    }

    // ---------------------------------------------------------------------
    // Public query surface (lock-free)
    // ---------------------------------------------------------------------

    public ReadOnlySpan<DirHandle> GetChildDirs(DirHandle dir)
    {
        var roots = _roots;

        if (!roots.TryGetValue(dir.ScanRootId, out var root))
            return ReadOnlySpan<DirHandle>.Empty;

        if (!root.ChildDirSliceByDirIndex.TryGetValue(dir.Index, out var slice) || slice.IsEmpty)
            return ReadOnlySpan<DirHandle>.Empty;

        return root.ChildDirsPool.AsSpan(slice.Offset, slice.Length);
    }

    public ReadOnlySpan<FileHandle> GetChildFiles(DirHandle dir)
    {
        var roots = _roots;

        if (!roots.TryGetValue(dir.ScanRootId, out var root))
            return ReadOnlySpan<FileHandle>.Empty;

        if (!root.ChildFileSliceByDirIndex.TryGetValue(dir.Index, out var slice) || slice.IsEmpty)
            return ReadOnlySpan<FileHandle>.Empty;

        return root.ChildFilesPool.AsSpan(slice.Offset, slice.Length);
    }

    public DirAggregateStats GetDirStats(DirHandle dir)
    {
        var roots = _roots;

        return roots.TryGetValue(dir.ScanRootId, out var root) &&
               root.StatsByDirIndex.TryGetValue(dir.Index, out var s)
            ? s
            : new DirAggregateStats
            {
                DirCount = 0,
                FileCount = 0,
                TotalBytes = 0,
                DuplicateFiles = 0,
                DuplicateBytes = 0
            };
    }

    public bool TryGetSubtreeRange(DirHandle dir, out SubtreeRange range)
    {
        var roots = _roots;

        if (!roots.TryGetValue(dir.ScanRootId, out var root))
        {
            range = default;
            return false;
        }

        return root.SubtreeRangeByDirIndex.TryGetValue(dir.Index, out range);
    }

    public bool TryGetFileDirPreorder(FileHandle file, out int preorder)
    {
        var roots = _roots;

        if (!roots.TryGetValue(file.ScanRootId, out var root))
        {
            preorder = -1;
            return false;
        }

        var arr = root.DirPreorderByFileIndex;
        if ((uint)file.Index >= (uint)arr.Length)
        {
            preorder = -1;
            return false;
        }

        preorder = arr[file.Index];
        return preorder >= 0;
    }

    // ---------------------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------------------

    protected override ValueTask OnBootstrapEventAsync(BootstrapEvent evt, CancellationToken ct)
    {
        _snapshotView = evt.RepoSnapshotView;

        if (!TryLoadState(evt.Generation))
        {
            // Fallback: rebuild from snapshot and persist.
            RebuildFromSnapshot(evt.RepoSnapshotView);
            _lastIndexedGeneration = evt.Generation;
            SaveState();
        }
        else
        {
            _lastIndexedGeneration = evt.Generation;
        }

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnScanRootSnapshotReplacedEventAsync(
        ScanRootSnapshotReplacedEvent evt,
        CancellationToken ct)
    {
        if (evt.Generation <= _lastIndexedGeneration)
            return ValueTask.CompletedTask;

        _snapshotView = evt.RepoSnapshotView;

        s_log.Info("Rebuilding TreeIndex (generation = {0}).", evt.Generation);

        RebuildFromSnapshot(evt.RepoSnapshotView);

        _lastIndexedGeneration = evt.Generation;
        SaveState();

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnRepoScanRootRemovedEventAsync(
        RepoScanRootRemovedEvent evt,
        CancellationToken ct)
    {
        // Ignore stale/out-of-order events (channel may drop old items).
        if (evt.Generation <= _lastIndexedGeneration)
            return ValueTask.CompletedTask;

        var removedRootId = evt.ScanRootIdValue;

        var oldRoots = _roots;

        // remove the per-root entry.
        if (!oldRoots.ContainsKey(removedRootId))
        {
            _lastIndexedGeneration = evt.Generation;
            SaveState();
            return ValueTask.CompletedTask;
        }

        var newRoots = new Dictionary<ScanRootId, RootTreeIndexState>(Math.Max(0, oldRoots.Count - 1));
        foreach (var (k, v) in oldRoots)
            if (k != removedRootId)
                newRoots[k] = v;

        _roots = newRoots;

        _lastIndexedGeneration = evt.Generation;
        SaveState();

        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnRepoFileDeletedEventAsync(RepoFileDeletedEvent evt, CancellationToken ct)
    => ApplyRootMutation(evt.Generation, evt.File.ScanRootId, rootSnapshot =>
        {
            ApplyFileDeleteToRoot(rootSnapshot, evt.File);
        });

    protected override ValueTask OnRepoDirDeletedEventAsync(RepoDirDeletedEvent evt, CancellationToken ct)
    => ApplyRootMutation(evt.Generation, evt.Dir.ScanRootId, rootSnapshot =>
        {
            ApplyDirDeleteToRoot(
                rootSnapshot,
                evt.Dir,
                ExtractDeletedFileHandles(evt.DeletedFiles),
                evt.DeletedDirIds);
        });

    private ValueTask ApplyRootMutation(
        long generation,
        ScanRootId scanRootId,
        Action<ScanRootSnapshotView> mutation)
    {
        if (generation <= _lastIndexedGeneration)
            return ValueTask.CompletedTask;

        var snapshot = _snapshotView;
        if (snapshot is null || !snapshot.Snapshots.TryGetValue(scanRootId, out var rootSnapshot))
        {
            _lastIndexedGeneration = generation;
            SaveState();
            return ValueTask.CompletedTask;
        }

        mutation(rootSnapshot);

        _lastIndexedGeneration = generation;
        SaveState();

        return ValueTask.CompletedTask;
    }

    private static FileHandle[] ExtractDeletedFileHandles(
        (FileId FileId, FileHandle FileHandle)[] deletedFiles)
    {
        var result = new FileHandle[deletedFiles.Length];
        for (var i = 0; i < deletedFiles.Length; i++)
            result[i] = deletedFiles[i].FileHandle;
        return result;
    }
}
