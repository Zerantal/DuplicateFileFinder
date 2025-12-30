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
        Dictionary<long, ScanRoot> scanRoots;
        List<ScanRun> scanRuns;
        Dictionary<long, ScanRootSnapshotV2> scanRootSnapshots;

        lock (_sync)
        {
            repoPath = _repoPath;
            rootsDirPath = Path.Combine(_repoPath, "roots");

            meta = _meta;
            scanRoots = new Dictionary<long, ScanRoot>(_scanRoots);
            scanRuns = new List<ScanRun>(_scanRuns);
            scanRootSnapshots = new Dictionary<long, ScanRootSnapshotV2>(_scanRootSnapshots);
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
        var rootsByPath = scanRoots.Values
            .Where(r => !r.IsDeleted)
            .GroupBy(r => r.RootPath, StringComparer.Ordinal)
            .ToList();

        foreach (var grp in rootsByPath)
        {
            if (grp.Count() > 1)
            {
                var ids = string.Join(", ", grp.Select(r => r.RootId));
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Warning,
                    Code = "ROOT_DUP_ROOTPATH",
                    Message = $"RootPath '{grp.Key}' has {grp.Count()} ScanRoots: {ids}."
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
        var snapshotFilesById = new Dictionary<long, string>();

        if (Directory.Exists(rootsDirPath))
        {
            foreach (var path in Directory.GetFiles(rootsDirPath, "*.mp"))
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                if (long.TryParse(fileName, out var id))
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

        return issues;
    }

    private readonly record struct DirEntry(long ScanRootId, DirRecordV2 Record);
    private readonly record struct FileEntry(long ScanRootId, FileRecordV2 Record);

    private static (Dictionary<long, DirEntry> Dirs, Dictionary<long, FileEntry> Files, List<RepoIntegrityIssue> Issues)
        BuildDirFileMapsFromSnapshots(Dictionary<long, ScanRootSnapshotV2> snapshots)
    {
        var dirs = new Dictionary<long, DirEntry>(capacity: Math.Max(1024, snapshots.Count * 1024));
        var files = new Dictionary<long, FileEntry>(capacity: Math.Max(1024, snapshots.Count * 1024));
        var issues = new List<RepoIntegrityIssue>();

        foreach (var (rootId, snap) in snapshots)
        {
            if (snap.ScanRootId != rootId)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Warning,
                    Code = "SNAPSHOT_ROOTID_MISMATCH",
                    Message = $"In-memory snapshot dictionary key rootId={rootId} but snapshot.ScanRootId={snap.ScanRootId}."
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
                        Message = $"Snapshot for ScanRootId {rootId} contains duplicate dirId {d.DirId}."
                    });
                    continue;
                }

                if (snap.StringPool is not null && (uint)d.NameStrIdx >= (uint)poolCount && d.ParentDirId != -1)
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Error,
                        Code = "DIR_NAMEIDX_OOB",
                        Message = $"Dir {d.DirId} (root={rootId}) has NameStrIdx {d.NameStrIdx} outside pool range 0..{poolCount - 1}."
                    });
                }

                if (d.ErrorMessageStrIdx >= 0 && snap.StringPool is not null &&
                    (uint)d.ErrorMessageStrIdx >= (uint)poolCount)
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Error,
                        Code = "DIR_ERRIDX_OOB",
                        Message = $"Dir {d.DirId} (root={rootId}) has ErrorMessageStrIdx {d.ErrorMessageStrIdx} outside pool range 0..{poolCount - 1}."
                    });
                }

                dirs[d.DirId] = new DirEntry(rootId, d);
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
                        Message = $"Snapshot for ScanRootId {rootId} contains duplicate fileId {f.FileId}."
                    });
                    continue;
                }

                if (snap.StringPool is not null && (uint)f.NameStrIdx >= (uint)poolCount)
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Error,
                        Code = "FILE_NAMEIDX_OOB",
                        Message = $"File {f.FileId} (root={rootId}) has NameStrIdx {f.NameStrIdx} outside pool range 0..{poolCount - 1}."
                    });
                }

                if (f.ErrorMessageStrIdx >= 0 && snap.StringPool is not null &&
                    (uint)f.ErrorMessageStrIdx >= (uint)poolCount)
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Error,
                        Code = "FILE_ERRIDX_OOB",
                        Message = $"File {f.FileId} (root={rootId}) has ErrorMessageStrIdx {f.ErrorMessageStrIdx} outside pool range 0..{poolCount - 1}."
                    });
                }

                files[f.FileId] = new FileEntry(rootId, f);
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

            if ((uint)d.NameStrIdx >= (uint)poolCount && d.ParentDirId != -1)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "ROOT_SNAPSHOT_DIR_NAMEIDX_OOB",
                    Message = $"Snapshot {filePath} dirId {d.DirId} has NameStrIdx {d.NameStrIdx} outside pool range 0..{poolCount - 1}.",
                    FilePath = filePath
                });
            }

            if (d.ErrorMessageStrIdx >= 0 && (uint)d.ErrorMessageStrIdx >= (uint)poolCount)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "ROOT_SNAPSHOT_DIR_ERRIDX_OOB",
                    Message = $"Snapshot {filePath} dirId {d.DirId} has ErrorMessageStrIdx {d.ErrorMessageStrIdx} outside pool range 0..{poolCount - 1}.",
                    FilePath = filePath
                });
            }
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

            if ((uint)f.NameStrIdx >= (uint)poolCount)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "ROOT_SNAPSHOT_FILE_NAMEIDX_OOB",
                    Message = $"Snapshot {filePath} fileId {f.FileId} has NameStrIdx {f.NameStrIdx} outside pool range 0..{poolCount - 1}.",
                    FilePath = filePath
                });
            }

            if (f.ErrorMessageStrIdx >= 0 && (uint)f.ErrorMessageStrIdx >= (uint)poolCount)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "ROOT_SNAPSHOT_FILE_ERRIDX_OOB",
                    Message = $"Snapshot {filePath} fileId {f.FileId} has ErrorMessageStrIdx {f.ErrorMessageStrIdx} outside pool range 0..{poolCount - 1}.",
                    FilePath = filePath
                });
            }
        }
    }

    private sealed class RebuiltStateV2
    {
        public Dictionary<long, DirEntry> Dirs { get; init; } = new();
        public Dictionary<long, FileEntry> Files { get; init; } = new();
    }

    private static RebuiltStateV2 RebuildStateFromStoreV2(
        string rootsDirPath,
        CancellationToken ct)
    {
        var dirs = new Dictionary<long, DirEntry>();
        var files = new Dictionary<long, FileEntry>();

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


    private static void CompareState<TEntry>(
        string label,
        IDictionary<long, TEntry> inMemory,
        IDictionary<long, TEntry> rebuilt,
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
        long dirId,
        long expectedRootId,
        IReadOnlyDictionary<long, DirEntry> dirs,
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
}