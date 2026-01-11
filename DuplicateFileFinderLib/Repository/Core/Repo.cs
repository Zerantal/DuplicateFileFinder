// DuplicateFileFinderLib/Repo/Repo.cs

using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Core;

/// <summary>
///     The persistent database of all scanned files across all scan locations.
///     Uses a snapshot + append-only delta log for durability.
/// </summary>
public sealed partial class Repo : IRepoInternal
{
    // NOTE ON THREAD-SAFETY:
    // - All access to _scanRootSnapshots/_scanRoots/_scanRuns is guarded by _sync.
    // - Snapshots stored in _scanRootSnapshots MUST be treated as immutable after insertion.
    //   (Dirs/Files arrays and StringPool must never be mutated.) Consumers may safely hold
    //   references returned by TryGetScanRootView/GetRepoSnapshotView.

    private const int RepoSchemaVersion = 6;

    private readonly string _repoPath;

    // live state
    private RepoMeta _meta = null!;
    private readonly Dictionary<long, ScanRootSnapshotV2> _scanRootSnapshots = new();
    private List<ScanRun> _scanRuns = new();
    private Dictionary<long, ScanRoot> _scanRoots = new();

    private readonly Dictionary<long, ScanRun> _scanRunIndex = new(); // scan run id -> scan run

    private readonly Lock _sync = new();

    private RepoMetaFile _metaFile = null!;
    private bool _disposed;

    private Repo(string repoPath, RepoMetaFile metaFile)
    {
        _repoPath = Path.GetFullPath(repoPath);

        Directory.CreateDirectory(_repoPath);

        LoadFromMetaFile(metaFile);
    }
}
