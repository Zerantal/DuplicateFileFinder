// DuplicateFileFinderLib/Repository/Repo.IntegrityRepair.cs

using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Repository;

public sealed partial class Repo
{
    /// <summary>
    /// One-off repair for migrated / messy repos.
    /// It:
    /// - Promotes dirs whose ParentId is missing to roots.
    /// - Recreates missing ScanRoots for ScanRuns.
    /// - Binds ScanRoot.DirId (creating dummy root dirs as needed).
    /// - Removes ScanRuns that are not referenced by any Dir/File record.
    /// - Removes ScanRoots with no remaining runs.
    /// - Deduplicates ScanRoots per RootPath (keeping the best candidate).
    /// - Deletes orphan per-root snapshot files (no matching ScanRoot).
    /// </summary>
    public void RepairMigratedRepo()
    {
        string rootsDirPath;
        lock (_sync)
        {
            rootsDirPath = Path.Combine(_repoPath, "roots");
        }

        Directory.CreateDirectory(rootsDirPath);

        lock (_sync)
        {
            bool changedDirs      = false;
            bool changedScanRoots = false;
            bool changedScanRuns  = false;

            // ------------------------------------
            // 1. Fix DIR_PARENT_MISSING
            // ------------------------------------
            var knownDirIds = new HashSet<long>(_dirs.Keys);
            var newDirs     = new Dictionary<long, DirRecord>(_dirs);

            foreach (var (id, dir) in _dirs)
            {
                if (dir.ParentId is { } pid && !knownDirIds.Contains(pid))
                {
                    // Parent is missing: promote this directory to a root.
                    var fixedDir = dir with { ParentId = null };
                    newDirs[id]  = fixedDir;
                    changedDirs  = true;
                }
            }

            if (changedDirs)
                _dirs = newDirs;

            // ------------------------------------
            // 2. Fix RUN_ROOT_MISSING (recreate ScanRoots)
            // ------------------------------------
            var rootsById = _scanRoots.ToDictionary(kv => kv.Key, kv => kv.Value);

            for (int i = 0; i < _scanRuns.Count; i++)
            {
                var run = _scanRuns[i];

                if (run.ScanRootId == 0)
                    continue;

                if (rootsById.ContainsKey(run.ScanRootId))
                    continue;

                if (string.IsNullOrWhiteSpace(run.RootPath))
                    continue; // cannot recover without a path

                var canonical = PathUtils.NormalizePath(run.RootPath);
                var dirId = TryGetDirIdForFullPath_NoLock(canonical) ?? 0;

                var root = new ScanRoot
                {
                    RootId        = run.ScanRootId,
                    DirId         = dirId,
                    RootPath      = canonical,
                    CreatedAt     = run.StartedAt,
                    LastScannedAt = run.FinishedAt ?? run.StartedAt,

                    VolumeId      = null,
                    VolumeLabel   = null,
                    IsRotational  = null,
                    FileSystemType = null,
                    DevicePath    = null,
                    DeviceModel   = null,
                    DisplayName   = null
                };

                rootsById[root.RootId] = root;
                changedScanRoots   = true;
            }

            _scanRoots = rootsById;

            // ------------------------------------
            // 3. Bind ROOT_DIRID_EMPTY (ScanRoot.DirId)
            // ------------------------------------
            foreach (var (id, root) in _scanRoots.ToArray())
            {
                if (root.DirId != 0)
                    continue;

                if (string.IsNullOrWhiteSpace(root.RootPath))
                    continue;

                var canonical = PathUtils.NormalizePath(root.RootPath);

                // 3a. Try full-path match using existing helper
                var dirId = TryGetDirIdForFullPath_NoLock(canonical);

                // 3b. If that fails, try leaf-name match ANYWHERE in the tree
                if (dirId is null)
                {
                    var trimmed  = canonical.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                    var leafName = Path.GetFileName(trimmed);
                    if (string.IsNullOrEmpty(leafName))
                        leafName = canonical;

                    var candidates = _dirs.Values
                        .Where(d => string.Equals(d.Name, leafName, StringComparison.Ordinal))
                        .Select(d => d.DirId)
                        .ToList();

                    if (candidates.Count == 1)
                        dirId = candidates[0];
                }

                // 3c. If still nothing, create a dummy root dir
                if (dirId is null)
                {
                    var trimmed  = canonical.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                    var leafName = Path.GetFileName(trimmed);
                    if (string.IsNullOrEmpty(leafName))
                        leafName = canonical;

                    var newDirId = AllocateDirId_NoLock();
                    
                    var newRootDir = new DirRecord
                    {
                        DirId            = newDirId,
                        ParentId         = null,
                        Name             = leafName,
                        LastSeenSequence = 0,
                        Status           = ScanEntryStatus.None,
                        ErrorMessage     = null
                    };

                    _dirs[newDirId] = newRootDir;
                    changedDirs      = true;
                    dirId            = newDirId;
                }

                if (dirId is { } boundId && boundId != 0)
                {
                    var updated = root with { DirId = boundId };
                    _scanRoots[id] = updated;
                    changedScanRoots = true;
                }
            }

            // ------------------------------------
            // 4. Remove orphan ScanRuns (no dir/file references)
            // ------------------------------------
            var usedSequences = new HashSet<long>();
            foreach (var d in _dirs.Values)
            {
                if (d.LastSeenSequence > 0)
                    usedSequences.Add(d.LastSeenSequence);
            }

            foreach (var f in _files.Values)
            {
                if (f.LastSeenScanSequence > 0)
                    usedSequences.Add(f.LastSeenScanSequence);
            }

            var keptRuns      = new List<ScanRun>();

            foreach (var run in _scanRuns)
            {
                if (!usedSequences.Contains(run.RunId))
                {
                    changedScanRuns = true;
                    continue;
                }

                keptRuns.Add(run);
            }

            if (changedScanRuns)
            {
                _scanRuns    = keptRuns;
                _scanRunIndex.Clear();
                foreach (var run in keptRuns) _scanRunIndex.Add(run.RunId, run);
            }

            // ------------------------------------
            // 5. Remove ScanRoots with no remaining runs
            // ------------------------------------
            var runsByRootId = _scanRuns.GroupBy(r => r.ScanRootId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var newRoots = new Dictionary<long, ScanRoot>();
            foreach (var (id, root) in _scanRoots)
            {
                if (runsByRootId.TryGetValue(id, out var runs) && runs.Count > 0)
                {
                    newRoots[id] = root;
                }
                else
                {
                    // no runs -> we'll drop this root and its snapshot file (later)
                    changedScanRoots = true;
                }
            }

            _scanRoots = newRoots;

            // ------------------------------------
            // 6. Deduplicate ScanRoots per RootPath
            // ------------------------------------
            var rootsByPath = _scanRoots.Values
                .GroupBy(r => r.RootPath, StringComparer.Ordinal)
                .ToList();

            var canonicalRoots   = new Dictionary<long, ScanRoot>();
            var rootIdRemap      = new Dictionary<long, long>(); // oldId -> canonicalId
            var rootIdsToDelete  = new HashSet<long>();

            foreach (var grp in rootsByPath)
            {
                var roots = grp.ToList();
                if (roots.Count == 1)
                {
                    var single = roots[0];
                    canonicalRoots[single.RootId] = single;
                    continue;
                }

                // Choose canonical:
                // 1. prefer one with non-empty DirId
                // 2. among those, prefer latest LastScannedAt
                var candidates = roots.Where(r => r.DirId != 0).ToList();
                if (candidates.Count == 0)
                    candidates = roots;

                var canonical = candidates
                    .OrderByDescending(r => r.LastScannedAt)
                    .First();

                canonicalRoots[canonical.RootId] = canonical;

                // Map others to canonical
                foreach (var r in roots)
                {
                    if (r.RootId == canonical.RootId)
                        continue;

                    rootIdRemap[r.RootId] = canonical.RootId;
                    rootIdsToDelete.Add(r.RootId);
                }
            }

            // Remap runs to canonical root ids
            if (rootIdRemap.Count > 0)
            {
                var newRuns     = new List<ScanRun>(_scanRuns.Count);

                foreach (var run in _scanRuns)
                {
                    if (rootIdRemap.TryGetValue(run.ScanRootId, out var canonicalId))
                    {
                        var updated = run with { ScanRootId = canonicalId };
                        newRuns.Add(updated);
                    }
                    else
                    {
                        newRuns.Add(run);
                    }
                }

                _scanRuns     = newRuns;
                _scanRunIndex.Clear();
                foreach (var run in newRuns) _scanRunIndex.Add(run.RunId, run);
                changedScanRuns = true;
            }

            if (rootIdsToDelete.Count > 0 || canonicalRoots.Count != _scanRoots.Count)
            {
                _scanRoots      = canonicalRoots;
                changedScanRoots = true;
            }

            // ------------------------------------
            // 7. Delete orphan per-root snapshot files
            // ------------------------------------
            if (Directory.Exists(rootsDirPath))
            {
                var validRootIds = new HashSet<long>(_scanRoots.Keys);

                throw new NotImplementedException("TODO");
                
                // foreach (var path in Directory.GetFiles(rootsDirPath, "*.mp"))
                // {
                //     var name = Path.GetFileNameWithoutExtension(path);
                //     if (!Guid.TryParseExact(name, "N", out var id))
                //         continue;
                //
                //     if (!validRootIds.Contains(id))
                //     {
                //         try
                //         {
                //             File.Delete(path);
                //         }
                //         catch
                //         {
                //             // ignore IO errors in repair
                //         }
                //     }
                // }
            }

            // ------------------------------------
            if (changedDirs || changedScanRoots || changedScanRuns)
            {
                SaveMeta_NoLock();
                SaveScanSnapshots_NoLock();
            }
        }
    }
    
    private long? TryGetDirIdForFullPath_NoLock(string canonicalFullPath)
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