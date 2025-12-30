// DuplicateFileFinderLib/Repository/Core/Repo.IntegrityRepair.cs

using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Repository.Core;

public sealed partial class Repo
{
    /// <summary>
    /// One-off repair for migrated / messy repos (best-effort).
    ///
    /// Repairs:
    /// - In each per-root snapshot: any dir whose ParentDirId is missing is promoted to a root (ParentDirId = -1).
    /// - Ensures ScanRoots referenced by ScanRuns exist.
    /// - Ensures ScanRoot.DirId != 0 (allocates if missing).
    /// - Removes ScanRuns not referenced by any snapshot dir/file LastSeenScanSequence.
    /// - Removes non-deleted ScanRoots with no remaining runs.
    /// - Deduplicates non-deleted ScanRoots by (VolumePath, RootPath).
    /// - Deletes orphan per-root snapshot files (no matching ScanRoot).
    ///
    /// Persistence:
    /// - Writes ONLY changed snapshots.
    /// - Writes meta only if it changed.
    /// </summary>
    public async Task RepairMigratedRepoAsync(CancellationToken ct = default)
    {
        // Capture repo path (no locking needed after construction)
        string repoPath;
        lock (_sync)
        {
            repoPath = _repoPath;
        }

        var rootsDirPath = Path.Combine(repoPath, "roots");
        Directory.CreateDirectory(rootsDirPath);

        // Track changes we need to persist outside lock.
        var changedSnapshots = new List<ScanRootSnapshotV2>();
        bool metaChanged = false;

        // ------------------------------
        // 1) Fix per-root snapshots: parent missing => promote to root
        // ------------------------------
        lock (_sync)
        {
            foreach (var (rootId, snap) in _scanRootSnapshots.ToArray())
            {
                var dirs = snap.Dirs;
                if (dirs.Length == 0)
                    continue;

                var knownDirIds = new HashSet<long>(dirs.Length);
                foreach (var d in dirs)
                    knownDirIds.Add(d.DirId);

                bool changed = false;
                var newDirs = (DirRecordV2[])dirs.Clone();

                for (int i = 0; i < newDirs.Length; i++)
                {
                    var d = newDirs[i];

                    // ParentDirId == -1 is already a root/sentinel.
                    if (d.ParentDirId >= 0 && !knownDirIds.Contains(d.ParentDirId))
                    {
                        newDirs[i] = d with { ParentDirId = -1 };
                        changed = true;
                    }
                }

                if (changed)
                {
                    var updated = snap with { Dirs = newDirs };
                    _scanRootSnapshots[rootId] = updated;
                    changedSnapshots.Add(updated);
                }
            }
        }

        // Persist changed snapshots (outside lock). RepoStore is gated; writes are temp+move.
        foreach (var s in changedSnapshots)
        {
            ct.ThrowIfCancellationRequested();
            await RepoStore.SaveScanRootSnapshotV2Async(repoPath, s, ct).ConfigureAwait(false);
        }

        // ------------------------------
        // 2) Fix meta: ensure ScanRoots exist for ScanRuns, bind DirId, remove orphan runs/roots, dedupe roots
        // ------------------------------
        lock (_sync)
        {
            // 2a) Ensure ScanRoots referenced by runs exist (best-effort).
            foreach (var run in _scanRuns.ToArray())
            {
                if (run.ScanRootId <= 0)
                    continue;

                if (_scanRoots.ContainsKey(run.ScanRootId))
                    continue;

                if (string.IsNullOrWhiteSpace(run.RootPath))
                    continue; // cannot recover without a path

                var canonicalRootPath = PathUtils.NormalizePath(run.RootPath);

                // If we have a snapshot for this ScanRootId, attempt to pick a plausible root DirId.
                long dirId = 0;
                if (_scanRootSnapshots.TryGetValue(run.ScanRootId, out var snap))
                {
                    dirId = PickSnapshotRootDirId_NoLock(snap, canonicalRootPath);
                }

                var root = new ScanRoot
                {
                    RootId = run.ScanRootId,
                    RootPath = canonicalRootPath,
                    DirId = dirId,
                    CreatedAt = run.StartedAt,
                    LastScannedAt = run.FinishedAt ?? run.StartedAt,

                    VolumeId = null,
                    VolumeLabel = null,
                    DisplayName = null,
                    IsRotational = null,
                    FileSystemType = null,
                    DevicePath = null,
                    DeviceModel = null,
                    VolumePath = null,
                    IsDeleted = false,
                    DeletedAtUtc = null
                };

                _scanRoots[root.RootId] = root;
                metaChanged = true;
            }

            // 2b) Ensure ScanRoot.DirId != 0 (allocate if missing).
            foreach (var (id, root) in _scanRoots.ToArray())
            {
                if (root.DirId > 0)
                    continue;

                long dirId = 0;
                if (_scanRootSnapshots.TryGetValue(id, out var snap))
                {
                    dirId = PickSnapshotRootDirId_NoLock(snap, root.RootPath);
                }

                if (dirId <= 0)
                {
                    dirId = AllocateDirId_NoLock(); // stable id; snapshot may not contain it, but this is “repair”.
                }

                _scanRoots[id] = root with { DirId = dirId };
                metaChanged = true;
            }

            // 2c) Remove orphan runs (no references in any snapshot LastSeenScanSequence).
            var usedSequences = new HashSet<long>();

            foreach (var snap in _scanRootSnapshots.Values)
            {
                foreach (var d in snap.Dirs)
                    if (d.LastSeenScanSequence > 0)
                        usedSequences.Add(d.LastSeenScanSequence);

                foreach (var f in snap.Files)
                    if (f.LastSeenScanSequence > 0)
                        usedSequences.Add(f.LastSeenScanSequence);
            }

            if (usedSequences.Count > 0)
            {
                var keptRuns = new List<ScanRun>(_scanRuns.Count);
                foreach (var run in _scanRuns)
                {
                    if (usedSequences.Contains(run.ScanSequence))
                        keptRuns.Add(run);
                    else
                        metaChanged = true;
                }

                if (keptRuns.Count != _scanRuns.Count)
                {
                    _scanRuns = keptRuns;
                    _scanRunIndex.Clear();
                    foreach (var run in keptRuns)
                        _scanRunIndex[run.ScanSequence] = run;
                }
            }

            // 2d) Remove non-deleted roots with no remaining runs.
            var runsByRootId = _scanRuns
                .GroupBy(r => r.ScanRootId)
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var (id, root) in _scanRoots.ToArray())
            {
                if (root.IsDeleted)
                    continue;

                if (!runsByRootId.TryGetValue(id, out var count) || count == 0)
                {
                    _scanRoots.Remove(id);
                    _scanRootSnapshots.Remove(id);
                    metaChanged = true;
                }
            }

            // 2e) Deduplicate non-deleted roots by (VolumePath, RootPath)
            var liveRoots = _scanRoots.Values.Where(r => !r.IsDeleted).ToList();
            var deletedRoots = _scanRoots.Values.Where(r => r.IsDeleted).ToList();

            var grouped = liveRoots
                .GroupBy(r => (VolumePath: r.VolumePath ?? string.Empty, r.RootPath),
                    VolumeRootKeyComparer.Ordinal)
                .ToList();


            var canonical = new Dictionary<long, ScanRoot>();
            var remap = new Dictionary<long, long>(); // oldRootId -> canonicalRootId

            foreach (var grp in grouped)
            {
                var list = grp.ToList();
                if (list.Count == 1)
                {
                    canonical[list[0].RootId] = list[0];
                    continue;
                }

                // Choose canonical:
                // 1) prefer DirId != 0
                // 2) prefer latest LastScannedAt
                var candidates = list.Where(r => r.DirId != 0).ToList();
                if (candidates.Count == 0)
                    candidates = list;

                var chosen = candidates
                    .OrderByDescending(r => r.LastScannedAt ?? DateTimeOffset.MinValue)
                    .First();

                canonical[chosen.RootId] = chosen;

                foreach (var r in list)
                {
                    if (r.RootId == chosen.RootId) continue;
                    remap[r.RootId] = chosen.RootId;
                }

                metaChanged = true;
            }

            // Remap ScanRuns to canonical root ids
            if (remap.Count > 0)
            {
                var newRuns = new List<ScanRun>(_scanRuns.Count);
                foreach (var run in _scanRuns)
                {
                    if (remap.TryGetValue(run.ScanRootId, out var newId))
                        newRuns.Add(run with { ScanRootId = newId });
                    else
                        newRuns.Add(run);
                }

                _scanRuns = newRuns;
                _scanRunIndex.Clear();
                foreach (var run in newRuns)
                    _scanRunIndex[run.ScanSequence] = run;
            }

            // Add back deleted roots unchanged.
            foreach (var r in deletedRoots)
                canonical[r.RootId] = r;

            _scanRoots = canonical;

            if (metaChanged)
            {
                // Update in-memory meta file snapshot; persist outside lock.
                _metaFile = new RepoMetaFile
                {
                    Meta = _meta,
                    ScanRoots = _scanRoots.Values.ToList(),
                    ScanRuns = _scanRuns.ToList()
                };
            }
        }

        if (metaChanged)
        {
            await RepoStore.SaveMetaAsync(repoPath, _metaFile, ct).ConfigureAwait(false);
        }

        // ------------------------------
        // 3) Delete orphan per-root snapshot files (no matching ScanRoot)
        // ------------------------------
        var validRootIds = new HashSet<long>();
        lock (_sync)
        {
            foreach (var id in _scanRoots.Keys)
                validRootIds.Add(id);
        }

        if (Directory.Exists(rootsDirPath))
        {
            foreach (var file in Directory.GetFiles(rootsDirPath, "*.mp"))
            {
                ct.ThrowIfCancellationRequested();

                var name = Path.GetFileNameWithoutExtension(file);
                if (!long.TryParse(name, out var id))
                    continue;

                if (!validRootIds.Contains(id))
                {
                    try { File.Delete(file); } catch { /* tolerate */ }
                }
            }
        }
    }

    /// <summary>
    /// Best-effort: pick a plausible root directory id from a snapshot.
    /// Prefers: a dir whose ParentDirId == -1 and name matches the leaf of rootPath.
    /// Falls back to: first dir with ParentDirId == -1.
    /// </summary>
    private static long PickSnapshotRootDirId_NoLock(ScanRootSnapshotV2 snap, string rootPath)
    {
        if (snap.Dirs.Length == 0)
            return 0;

        var pool = snap.StringPool;

        static string Leaf(string p)
        {
            var trimmed = p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
        }

        var leafName = Leaf(rootPath);

        long firstRoot = 0;

        foreach (var d in snap.Dirs)
        {
            if (d.ParentDirId != -1)
                continue;

            if (firstRoot == 0)
                firstRoot = d.DirId;

            try
            {
                var name = pool.GetString(d.NameStrIdx);
                if (string.Equals(name, leafName, StringComparison.Ordinal))
                    return d.DirId;
            }
            catch
            {
                // tolerate broken string index
            }
        }

        return firstRoot;
    }

    private sealed class VolumeRootKeyComparer : IEqualityComparer<(string VolumePath, string RootPath)>
    {
        public static readonly VolumeRootKeyComparer Ordinal = new(StringComparer.Ordinal);

        private readonly StringComparer _cmp;

        private VolumeRootKeyComparer(StringComparer cmp) => _cmp = cmp;

        public bool Equals((string VolumePath, string RootPath) x, (string VolumePath, string RootPath) y)
            => _cmp.Equals(x.VolumePath, y.VolumePath) && _cmp.Equals(x.RootPath, y.RootPath);

        public int GetHashCode((string VolumePath, string RootPath) obj)
        {
            unchecked
            {
                int h1 = _cmp.GetHashCode(obj.VolumePath);
                int h2 = _cmp.GetHashCode(obj.RootPath);
                return (h1 * 397) ^ h2;
            }
        }
    }

}