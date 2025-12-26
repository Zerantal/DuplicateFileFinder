using System.Collections.Immutable;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins;

public sealed class FileDirIndexPlugin : ChannelRepoPlugin, IFileDirReadModel
{
    private volatile ImmutableDictionary<long, FileHandle> _filesById = ImmutableDictionary<long, FileHandle>.Empty;
    private volatile ImmutableDictionary<long, DirHandle> _dirsById = ImmutableDictionary<long, DirHandle>.Empty;
    
    // cached snapshot of all file/dir records
    private volatile RepoSnapshotView? _snapshotView;
    
    // Persisted position (only mutated on bootstrap/worker thread)
    private long _lastIndexedGeneration;
    
    private readonly string _dataDirectory;
    private const string StateFileName = "file-dir-index.bin";

    public FileDirIndexPlugin(string dataDirectory) : base(4096)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory must be non-empty.", nameof(dataDirectory));

        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(_dataDirectory);
    }
    
    // ---------------------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------------------

    protected override void OnBootstrapEvent(BootstrapEvent evt)
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
    }

    protected override void OnScanRootSnapshotCommittedEvent(ScanRootSnapshotCommittedEvent evt)
    {
        // Ignore stale/out-of-order events (channel may drop old items).
        if (evt.Generation <= _lastIndexedGeneration)
            return;

        _snapshotView = evt.RepoSnapshotView;

        using (TimingLog.StartPhase($"Rebuilding FileDirIndexPlugin (ScanRoot {evt.ScanRootId}, Gen {evt.Generation})"))
        {
            RebuildFromSnapshot(evt.RepoSnapshotView);
        }

        _lastIndexedGeneration = evt.Generation;
        SaveState();
    }
    
    // ---------------------------------------------------------------------
    // Core index maintenance (build -> publish)
    // ---------------------------------------------------------------------

    private void RebuildFromSnapshot(RepoSnapshotView repoSnapshot)
    {
        var snapshotDict = repoSnapshot.Snapshots;
        // Build mutable dictionaries first
        var dirIndexbuilder = ImmutableDictionary.CreateBuilder<long, DirHandle>();
        var fileIndexbuilder = ImmutableDictionary.CreateBuilder<long, FileHandle>();

        foreach (var (rootId, snapshot) in snapshotDict)
        {
            for (int i = 0; i < snapshot.Dirs.Count; i++)
            {
                var dir = snapshot.Dirs[i];
                if (!dirIndexbuilder.TryAdd(dir.DirId, new DirHandle(rootId, i)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate dirId {dir.DirId} encountered while rebuilding FileDirIndexPlugin.");
                }
            }

            for (int i = 0; i < snapshot.Files.Count; i++)
            {
                var file = snapshot.Files[i];
                if (!fileIndexbuilder.TryAdd(file.FileId, new FileHandle(rootId, i)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate FileId {file.FileId} encountered while rebuilding FileDirIndexPlugin.");
                }
            }
        }
        
        // public index
        _dirsById = dirIndexbuilder.ToImmutable();
        _filesById = fileIndexbuilder.ToImmutable();

        // Publish snapshot view last so readers see coherent state.
        _snapshotView = repoSnapshot;
    }

// ---------------------------------------------------------------------
// Persistence
// ---------------------------------------------------------------------

    private string GetStateFilePath() => Path.Combine(_dataDirectory, StateFileName);

    private void SaveState()
    {
        var state = new FileDirIndexState
        {
            LastIndexedGeneration = _lastIndexedGeneration,
            DirsById = _dirsById.ToDictionary(x => x.Key, x => x.Value),
            FilesById = _filesById.ToDictionary(x => x.Key, x => x.Value)
        };
        
        var path = GetStateFilePath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(path, MemoryPackSerializer.Serialize(state));
    }

    private bool TryLoadState(long expectedGeneration)
    {
        var path = GetStateFilePath();
        if (!File.Exists(path))
            return false;

        try
        {
            var state = MemoryPackSerializer.Deserialize<FileDirIndexState>(File.ReadAllBytes(path));
            if (state is null)
                return false;

            // Only use the state if it matches the current repo position.
            if (state.LastIndexedGeneration != expectedGeneration)
                return false;

            // Rehydrate into immutable indexes + publish
            using (TimingLog.StartPhase("Rehydrating into immutable indexes"))
            {
                _dirsById = state.DirsById.ToImmutableDictionary();
                _filesById = state.FilesById.ToImmutableDictionary();
            }

            _lastIndexedGeneration = state.LastIndexedGeneration;
            return true;
        }
        catch
        {
            // Corrupt or incompatible state; ignore and rebuild from snapshot.
            return false;
        }
    }    
    
    // ---------------------------------------------------------------------
    // Public query surface (lock-free)
    // ---------------------------------------------------------------------
    public bool TryGetDir(long dirId, out DirHandle handle) => _dirsById.TryGetValue(dirId, out handle);
    public bool TryGetFile(long fileId, out FileHandle handle) => _filesById.TryGetValue(fileId, out handle);
    public int FileCount => _filesById.Count;
    public int DirCount => _dirsById.Count;

    public bool TryGetFilePathById(long fileId, out string relativePath)
    {
        relativePath = string.Empty;
        return TryGetFile(fileId, out var handle) && TryGetFilePathByHandle(handle, out relativePath);
    }

    public bool TryGetFilePathByHandle(FileHandle fileHandle, out string relativePath)
    {
        relativePath = string.Empty;

        var view = _snapshotView;
        if (view is null)
            return false;

        if (!view.Snapshots.TryGetValue(fileHandle.ScanRootId, out var snapshot))
            return false;

        if ((uint)fileHandle.Index >= (uint)snapshot.Files.Count)
            return false;

        var file = snapshot.Files[fileHandle.Index];

        // Collect segments bottom-up: [fileName, dirName, dirName, ...]
        var segments = new List<string>(capacity: 8) { view.DecodeFileName(fileHandle) };

        // Walk parent dirs until "root"
        long dirId = file.DirId;
        while (dirId > 0)
        {
            if (!_dirsById.TryGetValue(dirId, out var dh))
                return false; // inconsistent index/snapshot

            if (dh.ScanRootId != fileHandle.ScanRootId)
                return false; // should not happen

            if ((uint)dh.Index >= (uint)snapshot.Dirs.Count)
                return false;

            var dir = snapshot.Dirs[dh.Index];
            segments.Add(view.DecodeDirName(dh));
            dirId = dir.ParentDirId;
        }

        // Build relative path in correct order
        segments.Reverse();

        // Avoid Path.Combine in a loop; but fine as a sketch:
        relativePath = segments.Count == 0 ? string.Empty : segments[0];
        for (int i = 1; i < segments.Count; i++)
            relativePath = Path.Combine(relativePath, segments[i]);

        return true;
    }


    public bool TryGetDirPathById(long dirId, out string relativePath)
    {
        relativePath = string.Empty;
        return TryGetDir(dirId, out var handle) && TryGetDirPathByHandle(handle, out relativePath);
    }

    public bool TryGetDirPathByHandle(DirHandle dirHandle, out string relativePath)
    {
        relativePath = string.Empty;

        var view = _snapshotView;
        if (view is null)
            return false;

        if (!view.Snapshots.TryGetValue(dirHandle.ScanRootId, out var snapshot))
            return false;

        if ((uint)dirHandle.Index >= (uint)snapshot.Dirs.Count)
            return false;

        var segments = new List<string>(capacity: 8);
        
        var dir = snapshot.Dirs[dirHandle.Index];
        segments.Add(view.DecodeDirName(dirHandle));

        long parentId = dir.ParentDirId;
        while (parentId > 0)
        {
            if (!_dirsById.TryGetValue(parentId, out var parentHandle))
                return false;

            if (parentHandle.ScanRootId != dirHandle.ScanRootId)
                return false;

            if ((uint)parentHandle.Index >= (uint)snapshot.Dirs.Count)
                return false;

            var parent = snapshot.Dirs[parentHandle.Index];
            segments.Add(view.DecodeDirName(parentHandle));
            parentId = parent.ParentDirId;
        }

        segments.Reverse();

        relativePath = segments.Count == 0 ? string.Empty : segments[0];
        for (int i = 1; i < segments.Count; i++)
            relativePath = Path.Combine(relativePath, segments[i]);

        return true;
    }
}