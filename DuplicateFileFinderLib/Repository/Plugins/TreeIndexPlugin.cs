using System.Collections.Immutable;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins;

public class TreeIndexPlugin : ChannelRepoPlugin, ITreeIndexReadModel
{
    private const string StateFileName = "tree-index.bin";
    
    private volatile ImmutableDictionary<long, ImmutableArray<long>> _childrenDirsByParentId
        = ImmutableDictionary<long, ImmutableArray<long>>.Empty;

    private volatile ImmutableDictionary<long, ImmutableArray<long>> _childrenFilesByDirId
        = ImmutableDictionary<long, ImmutableArray<long>>.Empty;

    private volatile ImmutableDictionary<long, DirAggregateStats> _dirStatsById
        = ImmutableDictionary<long, DirAggregateStats>.Empty;

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
    
    public ImmutableArray<long> GetChildDirIds(long dirId)
    {
        var map = _childrenDirsByParentId;
        return map.TryGetValue(dirId, out var v) ? v : ImmutableArray<long>.Empty;
    }

    public ImmutableArray<long> GetChildFileIds(long dirId)
    {
        var map = _childrenFilesByDirId;
        return map.TryGetValue(dirId, out var v) ? v : ImmutableArray<long>.Empty;
    }

    public DirAggregateStats GetDirStats(long dirId)
    {
        var map = _dirStatsById;
        return map.TryGetValue(dirId, out var s)
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
            RebuildFromSnapshot(evt.Snapshot);
            
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
        RebuildFromSnapshot(evt.Snapshot);
        
        _lastIndexedGeneration = evt.Generation;
        _lastIndexedLogSequence = evt.NextLogSequence - 1;
        
        SaveState();
    }

    // ---------------------------------------------------------------------
    // Core index maintenance
    // ---------------------------------------------------------------------

    private void RebuildFromSnapshot(IRepoView snapshot)
    {
        
        var dirBuilder  = ImmutableDictionary.CreateBuilder<long, ImmutableArray<long>>();
        var fileBuilder = ImmutableDictionary.CreateBuilder<long, ImmutableArray<long>>();
        
        var tmpDirs  = new Dictionary<long, List<long>>();
        var tmpFiles = new Dictionary<long, List<long>>();

        // Build child dir map
        foreach (var (dirId, dirRecord) in snapshot.Dirs)
        {
            if (!dirRecord.ParentDirId.HasValue)
                continue;
    
            var parentDirId = dirRecord.ParentDirId.Value;
            if (!tmpDirs.TryGetValue(parentDirId, out var subdirs))
            {
                subdirs = new List<long>();
                tmpDirs[parentDirId] = subdirs;
            }
    
            subdirs.Add(dirId);
        }
    
        // Build child file map
        foreach (var (fileId, fileRecord) in snapshot.Files)
        {
            if (!tmpFiles.TryGetValue(fileRecord.DirId, out var fileList))
            {
                fileList = new List<long>();
                tmpFiles[fileRecord.DirId] = fileList;
            }
    
            fileList.Add(fileId);
        }
        
        foreach (var (k, v) in tmpDirs)
            dirBuilder[k] = [..v];

        foreach (var (k, v) in tmpFiles)
            fileBuilder[k] = [..v];
        
        _childrenDirsByParentId  = dirBuilder.ToImmutable();
        _childrenFilesByDirId = fileBuilder.ToImmutable();
        _dirStatsById = ComputeDirStats(snapshot, tmpDirs, tmpFiles);
        
        
    }
    
    private static ImmutableDictionary<long, DirAggregateStats> ComputeDirStats(
        IRepoView snapshot,
        Dictionary<long, List<long>> childrenDirsByParentId,
        Dictionary<long, List<long>> childrenFilesByDirId)
    {
        // We want, for each dir:
        //  - TotalBytes = sum of sizes of all descendant files
        //  - FileCount  = number of descendant files
        //  - DirCount   = number of descendant dirs (excluding self)
        //
        // Approach: memoized DFS over the directory graph (forest).
        // Snapshot may be large; keep allocations modest.
    
        var memo = ImmutableDictionary.CreateBuilder<long, DirAggregateStats>();
    
        DirAggregateStats Dfs(long dirId)
        {
            if (memo.TryGetValue(dirId, out var cached))
                return cached;
    
            long bytes = 0;
            int fileCount = 0;
            int dirCount = 0;
    
            if (childrenFilesByDirId.TryGetValue(dirId, out var fileIds))
            {
                foreach (var fileId in fileIds)
                {
                    if (!snapshot.Files.TryGetValue(fileId, out var f))
                        continue;
                    if (f.Size > 0)
                    {
                        bytes += f.Size;
                        fileCount++;
                    }
                }
            }
    
            if (childrenDirsByParentId.TryGetValue(dirId, out var childDirIds))
            {
                foreach (var childId in childDirIds)
                {
                    // Count the child dir itself
                    dirCount++;
    
                    var childStats = Dfs(childId);
                    bytes += childStats.TotalBytes;
                    fileCount += childStats.FileCount;
                    dirCount += childStats.DirCount; // child's descendants
                }
            }
    
            var stats = new DirAggregateStats
            {
                TotalBytes = bytes,
                FileCount = fileCount,
                DirCount = dirCount
            };
    
            memo[dirId] = stats;
            return stats;
        }
    
        // Compute stats for every directory id in snapshot
        foreach (var (dirId, dir) in snapshot.Dirs)
        {
            if (!dir.ParentDirId.HasValue)
                _ = Dfs(dirId);
        }

        return memo.ToImmutable();
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