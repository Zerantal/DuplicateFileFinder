using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Repository;

public sealed partial class Repo
{
    private const int RepoSchemaVersion = 5;

    /// <summary>
    /// Ensures the repo is migrated up to RepoSchemaVersion.
    /// Applies stepwise migrations and persists changes (snapshot + meta + scanroots/scanruns)
    /// if any migration actually ran.
    /// </summary>
    private void MigrateToLatest()
    {
        bool migrated = false;

        lock (_sync)
        {
            while (_meta.SchemaVersion < RepoSchemaVersion)
            {
                switch (_meta.SchemaVersion)
                {
                    case 4:
                        // 4 -> 5 introduces ScanRoot and ScanRun.ScanRootId
                        MigrateFrom4To5_ScanRoots();
                        _meta = _meta with { SchemaVersion = 5 };
                        migrated = true;
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unknown repo schema version: {_meta.SchemaVersion}. " +
                            $"Cannot migrate to {RepoSchemaVersion}.");
                }
            }
            
            // If nothing changed, ensure meta schema is at least RepoSchemaVersion and leave.
            if (!migrated)
            {
                if (_meta.SchemaVersion != RepoSchemaVersion)
                {
                    _meta = _meta with { SchemaVersion = RepoSchemaVersion };
                    SaveMeta_NoLock();
                }
                        
                return;
            }

            // After migration(s), write a fresh snapshot + meta + scanroots/scanruns.
            // SaveSnapshot_NoLock will include the migrated _meta (with new SchemaVersion).
            SaveSnapshot_NoLock();
            SaveScanRoots_NoLock();
            SaveScanRuns_NoLock();
        }
    }

    /// <summary>
    /// Schema 4 -> 5: introduce ScanRoot and ScanRun.ScanRootId, built from existing runs.
    /// </summary>
    private void MigrateFrom4To5_ScanRoots()
    {
        // Migration:
        // 1) Build ScanRoots from distinct RootPath values in ScanRuns.
        // 2) Assign ScanRun.ScanRootId accordingly.
        // 3) Bump SchemaVersion and persist.
        

        // We assume: _files, _dirs from snapshot+log; _scanRuns from LoadScanRuns.
        // _scanRoots will be rebuilt from scratch.
        _scanRoots = new Dictionary<Guid, ScanRoot>();

        // Map canonical root path -> ScanRoot
        var byRootPath = new Dictionary<string, ScanRoot>(StringComparer.Ordinal);

        for (int i = 0; i < _scanRuns.Count; i++)
        {
            var run = _scanRuns[i];

            if (string.IsNullOrWhiteSpace(run.RootPath))
                continue;

            var canonical = PathUtils.NormalizePath(run.RootPath);

            if (!byRootPath.TryGetValue(canonical, out var root))
            {
                var dirId = TryGetDirIdForFullPath_NoLock(canonical) ?? Guid.Empty;

                root = new ScanRoot
                {
                    Id       = Guid.NewGuid(),
                    RootPath = canonical,
                    DirId    = dirId,
                    CreatedAt = run.StartedAt,
                    LastScannedAt = run.FinishedAt ?? run.StartedAt
                };

                byRootPath[canonical] = root;
                _scanRoots[root.Id]   = root;
            }

            // Patch ScanRun with ScanRootId
            _scanRuns[i] = run with { ScanRootId = root.Id };
        }
    }
    
    /// <summary>
    /// Try to get the DirRecord.Id for a full canonical path.
    /// Best effort; returns null if no exact match is found.
    /// Caller must hold _sync if there is any chance of concurrent mutation.
    /// During migration, we call it under _sync.
    /// </summary>
    private Guid? TryGetDirIdForFullPath_NoLock(string canonicalFullPath)
    {
        foreach (var kv in _dirs)
        {
            var full = GetFullDirPath(kv.Key);
            if (PathUtils.IsSamePath(full, canonicalFullPath))
                return kv.Key;
        }

        return null;
    }
}
