using System.Collections.Immutable;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins;

public sealed class FileDirIndex : ChannelRepoPlugin, IFileDirReadModel
{
    private volatile ImmutableDictionary<long, FileHandle> _filesById = ImmutableDictionary<long, FileHandle>.Empty;
    private volatile ImmutableDictionary<long, DirHandle> _dirsById = ImmutableDictionary<long, DirHandle>.Empty;
    
    // Persisted position (only mutated on bootstrap/compaction thread)
    private long _lastIndexedGeneration;
    private long _lastIndexedLogSequence;
    
    private readonly string _dataDirectory;
    private const string StateFileName = "file-dir-index.bin";

    public FileDirIndex(string dataDirectory) : base(4096)
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
        var expectedLastLogSequence = evt.NextLogSequence - 1;

        if (!TryLoadState(evt.Generation, expectedLastLogSequence))
        {
            // Fallback: rebuild from snapshot and persist.
            RebuildFromSnapshot(evt.RepoSnapshotView);
            _lastIndexedGeneration = evt.Generation;
            _lastIndexedLogSequence = expectedLastLogSequence;
            SaveState();
        }
        else
        {
            // Loaded state is already consistent with generation/log seq.
            _lastIndexedGeneration = evt.Generation;
            _lastIndexedLogSequence = expectedLastLogSequence;
        }
    }

    protected override void OnCompactedEvent(CompactedEvent evt)
    {
        var expectedLastLogSequence = evt.NextLogSequence - 1;

        // After compaction, it’s safer to rebuild from snapshot and persist a clean state.
        RebuildFromSnapshot(evt.RepoSnapshotView);
        _lastIndexedGeneration = evt.Generation;
        _lastIndexedLogSequence = expectedLastLogSequence;
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
                        $"Duplicate dirId {dir.DirId} encountered while rebuilding FileDirIndex.");
                }
            }
            for (int i = 0; i < snapshot.Files.Count; i++)
            {
                var file = snapshot.Files[i];
                if (!fileIndexbuilder.TryAdd(file.FileId, new FileHandle(rootId, i)))
                {
                    throw new InvalidOperationException(
                        $"Duplicate FileId {file.FileId} encountered while rebuilding FileDirIndex.");
                }
            }
        }
        
        // public index
        _dirsById = dirIndexbuilder.ToImmutable();
        _filesById = fileIndexbuilder.ToImmutable();
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
            LastIndexedLogSequence = _lastIndexedLogSequence,
            DirsById = _dirsById.ToDictionary(x => x.Key, x => x.Value),
            FilesById = _filesById.ToDictionary(x => x.Key, x => x.Value)
        };
        
        var path = GetStateFilePath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(path, MemoryPackSerializer.Serialize(state));
    }

    private bool TryLoadState(long expectedGeneration, long expectedLastLogSequence)
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
            if (state.LastIndexedGeneration != expectedGeneration ||
                state.LastIndexedLogSequence != expectedLastLogSequence)
                return false;

            // Rehydrate into immutable indexes + publish
            _dirsById =  state.DirsById.ToImmutableDictionary(x => x.Key, x => x.Value);
            _filesById =  state.FilesById.ToImmutableDictionary(x => x.Key, x => x.Value);

            _lastIndexedGeneration = state.LastIndexedGeneration;
            _lastIndexedLogSequence = state.LastIndexedLogSequence;

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
}