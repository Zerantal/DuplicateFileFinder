// DuplicateFileFinderLib/Repository/Core/Repo.Integrity.cs

using DuplicateFileFinderLib.Repository.Storage.Models;

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Core;

public sealed partial class Repo
{
    public enum RepoIntegritySeverity
    {
        Info,
        Warning,
        Error
    }

    public sealed class RepoIntegrityIssue
    {
        public RepoIntegritySeverity Severity { get; init; }
        public string Code { get; init; } = "";
        public string Message { get; init; } = "";
        public string? FilePath { get; init; }
        // ReSharper disable once UnusedAutoPropertyAccessor.Global
        public Exception? Exception { get; init; }

        public override string ToString()
            => $"[{Severity}] {Code}: {Message}" +
               (FilePath is null ? "" : $" (File: {FilePath})");
    }

    /// <summary>
    /// Validate the integrity of this repo.
    /// - Shallow operation checks in-memory referential integrity and on-disk presence.
    /// - Deep operation rebuilds state from store (per-root snapshots) and compares with in-memory.
    /// </summary>
    public IReadOnlyList<RepoIntegrityIssue> ValidateIntegrity(
        bool deepConsistencyCheck = false,
        CancellationToken ct = default)
    {
        // Snapshot state under lock, then do IO outside
        string repoPath, rootsDirPath;
        RepoMeta meta;
        Dictionary<ScanRootId, ScanRoot> scanRoots;
        List<ScanRun> scanRuns;
        Dictionary<ScanRootId, ScanRootSnapshotV2> scanRootSnapshots;

        lock (_sync)
        {
            repoPath = _repoPath;
            rootsDirPath = Path.Combine(_repoPath, "roots");

            meta = _meta;
            scanRoots = new Dictionary<ScanRootId, ScanRoot>(_scanRoots);
            scanRuns = _scanRunIndex.Values.OrderBy(r => r.ScanSequence).ToList();
            scanRootSnapshots = new Dictionary<ScanRootId, ScanRootSnapshotV2>(_scanRootSnapshots);
        }

        // Flatten in-memory V2 snapshots into dir/file maps for referential checks.
        var (dirs, files, snapshotIssues) = BuildDirFileMapsFromSnapshots(scanRootSnapshots);
        var issues = new List<RepoIntegrityIssue>(capacity: 128);
        issues.AddRange(snapshotIssues);

        // ----------------------------------------------------
        // 1. repo.mp exists and deserializes
        // ----------------------------------------------------
        var metaPath = Path.Combine(repoPath, "repo.mp");
        if (!File.Exists(metaPath))
        {
            issues.Add(new RepoIntegrityIssue
            {
                Severity = RepoIntegritySeverity.Error,
                Code = "META_MISSING",
                Message = "Metadata file repo.mp is missing.",
                FilePath = metaPath
            });
        }
        else
        {
            try
            {
                var metaBytes = File.ReadAllBytes(metaPath);
                var metaFile = MemoryPackSerializer.Deserialize<RepoMetaFile>(metaBytes);
                if (metaFile is null)
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Error,
                        Code = "META_DESERIALIZE_NULL",
                        Message = "Failed to deserialize repo.mp (null result).",
                        FilePath = metaPath
                    });
                }
            }
            catch (Exception ex)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "META_DESERIALIZE_FAIL",
                    Message = $"Exception while deserializing repo.mp: {ex.Message}",
                    FilePath = metaPath,
                    Exception = ex
                });
            }
        }

        // ----------------------------------------------------
        // 2. Basic in-memory referential integrity
        // ----------------------------------------------------

        // 2a. ScanRoots -> snapshots and root dir id presence
        foreach (var root in scanRoots.Values.Where(r => !r.IsDeleted))
        {
            if (!scanRootSnapshots.TryGetValue(root.RootId, out _))
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Warning,
                    Code = "ROOT_SNAPSHOT_NOT_IN_MEMORY",
                    Message = $"ScanRoot {root.RootId} (rootPath={root.RootPath}) has no snapshot loaded in memory."
                });
            }

            if (root.DirId == 0)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Warning,
                    Code = "ROOT_DIRID_EMPTY",
                    Message = $"ScanRoot {root.RootId} has no dirId bound (rootPath={root.RootPath})."
                });
            }
            else if (!dirs.TryGetValue(root.DirId, out var dirRec) || dirRec.ScanRootId != root.RootId)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "ROOT_DIRID_MISSING_IN_SNAPSHOT",
                    Message = $"ScanRoot {root.RootId} dirId {root.DirId} not present in its snapshot."
                });
            }
        }

        // 2b. Orphan in-memory snapshots that have no ScanRoot (usually fine but worth reporting)
        foreach (var rootId in scanRootSnapshots.Keys)
        {
            if (!scanRoots.TryGetValue(rootId, out var r) || r.IsDeleted)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Info,
                    Code = "SNAPSHOT_ORPHAN_IN_MEMORY",
                    Message = $"In-memory snapshot exists for ScanRootId {rootId}, but ScanRoot is missing or deleted."
                });
            }
        }

        // 2c. Dirs: parent references (within same scan root) + cycles
        foreach (var (_, entry) in dirs)
        {
            var dir = entry.Record;

            if (dir.ParentDirId > 0)
            {
                if (!dirs.TryGetValue(dir.ParentDirId, out var parent))
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Error,
                        Code = "DIR_PARENT_MISSING",
                        Message = $"Dir {dir.DirId} (root={entry.ScanRootId}, nameIdx={dir.NameStrIdx}) has missing ParentDirId {dir.ParentDirId}."
                    });
                }
                else if (parent.ScanRootId != entry.ScanRootId)
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Error,
                        Code = "DIR_PARENT_WRONG_ROOT",
                        Message = $"Dir {dir.DirId} (root={entry.ScanRootId}) parent {dir.ParentDirId} belongs to different root {parent.ScanRootId}."
                    });
                }
            }
        }

        // cycle detection (by root)
        var visited = new HashSet<long>();
        var visiting = new HashSet<long>();
        foreach (var (dirId, entry) in dirs)
        {
            if (!visited.Contains(dirId))
                DetectDirCycleV2(dirId, entry.ScanRootId, dirs, visited, visiting, issues);
        }

        // 2d. Files -> Dirs, basic invariants
        foreach (var (_, entry) in files)
        {
            var file = entry.Record;

            if (file.DirId <= 0)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "FILE_DIRID_INVALID",
                    Message = $"File {file.FileId} (root={entry.ScanRootId}, nameIdx={file.NameStrIdx}) has invalid DirId {file.DirId}."
                });
                continue;
            }

            if (!dirs.TryGetValue(file.DirId, out var dirEntry))
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "FILE_DIR_MISSING",
                    Message = $"File {file.FileId} (root={entry.ScanRootId}, nameIdx={file.NameStrIdx}) references missing dirId {file.DirId}."
                });
            }
            else if (dirEntry.ScanRootId != entry.ScanRootId)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "FILE_DIR_WRONG_ROOT",
                    Message = $"File {file.FileId} (root={entry.ScanRootId}) references dirId {file.DirId} belonging to root {dirEntry.ScanRootId}."
                });
            }

            if (file.Size < 0)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Warning,
                    Code = "FILE_SIZE_NEGATIVE",
                    Message = $"File {file.FileId} (root={entry.ScanRootId}) has negative size {file.Size}."
                });
            }
        }

        // 2e. ScanRuns -> ScanRoots + sequence sanity + duplicates
        var seenRunIds = new HashSet<long>();
        long maxRunId = -1;

        foreach (var run in scanRuns)
        {
            if (!seenRunIds.Add(run.ScanSequence))
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "RUN_DUPLICATE_ID",
                    Message = $"Duplicate ScanRun id {run.ScanSequence} exists in metadata."
                });
            }

            maxRunId = Math.Max(maxRunId, run.ScanSequence);

            if (!scanRoots.ContainsKey(run.ScanRootId))
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "RUN_ROOT_MISSING",
                    Message = $"ScanRun {run.ScanSequence} references missing ScanRootId {run.ScanRootId}."
                });
            }

            if (run.ScanSequence >= meta.NextScanSequence)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Warning,
                    Code = "RUN_SEQUENCE_OUT_OF_RANGE",
                    Message = $"ScanRun {run.ScanSequence} >= NextScanSequence {meta.NextScanSequence}."
                });
            }
        }

        if (maxRunId >= 0 && meta.NextScanSequence <= maxRunId)
        {
            issues.Add(new RepoIntegrityIssue
            {
                Severity = RepoIntegritySeverity.Warning,
                Code = "META_NEXTSCANSEQ_NOT_ADVANCED",
                Message = $"_meta.NextScanSequence ({meta.NextScanSequence}) is not greater than max ScanRun id ({maxRunId})."
            });
        }

        // ----------------------------------------------------
        // 3. Higher-level consistency
        // ----------------------------------------------------

        // 3a. duplicate ScanRoots for same RootPath
        var rootsByVolumeAndPath = scanRoots.Values
            .Where(r => !r.IsDeleted)
            .GroupBy(
                r => new
                {
                    // Prefer stable identity if available; else fall back to path.
                    VolumeId = r.VolumeId ?? "",
                    VolumePath = r.VolumePath ?? "",
                    r.RootPath
                })
            .ToList();

        foreach (var grp in rootsByVolumeAndPath)
        {
            // If VolumeId is populated, only use that + RootPath.
            // If VolumeId is empty for all entries in the group, VolumePath+RootPath is still better than RootPath alone.
            if (grp.Count() > 1)
            {
                var ids = string.Join(", ", grp.Select(r => r.RootId));

                var volKey = !string.IsNullOrWhiteSpace(grp.Key.VolumeId)
                    ? $"VolumeId='{grp.Key.VolumeId}'"
                    : $"VolumePath='{grp.Key.VolumePath}'";

                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Warning,
                    Code = "ROOT_DUP_VOLUME_AND_ROOTPATH",
                    Message = $"{volKey}, RootPath '{grp.Key.RootPath}' has {grp.Count()} ScanRoots: {ids}."
                });
            }
        }

        // 3b. roots that have no runs at all
        var runsByRootId = scanRuns.GroupBy(r => r.ScanRootId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var root in scanRoots.Values.Where(r => !r.IsDeleted))
        {
            if (!runsByRootId.TryGetValue(root.RootId, out var runs) || runs.Count == 0)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Warning,
                    Code = "ROOT_UNUSED_NO_RUNS",
                    Message = $"ScanRoot {root.RootId} (rootPath={root.RootPath}) has no associated ScanRuns."
                });
            }
        }

        // ----------------------------------------------------
        // 5. Per-root snapshots on disk (ScanRootSnapshotV2)
        // ----------------------------------------------------
        var snapshotFilesById = new Dictionary<ScanRootId, string>();

        if (Directory.Exists(rootsDirPath))
        {
            foreach (var path in Directory.GetFiles(rootsDirPath, "*.mp"))
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                if (int.TryParse(fileName, out var id))
                {
                    snapshotFilesById[id] = path;
                }
                else
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Warning,
                        Code = "ROOT_SNAPSHOT_BAD_NAME",
                        Message = $"Snapshot file '{fileName}' does not parse as a long root id.",
                        FilePath = path
                    });
                }
            }
        }

        // missing snapshots for known (non-deleted) roots
        foreach (var root in scanRoots.Values.Where(r => !r.IsDeleted))
        {
            if (!snapshotFilesById.TryGetValue(root.RootId, out var path))
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Warning,
                    Code = "ROOT_SNAPSHOT_MISSING",
                    Message = $"No per-root snapshot for ScanRoot {root.RootId} (rootPath={root.RootPath}).",
                });
                continue;
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                var bytes = File.ReadAllBytes(path);
                var snap = MemoryPackSerializer.Deserialize<ScanRootSnapshotV2>(bytes);

                ValidateSnapshotV2(snap, path, issues);
            }
            catch (Exception ex)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "ROOT_SNAPSHOT_DESERIALIZE_FAIL",
                    Message = $"Exception while deserializing snapshot {path} as ScanRootSnapshotV2: {ex.Message}",
                    FilePath = path,
                    Exception = ex
                });
            }
        }

        // orphan snapshot files (no matching ScanRoot)
        foreach (var (id, path) in snapshotFilesById)
        {
            if (!scanRoots.TryGetValue(id, out var r) || r.IsDeleted)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Info,
                    Code = "ROOT_SNAPSHOT_ORPHAN",
                    Message = $"Snapshot file {path} has no matching ScanRoot (id={id}) or root is deleted.",
                    FilePath = path
                });
            }
        }

        // ----------------------------------------------------
        // 6. Optional deep consistency: rebuild state from store
        // ----------------------------------------------------
        if (deepConsistencyCheck)
        {
            try
            {
                var rebuilt = RebuildStateFromStoreV2(rootsDirPath, ct);

                CompareState(
                    "DIRS_V2",
                    dirs,
                    rebuilt.Dirs,
                    d => $"nameIdx={d.Record.NameStrIdx}",
                    issues);

                CompareState(
                    "FILES_V2",
                    files,
                    rebuilt.Files,
                    f => $"nameIdx={f.Record.NameStrIdx}",
                    issues);
            }
            catch (Exception ex)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "DEEP_REBUILD_FAIL",
                    Message = $"Exception during deep consistency rebuild: {ex.Message}",
                    Exception = ex
                });
            }
        }

        return DeduplicateIssues(issues);
    }


    private readonly record struct DirEntry(ScanRootId ScanRootId, DirRecordV2 Record);
    private readonly record struct FileEntry(ScanRootId ScanRootId, FileRecordV2 Record);

    private static (Dictionary<DirId, DirEntry> Dirs, Dictionary<FileId, FileEntry> Files, List<RepoIntegrityIssue> Issues)
        BuildDirFileMapsFromSnapshots(Dictionary<ScanRootId, ScanRootSnapshotV2> snapshots)
    {
        var dirs = new Dictionary<DirId, DirEntry>(capacity: Math.Max(1024, snapshots.Count * 1024));
        var files = new Dictionary<FileId, FileEntry>(capacity: Math.Max(1024, snapshots.Count * 1024));
        var issues = new List<RepoIntegrityIssue>();

        foreach (var (scanRootId, snap) in snapshots)
        {
            if (snap.ScanRootId != scanRootId)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Warning,
                    Code = "SNAPSHOT_ROOTID_MISMATCH",
                    Message = $"In-memory snapshot dictionary key scanRootId={scanRootId} but snapshot.ScanRootId={snap.ScanRootId}."
                });
            }

            var poolCount = snap.StringPool.Count;

            var seenDirIds = new HashSet<long>();
            foreach (var d in snap.Dirs)
            {
                if (!seenDirIds.Add(d.DirId))
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Error,
                        Code = "SNAPSHOT_DUP_DIRID",
                        Message = $"Snapshot for ScanRootId {scanRootId} contains duplicate dirId {d.DirId}."
                    });
                    continue;
                }

                ValidateRequiredStringIndex(
                    issues,
                    RepoIntegritySeverity.Error,
                    code: "DIR_NAMEIDX_INVALID",
                    scanRootId: scanRootId,
                    entityKind: "Dir",
                    entityId: d.DirId,
                    fieldName: nameof(d.NameStrIdx),
                    idx: d.NameStrIdx,
                    poolCount: poolCount);

                ValidateOptionalStringIndex(
                    issues,
                    RepoIntegritySeverity.Error,
                    code: "DIR_ERRIDX_INVALID",
                    scanRootId: scanRootId,
                    entityKind: "Dir",
                    entityId: d.DirId,
                    fieldName: nameof(d.ErrorMessageStrIdx),
                    idx: d.ErrorMessageStrIdx,
                    poolCount: poolCount);

                dirs[d.DirId] = new DirEntry(scanRootId, d);
            }

            var seenFileIds = new HashSet<long>();
            foreach (var f in snap.Files)
            {
                if (!seenFileIds.Add(f.FileId))
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Error,
                        Code = "SNAPSHOT_DUP_FILEID",
                        Message = $"Snapshot for ScanRootId {scanRootId} contains duplicate fileId {f.FileId}."
                    });
                    continue;
                }

                ValidateRequiredStringIndex(
                    issues,
                    RepoIntegritySeverity.Error,
                    code: "FILE_NAMEIDX_INVALID",
                    scanRootId: scanRootId,
                    entityKind: "File",
                    entityId: f.FileId,
                    fieldName: nameof(f.NameStrIdx),
                    idx: f.NameStrIdx,
                    poolCount: poolCount);

                ValidateOptionalStringIndex(
                    issues,
                    RepoIntegritySeverity.Error,
                    code: "FILE_ERRIDX_INVALID",
                    scanRootId: scanRootId,
                    entityKind: "File",
                    entityId: f.FileId,
                    fieldName: nameof(f.ErrorMessageStrIdx),
                    idx: f.ErrorMessageStrIdx,
                    poolCount: poolCount);

                files[f.FileId] = new FileEntry(scanRootId, f);
            }
        }

        return (dirs, files, issues);
    }

    private static void ValidateSnapshotV2(
        ScanRootSnapshotV2 snap,
        string filePath,
        IList<RepoIntegrityIssue> issues)
    {
        var poolCount = snap.StringPool.Count;
        var dirIds = new HashSet<long>();

        foreach (var d in snap.Dirs)
        {
            if (!dirIds.Add(d.DirId))
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "ROOT_SNAPSHOT_DUP_DIR",
                    Message = $"Snapshot {filePath} contains duplicate dirId {d.DirId}.",
                    FilePath = filePath
                });
            }

            ValidateRequiredStringIndex(
                issues,
                RepoIntegritySeverity.Error,
                code: "DIR_NAMEIDX_INVALID",
                scanRootId: snap.ScanRootId,
                entityKind: "Dir",
                entityId: d.DirId,
                fieldName: nameof(d.NameStrIdx),
                idx: d.NameStrIdx,
                poolCount: poolCount);

            ValidateOptionalStringIndex(
                issues,
                RepoIntegritySeverity.Error,
                code: "DIR_ERRIDX_INVALID",
                scanRootId: snap.ScanRootId,
                entityKind: "Dir",
                entityId: d.DirId,
                fieldName: nameof(d.ErrorMessageStrIdx),
                idx: d.ErrorMessageStrIdx,
                poolCount: poolCount);
        }

        foreach (var d in snap.Dirs)
        {
            if (d.ParentDirId > 0 && !dirIds.Contains(d.ParentDirId))
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "ROOT_SNAPSHOT_DIR_PARENT_MISSING",
                    Message = $"Snapshot {filePath} dirId {d.DirId} references missing ParentDirId {d.ParentDirId}.",
                    FilePath = filePath
                });
            }
        }

        foreach (var f in snap.Files)
        {
            if (!dirIds.Contains(f.DirId))
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "ROOT_SNAPSHOT_FILE_DIR_MISSING",
                    Message = $"Snapshot {filePath} fileId {f.FileId} references missing dirId {f.DirId}.",
                    FilePath = filePath
                });
            }

            ValidateRequiredStringIndex(
                issues,
                RepoIntegritySeverity.Error,
                code: "FILE_NAMEIDX_INVALID",
                scanRootId: snap.ScanRootId,
                entityKind: "File",
                entityId: f.FileId,
                fieldName: nameof(f.NameStrIdx),
                idx: f.NameStrIdx,
                poolCount: poolCount);

            ValidateOptionalStringIndex(
                issues,
                RepoIntegritySeverity.Error,
                code: "FILE_ERRIDX_INVALID",
                scanRootId: snap.ScanRootId,
                entityKind: "File",
                entityId: f.FileId,
                fieldName: nameof(f.ErrorMessageStrIdx),
                idx: f.ErrorMessageStrIdx,
                poolCount: poolCount);
        }
    }

    private sealed class RebuiltStateV2
    {
        public Dictionary<DirId, DirEntry> Dirs { get; init; } = new();
        public Dictionary<FileId, FileEntry> Files { get; init; } = new();
    }

    private static RebuiltStateV2 RebuildStateFromStoreV2(
        string rootsDirPath,
        CancellationToken ct)
    {
        var dirs = new Dictionary<DirId, DirEntry>();
        var files = new Dictionary<FileId, FileEntry>();

        // Per-root snapshots
        if (Directory.Exists(rootsDirPath))
        {
            foreach (var path in Directory.GetFiles(rootsDirPath, "*.mp"))
            {
                ct.ThrowIfCancellationRequested();
                var bytes = File.ReadAllBytes(path);
                var snap = MemoryPackSerializer.Deserialize<ScanRootSnapshotV2>(bytes);

                foreach (var d in snap.Dirs)
                    dirs[d.DirId] = new DirEntry(snap.ScanRootId, d);

                foreach (var f in snap.Files)
                    files[f.FileId] = new FileEntry(snap.ScanRootId, f);
            }
        }

        return new RebuiltStateV2 { Dirs = dirs, Files = files };
    }


    private static void CompareState<TIdType, TEntry>(
        string label,
        IDictionary<TIdType, TEntry> inMemory,
        IDictionary<TIdType, TEntry> rebuilt,
        Func<TEntry, string?> nameSelector,
        IList<RepoIntegrityIssue> issues)
    {
        foreach (var (id, value) in inMemory)
        {
            if (!rebuilt.ContainsKey(id))
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = $"{label}_MISSING_IN_REBUILT",
                    Message = $"{label}: id {id} ({nameSelector(value)}) exists in memory but not in rebuilt state."
                });
            }
        }

        foreach (var (id, value) in rebuilt)
        {
            if (!inMemory.ContainsKey(id))
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Warning,
                    Code = $"{label}_ONLY_IN_REBUILT",
                    Message = $"{label}: id {id} ({nameSelector(value)}) exists only in rebuilt state."
                });
            }
        }
    }

    private static void DetectDirCycleV2(
        DirId dirId,
        ScanRootId expectedRootId,
        IReadOnlyDictionary<DirId, DirEntry> dirs,
        HashSet<long> visited,
        HashSet<long> visiting,
        IList<RepoIntegrityIssue> issues)
    {
        if (!visiting.Add(dirId))
        {
            issues.Add(new RepoIntegrityIssue
            {
                Severity = RepoIntegritySeverity.Error,
                Code = "DIR_CYCLE",
                Message = $"Directory graph contains a cycle involving dirId {dirId} (expectedRoot={expectedRootId})."
            });
            return;
        }

        if (!dirs.TryGetValue(dirId, out var entry))
        {
            visiting.Remove(dirId);
            return;
        }

        if (entry.ScanRootId != expectedRootId)
        {
            issues.Add(new RepoIntegrityIssue
            {
                Severity = RepoIntegritySeverity.Error,
                Code = "DIR_CYCLE_CROSS_ROOT_EDGE",
                Message = $"Cycle traversal encountered cross-root edge at dirId {dirId}: expectedRoot={expectedRootId}, actualRoot={entry.ScanRootId}."
            });
            visiting.Remove(dirId);
            visited.Add(dirId);
            return;
        }

        var parentId = entry.Record.ParentDirId;
        if (parentId > 0 && dirs.TryGetValue(parentId, out var parent) && parent.ScanRootId == expectedRootId)
        {
            if (!visited.Contains(parentId))
                DetectDirCycleV2(parentId, expectedRootId, dirs, visited, visiting, issues);
        }

        visiting.Remove(dirId);
        visited.Add(dirId);
    }



    private static bool IsValidPoolIndex(int idx, int poolCount)
        => idx >= 0 && idx < poolCount;

    private static void ValidateRequiredStringIndex(
        IList<RepoIntegrityIssue> issues,
        RepoIntegritySeverity severity,
        string code,
        long scanRootId,
        string entityKind,
        long entityId,
        string fieldName,
        int idx,
        int poolCount,
        string? filePath = null)
    {
        if (IsValidPoolIndex(idx, poolCount))
            return;

        issues.Add(new RepoIntegrityIssue
        {
            Severity = severity,
            Code = code,
            Message = $"{entityKind} {entityId} (ScanRoot={scanRootId}) has {fieldName}={idx} which is invalid. Expected 0..{poolCount - 1}.",
            FilePath = filePath
        });
    }

    private static void ValidateOptionalStringIndex(
        IList<RepoIntegrityIssue> issues,
        RepoIntegritySeverity severity,
        string code,
        long scanRootId,
        string entityKind,
        long entityId,
        string fieldName,
        int idx,
        int poolCount,
        string? filePath = null)
    {
        // Optional string: -1 means “none”
        if (idx < 0)
            return;

        ValidateRequiredStringIndex(
            issues, severity, code, scanRootId, entityKind, entityId, fieldName, idx, poolCount, filePath);
    }

    private static string BuildIssueKey(RepoIntegrityIssue i)
    {
        // The aim is: if two paths report the same thing, keep only one.
        // Prefer entries with FilePath (more actionable).
        // Key includes Code + ScanRoot/Entity info embedded in message, so Message is part of key.
        // (If you later add structured fields, switch to those instead of Message.)
        return string.Join("|",
            i.Severity.ToString(),
            i.Code,
            i.FilePath ?? "",
            i.Message);
    }

    private static List<RepoIntegrityIssue> DeduplicateIssues(List<RepoIntegrityIssue> issues)
    {
        // Prefer issues that have FilePath when duplicates exist.
        var bestByKey = new Dictionary<string, RepoIntegrityIssue>(capacity: issues.Count);

        foreach (var issue in issues)
        {
            var key = BuildIssueKey(issue);

            if (!bestByKey.TryGetValue(key, out var existing))
            {
                bestByKey[key] = issue;
                continue;
            }

            // Prefer the one that has FilePath (more actionable).
            var existingHasPath = !string.IsNullOrWhiteSpace(existing.FilePath);
            var currentHasPath = !string.IsNullOrWhiteSpace(issue.FilePath);

            if (!existingHasPath && currentHasPath)
                bestByKey[key] = issue;

            // Otherwise keep first (stable).
        }

        // Keep a stable-ish order: severity then code then message.
        // (If you want to preserve original order, store an ordinal and sort by it.)
        return bestByKey.Values
            .OrderBy(i => i.Severity)
            .ThenBy(i => i.Code, StringComparer.Ordinal)
            .ThenBy(i => i.FilePath ?? "", StringComparer.Ordinal)
            .ThenBy(i => i.Message, StringComparer.Ordinal)
            .ToList();
    }

}
