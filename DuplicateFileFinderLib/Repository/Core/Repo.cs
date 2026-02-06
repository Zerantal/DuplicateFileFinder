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
    private const int RepoSchemaVersion = 6;

    private readonly string _repoPath;

    // live state
    private RepoMeta _meta = null!;

    // IMPORTANT: These collections are treated as immutable snapshots (copy-on-write containers).
    // - Never mutate a dictionary/list instance in-place.
    // - Any update must replace the entire reference with a copied instance.
    // This ensures previously published RepoSnapshotView instances remain safe to enumerate.
    private Dictionary<ScanRootId, ScanRootSnapshotV2> _scanRootSnapshots = new();
    private Dictionary<ScanRootId, ScanRoot> _scanRoots = new();
    private Dictionary<long, ScanRun> _scanRunIndex = new(); // scan run id -> scan run

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
