using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins;

public class TreeIndexPlugin : ChannelRepoPlugin, ITreeIndexReadModel
{
    private const string StateFileName = "tree-index.bin";

    private readonly Dictionary<long, List<long>> _childrenDirsByParentId = new();
    private readonly Dictionary<long, List<long>> _childrenFilesByDirId = new();

    private readonly string _dataDirectory;
    private readonly Lock _lock = new();

    private long _lastIndexedGeneration;
    private long _lastIndexedLogSequence;

    public TreeIndexPlugin(string dataDirectory)
        : base(4096)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory must be non-empty.", nameof(dataDirectory));

        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(_dataDirectory);
    }


    // ---------------------------------------------------------------------
    // Public query surface
    // ---------------------------------------------------------------------
    public IReadOnlyList<long> GetChildFileIds(long dirId)
    {
        _childrenFilesByDirId.TryGetValue(dirId, out var files);

        return files ?? [];
    }

    public IReadOnlyList<long> GetChildDirIds(long dirId)
    {
        _childrenDirsByParentId.TryGetValue(dirId, out var dirs);

        return dirs ?? [];
    }


    // ---------------------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------------------

    protected override void OnBootstrapEvent(BootstrapEvent evt)
    {
        if (!TryLoadState(evt.Generation, evt.NextLogSequence - 1))
        {
            // Fallback: rebuild from snapshot and persist.
            RebuildFromSnapshot(evt.Snapshot);
            lock (_lock)
            {
                _lastIndexedGeneration = evt.Generation;
                _lastIndexedLogSequence = evt.NextLogSequence - 1;
            }

            SaveState();
        }
        else
        {
            lock (_lock)
            {
                _lastIndexedGeneration = evt.Generation;
                _lastIndexedLogSequence = evt.NextLogSequence - 1;
            }
        }
    }

    protected override void OnCompactedEvent(CompactedEvent evt)
    {
        // After compaction, it’s safer to rebuild from snapshot and persist a clean state.
        RebuildFromSnapshot(evt.Snapshot);
        lock (_lock)
        {
            _lastIndexedGeneration = evt.Generation;
            _lastIndexedLogSequence = evt.NextLogSequence - 1;
        }

        SaveState();
    }

    // ---------------------------------------------------------------------
    // Core index maintenance
    // ---------------------------------------------------------------------

    private void RebuildFromSnapshot(IRepoView snapshot)
    {
        var newChildrenDirsByParentId = new Dictionary<long, List<long>>();
        var newChildrenFilesByDirId = new Dictionary<long, List<long>>();

        foreach (var (dirId, dirRecord) in snapshot.Dirs)
        {
            if (!dirRecord.ParentDirId.HasValue)
                continue;

            var parentDirId = dirRecord.ParentDirId.Value;
            if (!newChildrenDirsByParentId.TryGetValue(parentDirId, out var subdirs))
            {
                subdirs = new List<long>();
                newChildrenDirsByParentId[parentDirId] = subdirs;
            }

            subdirs.Add(dirId);
        }

        foreach (var (fileId, fileRecord) in snapshot.Files)
        {
            if (!newChildrenFilesByDirId.TryGetValue(fileRecord.DirId, out var fileList))
            {
                fileList = new List<long>();
                newChildrenFilesByDirId[fileRecord.DirId] = fileList;
            }

            fileList.Add(fileId);
        }

        lock (_lock)
        {
            _childrenDirsByParentId.Clear();
            _childrenFilesByDirId.Clear();

            foreach (var (parentDirId, subdirs) in newChildrenDirsByParentId)
                _childrenDirsByParentId[parentDirId] = subdirs;

            foreach (var (dirId, files) in newChildrenFilesByDirId)
                _childrenFilesByDirId[dirId] = files;
        }
    }

    // ---------------------------------------------------------------------
    // Persistence
    // ---------------------------------------------------------------------

    private string GetStateFilePath()
    {
        return Path.Combine(_dataDirectory, StateFileName);
    }

    private void SaveState()
    {
        TreeIndexState state;

        lock (_lock)
        {
            var childrenDirsByParentCopy = new Dictionary<long, List<long>>(_childrenDirsByParentId.Count);
            var childrenFilesByDirCopy = new Dictionary<long, List<long>>(_childrenFilesByDirId.Count);

            foreach (var (dirId, subdirs) in _childrenDirsByParentId)
                childrenDirsByParentCopy[dirId] = [..subdirs];

            foreach (var (dirId, files) in _childrenFilesByDirId)
                childrenFilesByDirCopy[dirId] = [..files];

            state = new TreeIndexState
            {
                LastIndexedGeneration = _lastIndexedGeneration,
                LastIndexedLogSequence = _lastIndexedLogSequence,
                ChildrenDirsByParentId = childrenDirsByParentCopy,
                ChildrenFilesByDirId = childrenFilesByDirCopy
            };
        }

        var path = GetStateFilePath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var bytes = MemoryPackSerializer.Serialize(state);
        File.WriteAllBytes(path, bytes);
    }

    private bool TryLoadState(long expectedGeneration, long expectedLastLogSequence)
    {
        var path = GetStateFilePath();
        if (!File.Exists(path))
            return false;

        try
        {
            var bytes = File.ReadAllBytes(path);
            var state = MemoryPackSerializer.Deserialize<TreeIndexState>(bytes);
            if (state is null)
                return false;

            // Only use the state if it matches the current repo position.
            if (state.LastIndexedGeneration != expectedGeneration ||
                state.LastIndexedLogSequence != expectedLastLogSequence)
                return false;

            lock (_lock)
            {
                _childrenDirsByParentId.Clear();
                _childrenFilesByDirId.Clear();

                foreach (var (dirId, subdirs) in state.ChildrenDirsByParentId)
                    _childrenDirsByParentId[dirId] = subdirs;
                foreach (var (dirId, files) in state.ChildrenFilesByDirId)
                    _childrenFilesByDirId[dirId] = files;

                _lastIndexedGeneration = state.LastIndexedGeneration;
                _lastIndexedLogSequence = state.LastIndexedLogSequence;
            }

            return true;
        }
        catch
        {
            // Corrupt or incompatible state; ignore and rebuild from snapshot.
            return false;
        }
    }
}