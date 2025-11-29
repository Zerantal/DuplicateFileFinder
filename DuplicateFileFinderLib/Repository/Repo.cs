// DuplicateFileFinderLib/Repo/Repo.cs

using System.Collections.Concurrent;
using DuplicateFileFinderLib.Repository.Models;
using NLog;

namespace DuplicateFileFinderLib.Repository;

/// <summary>
///     The persistent database of all scanned files across all scan locations.
///     Uses a snapshot + append-only delta log for durability.
/// </summary>
public sealed partial class Repo : IRepo, IDisposable, IAsyncDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    
    private bool _disposed;
    
    // persistence data models
    private RepoMeta _meta = null!;
    private Dictionary<Guid, DirRecord> _dirs = new();
    private Dictionary<Guid, FileRecord> _files = new();
    private Dictionary<HashKey, List<Guid>> _hashIndex = new();
    private List<ScanRun> _scanRuns = new();
    
    // RootId -> ScanRoot
    private Dictionary<Guid, ScanRoot> _scanRoots = new();
    
    
    // DirId -> full path
    private readonly ConcurrentDictionary<Guid, string> _dirPathCache = new();
    // scan sequence number -> scan run
    private readonly Dictionary<long, ScanRun> _scanRunIndex = new();

    // to sync snapshot+meta mutations.
    private readonly Lock _sync = new();
}