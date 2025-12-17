using System.Collections.Immutable;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins;

public class TreeIndexPlugin : ChannelRepoPlugin, ITreeIndexReadModel
{
    private const string StateFileName = "tree-index.bin";
    
    private volatile ImmutableDictionary<DirHandle, ImmutableArray<DirHandle>> _childrenDirsByParentId
        = ImmutableDictionary<DirHandle, ImmutableArray<DirHandle>>.Empty;

    private volatile ImmutableDictionary<DirHandle, ImmutableArray<FileHandle>> _childrenFilesByDirId
        = ImmutableDictionary<DirHandle, ImmutableArray<FileHandle>>.Empty;

    private volatile ImmutableDictionary<DirHandle, DirAggregateStats> _dirStatsById
        = ImmutableDictionary<DirHandle, DirAggregateStats>.Empty;

    private readonly string _dataDirectory;

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
    
    public ImmutableArray<DirHandle> GetChildDirs(DirHandle dir)
    {
        var map = _childrenDirsByParentId;
        return map.TryGetValue(dir, out var v) ? v : ImmutableArray<DirHandle>.Empty;
    }

    public ImmutableArray<FileHandle> GetChildFiles(DirHandle dir)
    {
        var map = _childrenFilesByDirId;
        return map.TryGetValue(dir, out var v) ? v : ImmutableArray<FileHandle>.Empty;
    }

    public DirAggregateStats GetDirStats(DirHandle dir)
    {
        var map = _dirStatsById;
        return map.TryGetValue(dir, out var s)
            ? s : new DirAggregateStats {DirCount = 0, FileCount = 0, TotalBytes = 0};
    }


    // ---------------------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------------------

    protected override void OnBootstrapEvent(BootstrapEvent evt)
    {
        if (!TryLoadState(evt.Generation, evt.NextLogSequence - 1))
        {
            // Fallback: rebuild from snapshot and persist.
            RebuildFromSnapshot(evt.RepoSnapshotView);
            
            _lastIndexedGeneration = evt.Generation;
            _lastIndexedLogSequence = evt.NextLogSequence - 1;
            
            SaveState();
        }
        else
        {
            _lastIndexedGeneration = evt.Generation;
            _lastIndexedLogSequence = evt.NextLogSequence - 1;
        }
    }

    protected override void OnCompactedEvent(CompactedEvent evt)
    {
        // After compaction, it’s safer to rebuild from snapshot and persist a clean state.
        RebuildFromSnapshot(evt.RepoSnapshotView);
        
        _lastIndexedGeneration = evt.Generation;
        _lastIndexedLogSequence = evt.NextLogSequence - 1;
        
        SaveState();
    }

    // ---------------------------------------------------------------------
    // Core index maintenance
    // ---------------------------------------------------------------------

    private void RebuildFromSnapshot(RepoSnapshotView repoSnapshot)
    {
        var snapshotDict = repoSnapshot.Snapshots;
        var childrenDirsTmp  = new Dictionary<DirHandle, List<DirHandle>>();
        var childrenFilesTmp = new Dictionary<DirHandle, List<FileHandle>>();
        
        // Used for DFS start points (forest roots).
        var rootDirs = new List<DirHandle>();

        foreach (var snapshot in snapshotDict.Values)
        {
            var rootId = snapshot.ScanRootId;

            // Build id->handle maps for this root so we can resolve parent relationships quickly.
            // (No global dictionary; handles are only valid within their root snapshot anyway.)
            var dirIdToHandle = new Dictionary<long, DirHandle>(capacity: snapshot.Dirs.Count);
            
            for (int i = 0; i < snapshot.Dirs.Count; i++)
            {
                var dir = snapshot.Dirs[i];
                var h = new DirHandle(rootId, i);
                dirIdToHandle[dir.DirId] = h;

                if (dir.ParentDirId < 0)
                    rootDirs.Add(h);
            }

            // Child dirs
            for (int i = 0; i < snapshot.Dirs.Count; i++)
            {
                var dir = snapshot.Dirs[i];
                if (dir.ParentDirId < 0)    // root dir / orphaned dir
                    continue;
                
                if (!dirIdToHandle.TryGetValue(dir.ParentDirId, out var parentHandle))
                {
                    throw new InvalidOperationException(
                        $"Dir {dir.DirId} references missing parent {dir.ParentDirId} in scan root {rootId}.");
                }

                var childHandle = new DirHandle(rootId, i);

                if (!childrenDirsTmp.TryGetValue(parentHandle, out var list))
                {
                    list = [];
                    childrenDirsTmp[parentHandle] = list;
                }

                list.Add(childHandle);
            }

            // Child files
            for (int i = 0; i < snapshot.Files.Count; i++)
            {
                var file = snapshot.Files[i];

                if (!dirIdToHandle.TryGetValue(file.DirId, out var parentDirHandle))
                    continue;

                var fileHandle = new FileHandle(rootId, i);

                if (!childrenFilesTmp.TryGetValue(parentDirHandle, out var list))
                {
                    list = [];
                    childrenFilesTmp[parentDirHandle] = list;
                }

                list.Add(fileHandle);
            }
        }

        // Freeze children maps
        var childDirsBuilder = ImmutableDictionary.CreateBuilder<DirHandle, ImmutableArray<DirHandle>>();
        foreach (var (parent, list) in childrenDirsTmp)
            childDirsBuilder[parent] = [..list];

        var childFilesBuilder = ImmutableDictionary.CreateBuilder<DirHandle, ImmutableArray<FileHandle>>();
        foreach (var (parent, list) in childrenFilesTmp)
            childFilesBuilder[parent] = [..list];

        var frozenChildDirs  = childDirsBuilder.ToImmutable();
        var frozenChildFiles = childFilesBuilder.ToImmutable();

        _childrenDirsByParentId = frozenChildDirs;
        _childrenFilesByDirId   = frozenChildFiles;
        _dirStatsById           = ComputeDirStats(snapshotDict, frozenChildDirs, frozenChildFiles, rootDirs);
    }
        
    
    private static ImmutableDictionary<DirHandle, DirAggregateStats> ComputeDirStats(
        IReadOnlyDictionary<long, ScanRootSnapshotView> snapshotDict,
        ImmutableDictionary<DirHandle, ImmutableArray<DirHandle>> childrenDirsByParent,
        ImmutableDictionary<DirHandle, ImmutableArray<FileHandle>> childrenFilesByDir,
        IReadOnlyList<DirHandle> rootDirs)
    {
        // We want, for each dir:
        //  - TotalBytes = sum of sizes of all descendant files
        //  - FileCount  = number of descendant files
        //  - DirCount   = number of descendant dirs (excluding self)
        //
        // Approach: memoized DFS over the directory graph (forest).
        // Snapshot may be large; keep allocations modest.
    
    
        var memo = new Dictionary<DirHandle, DirAggregateStats>(capacity: Math.Max(1024, rootDirs.Count));

        DirAggregateStats Dfs(DirHandle dir)
        {
            if (memo.TryGetValue(dir, out var cached))
                return cached;
    
            long bytes = 0;
            int fileCount = 0;
            int dirCount = 0;
    
            // Files directly under this dir
            if (childrenFilesByDir.TryGetValue(dir, out var files))
            {
                for (int i = 0; i < files.Length; i++)
                {
                    var fh = files[i];

                    if (!snapshotDict.TryGetValue(fh.ScanRootId, out var snap))
                        continue;

                    var f = snap.Files[fh.Index];
                    if (f.Size > 0)
                    {
                        bytes += f.Size;
                        fileCount++;
                    }
                }
            }
    
            // Recurse into child dirs
            if (childrenDirsByParent.TryGetValue(dir, out var childDirs))
            {
                for (int i = 0; i < childDirs.Length; i++)
                {
                    var child = childDirs[i];

                    // Count the child itself
                    dirCount++;
    
                    var childStats = Dfs(child);
                    bytes += childStats.TotalBytes;
                    fileCount += childStats.FileCount;
                    dirCount += childStats.DirCount;
                }
            }
    
            var stats = new DirAggregateStats
            {
                TotalBytes = bytes,
                FileCount = fileCount,
                DirCount = dirCount
            };
    
            memo[dir] = stats;
            return stats;
        }
    
        // Compute stats for each forest root; DFS will memoize descendants.
        for (int i = 0; i < rootDirs.Count; i++)
            _ = Dfs(rootDirs[i]);

        return memo.ToImmutableDictionary();
    }


    // ---------------------------------------------------------------------
    // Persistence
    // ---------------------------------------------------------------------

    private string GetStateFilePath() => Path.Combine(_dataDirectory, StateFileName);

    private void SaveState()
    {
        var state = new TreeIndexState
        {
            LastIndexedGeneration = _lastIndexedGeneration,
            LastIndexedLogSequence = _lastIndexedLogSequence,
            ChildrenDirsByParentId = _childrenDirsByParentId,
            ChildrenFilesByDirId = _childrenFilesByDirId,
            DirStatsById = _dirStatsById
        };
        

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
            
            _lastIndexedGeneration = state.LastIndexedGeneration;
            _lastIndexedLogSequence = state.LastIndexedLogSequence;

            _childrenDirsByParentId  = state.ChildrenDirsByParentId;
            _childrenFilesByDirId    = state.ChildrenFilesByDirId;
            _dirStatsById            = state.DirStatsById;


            return true;
        }
        catch
        {
            return false;
        }
    }
}