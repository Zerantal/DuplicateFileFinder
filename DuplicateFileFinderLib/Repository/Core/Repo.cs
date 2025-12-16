// DuplicateFileFinderLib/Repo/Repo.cs

using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Core;

/// <summary>
///     The persistent database of all scanned files across all scan locations.
///     Uses a snapshot + append-only delta log for durability.
/// </summary>
public sealed partial class Repo : IRepo, IRepoInternal
{
    private const int RepoSchemaVersion = 6;
    
    private readonly string _repoPath;
    private readonly string _logDirPath;
    
    // live state
    private Dictionary<long, ScanRootSnapshotV2> _scanRootSnapshots = new(); 
    private Dictionary<long, DirRecord>  _dirs       = new();
    private Dictionary<long, FileRecord> _files      = new();
    private List<ScanRun> _scanRuns = new();
    private Dictionary<long, ScanRoot>   _scanRoots  = new();

    private readonly Dictionary<long, string> _dirPathCache = new();
    private readonly Dictionary<long, ScanRun> _scanRunIndex = new(); // scan run id -> scan run 
    
    private readonly Lock _sync = new();
    
    private RepoMetaFile _metaFile = null!;
    private bool _disposed;
    
    private Repo(string repoPath, RepoMetaFile metaFile)
    {
        _repoPath  = Path.GetFullPath(repoPath);
        _logDirPath = Path.Combine(_repoPath, "log");

        Directory.CreateDirectory(_repoPath);
        Directory.CreateDirectory(_logDirPath);

        LoadFromMetaFile(metaFile);
    }

    private void LoadFromMetaFile(RepoMetaFile metaFile)
    {
        _metaFile = metaFile;

        Meta = metaFile.Meta with
        {
            // ensure schema version is current
            SchemaVersion = RepoSchemaVersion
        };

        _scanRoots.Clear();
        foreach (var root in metaFile.ScanRoots)
            _scanRoots[root.RootId] = root;

        _scanRuns.Clear();
        _scanRuns.AddRange(metaFile.ScanRuns);

        _scanRunIndex.Clear();
        foreach (var run in _scanRuns)
            _scanRunIndex[run.ScanSequence] = run;
    }
    
    public void DeleteScanRoot(long scanRootId)
    {
        lock (_sync)
        {
            if (_scanRoots.TryGetValue(scanRootId, out var scanRoot))
            {
                scanRoot = scanRoot with { IsDeleted = true, DirId = 0, DeletedAtUtc = DateTimeOffset.UtcNow };
                _scanRoots[scanRootId] = scanRoot;
            }
            SaveMeta_NoLock();
        }
    }
}