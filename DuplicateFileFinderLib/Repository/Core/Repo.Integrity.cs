// DuplicateFileFinderLib/Repository/Repo.Integrity.cs

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;
using MemoryPack;
using DirRecord = DuplicateFileFinderLib.Repository.Storage.Models.DirRecord;
using FileRecord = DuplicateFileFinderLib.Repository.Storage.Models.FileRecord;
using RepoDelta = DuplicateFileFinderLib.Repository.Storage.Models.RepoDelta;
using RepoMetaFile = DuplicateFileFinderLib.Repository.Storage.Models.RepoMetaFile;
using ScanRun = DuplicateFileFinderLib.Repository.Storage.Models.ScanRun;

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
    /// - Deep operation rebuilds state from store (per-root snapshots + deltas) and compares with in-memory.
    /// </summary>
    public IReadOnlyList<RepoIntegrityIssue> ValidateIntegrity(
        bool deepConsistencyCheck = false,
        CancellationToken ct = default)
    {
        // Snapshot state under lock, then do IO outside
        string repoPath, logDirPath, rootsDirPath;
        RepoMeta meta;
        Dictionary<long, DirRecord> dirs;
        Dictionary<long, FileRecord> files;
        Dictionary<long, ScanRoot> scanRoots;
        List<ScanRun> scanRuns;

        lock (_sync)
        {
            repoPath     = _repoPath;
            logDirPath   = _logDirPath;
            rootsDirPath = Path.Combine(_repoPath, "roots");

            meta      = Meta;
            dirs      = new Dictionary<long, DirRecord>(_dirs);
            files     = new Dictionary<long, FileRecord>(_files);
            scanRoots = new Dictionary<long, ScanRoot>(_scanRoots);
            scanRuns  = new List<ScanRun>(_scanRuns);
        }

        var issues = new List<RepoIntegrityIssue>();

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
                var metaFile  = MemoryPackSerializer.Deserialize<RepoMetaFile>(metaBytes);
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

        // 2a. ScanRoots -> Dirs
        foreach (var root in scanRoots.Values.Where(r => !r.IsDeleted))
        {
            if (root.DirId == 0)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Warning,
                    Code = "ROOT_DIRID_EMPTY",
                    Message = $"ScanRoot {root.RootId} has no dirId bound (rootPath={root.RootPath})."
                });
                continue;
            }

            if (!dirs.ContainsKey(root.DirId))
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "ROOT_DIRID_MISSING",
                    Message  = $"ScanRoot {root.RootId} dirId {root.DirId} not found in dirs (rootPath={root.RootPath})."
                });
            }
        }

        // 2b. Dirs: parent references, cycles
        var visited = new HashSet<long>();
        var visiting = new HashSet<long>();

        foreach (var dir in dirs.Values)
        {
            if (dir.ParentDirId is { } parentId && !dirs.ContainsKey(parentId))
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "DIR_PARENT_MISSING",
                    Message = $"Dir {dir.DirId} ('{dir.Name}') has missing ParentDirId {parentId}."
                });
            }
        }

        foreach (var dir in dirs.Values)
        {
            if (!visited.Contains(dir.DirId))
                DetectDirCycle(dir.DirId, dirs, visited, visiting, issues);
        }

        // 2c. Files -> Dirs
        foreach (var file in files.Values)
        {
            if (!dirs.ContainsKey(file.DirId))
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "FILE_DIR_MISSING",
                    Message = $"File {file.FileId} ('{file.Name}') references missing dirId {file.DirId}."
                });
            }
        }

        // 2d. ScanRuns -> ScanRoots + sequence range
        var rootsById = scanRoots;
        foreach (var run in scanRuns)
        {
            if (!rootsById.ContainsKey(run.ScanRootId))
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

        // ----------------------------------------------------
        // 3. Higher-level consistency:
        //    - duplicate roots for same RootPath
        //    - roots with no runs
        //    - orphan runs (no dir/file references)
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
                    Code     = "ROOT_DUP_ROOTPATH",
                    Message  = $"RootPath '{grp.Key}' has {grp.Count()} ScanRoots: {ids}."
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
                    Code     = "ROOT_UNUSED_NO_RUNS",
                    Message  = $"ScanRoot {root.RootId} (rootPath={root.RootPath}) has no associated ScanRuns."
                });
            }
        }
        
        // ----------------------------------------------------
        // 4. Log files: naming + basic deserialization
        // ----------------------------------------------------
        if (Directory.Exists(logDirPath))
        {
            var pattern = $"{meta.Generation}-*.delta";
            var logFiles = Directory.GetFiles(logDirPath, pattern)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            long lastLogId = -1;
            foreach (var path in logFiles)
            {
                var name = Path.GetFileNameWithoutExtension(path); // "<gen>-<logId>"
                var dash = name.IndexOf('-');
                if (dash <= 0)
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Warning,
                        Code = "LOG_BAD_NAME",
                        Message = $"Delta file '{name}' does not contain '<gen>-<id>'.",
                        FilePath = path
                    });
                    continue;
                }

                var idPart = name[(dash + 1)..];
                if (!long.TryParse(idPart, out var logId))
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Warning,
                        Code = "LOG_BAD_ID",
                        Message = $"Delta file '{name}' has non-numeric id part '{idPart}'.",
                        FilePath = path
                    });
                    continue;
                }

                if (logId <= lastLogId)
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Warning,
                        Code = "LOG_OUT_OF_ORDER",
                        Message = $"Delta file '{name}' has non-increasing id (prev={lastLogId}, cur={logId}).",
                        FilePath = path
                    });
                }
                lastLogId = logId;

                if (logId >= meta.NextLogSequence)
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Warning,
                        Code = "LOG_ID_GE_NEXT",
                        Message = $"Delta file '{name}' has id {logId} >= NextLogSequence {meta.NextLogSequence}.",
                        FilePath = path
                    });
                }

                try
                {
                    ct.ThrowIfCancellationRequested();
                    var bytes = File.ReadAllBytes(path);
                    var delta = MemoryPackSerializer.Deserialize<RepoDelta>(bytes);
                    if (delta is null)
                    {
                        issues.Add(new RepoIntegrityIssue
                        {
                            Severity = RepoIntegritySeverity.Error,
                            Code = "LOG_DESERIALIZE_NULL",
                            Message = $"Delta file '{name}' deserialized to null.",
                            FilePath = path
                        });
                    }
                }
                catch (Exception ex)
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Error,
                        Code = "LOG_DESERIALIZE_FAIL",
                        Message = $"Exception while deserializing delta '{name}': {ex.Message}",
                        FilePath = path,
                        Exception = ex
                    });
                }
            }
        }

        // ----------------------------------------------------
        // 5. Per-root snapshots:
        //    - missing snapshots for existing roots
        //    - orphan snapshot files (no matching root)
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
                        Code     = "ROOT_SNAPSHOT_BAD_NAME",
                        Message  = $"Snapshot file '{fileName}' …does not parse as a long root id.",
                        FilePath = path
                    });
                }
            }
        }

        // missing snapshots for known roots
        foreach (var root in scanRoots.Values.Where(r => !r.IsDeleted))
        {
            if (!snapshotFilesById.TryGetValue(root.RootId, out var path))
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Warning,
                    Code = "ROOT_SNAPSHOT_MISSING",
                    Message  = $"No per-root snapshot for ScanRoot {root.RootId} (rootPath={root.RootPath}).",
                });
                continue;
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                var bytes = File.ReadAllBytes(path);
                var snap  = MemoryPackSerializer.Deserialize<ScanRootSnapshot>(bytes);
                if (snap is null)
                {
                    issues.Add(new RepoIntegrityIssue
                    {
                        Severity = RepoIntegritySeverity.Error,
                        Code = "ROOT_SNAPSHOT_NULL",
                        Message  = $"Snapshot file {path} deserialized to null.",
                        FilePath = path
                    });
                    continue;
                }

                var dirIds = new HashSet<long>();
                foreach (var d in snap.Dirs)
                {
                    if (!dirIds.Add(d.DirId))
                    {
                        issues.Add(new RepoIntegrityIssue
                        {
                            Severity = RepoIntegritySeverity.Error,
                            Code = "ROOT_SNAPSHOT_DUP_DIR",
                            Message  = $"Snapshot {path} contains duplicate dirId {d.DirId}.",
                            FilePath = path
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
                            Message  = $"Snapshot {path} file {f.FileId} references missing dirId {f.DirId}.",
                            FilePath = path
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Error,
                    Code = "ROOT_SNAPSHOT_DESERIALIZE_FAIL",
                    Message   = $"Exception while deserializing snapshot {path}: {ex.Message}",
                    FilePath  = path,
                    Exception = ex
                });
            }
        }

        // orphan snapshot files (no matching ScanRoot)
        foreach (var (id, path) in snapshotFilesById)
        {
            if (!scanRoots.ContainsKey(id))
            {
                issues.Add(new RepoIntegrityIssue
                {
                    Severity = RepoIntegritySeverity.Info,
                    Code     = "ROOT_SNAPSHOT_ORPHAN",
                    Message  = $"Snapshot file {path} has no matching ScanRoot (id={id}).",
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
                var rebuilt = RebuildStateFromStore(meta, logDirPath, rootsDirPath, ct);

                CompareState(
                    "DIRS",
                    dirs,
                    rebuilt.Dirs,
                    d => d.Name,
                    issues);

                CompareState(
                    "FILES",
                    files,
                    rebuilt.Files,
                    f => f.Name,
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

    private sealed class RebuiltState
    {
        public Dictionary<long, DirRecord> Dirs { get; init; } = new();
        public Dictionary<long, FileRecord> Files { get; init; } = new();
    }

    private static RebuiltState RebuildStateFromStore(
        RepoMeta meta,
        string logDirPath,
        string rootsDirPath,
        CancellationToken ct)
    {
        var dirs  = new Dictionary<long, DirRecord>();
        var files = new Dictionary<long, FileRecord>();

        // Per-root snapshots
        if (Directory.Exists(rootsDirPath))
        {
            foreach (var path in Directory.GetFiles(rootsDirPath, "*.mp"))
            {
                ct.ThrowIfCancellationRequested();
                var bytes = File.ReadAllBytes(path);
                var snap  = MemoryPackSerializer.Deserialize<ScanRootSnapshot>(bytes);
                if (snap is null)
                    continue;

                foreach (var d in snap.Dirs)
                    dirs[d.DirId] = d;
                foreach (var f in snap.Files)
                    files[f.FileId] = f;
            }
        }

        // Deltas newer than baseline
        if (Directory.Exists(logDirPath))
        {
            var pattern = $"{meta.Generation}-*.delta";
            var filesOnDisk  = Directory.GetFiles(logDirPath, pattern)
                .OrderBy(f => f, StringComparer.Ordinal);

            foreach (var path in filesOnDisk)
            {
                ct.ThrowIfCancellationRequested();

                var name = Path.GetFileNameWithoutExtension(path); // "<gen>-<logId>"
                var dash = name.IndexOf('-');
                if (dash <= 0)
                    continue;

                var idPart = name[(dash + 1)..];
                if (!long.TryParse(idPart, out var logId))
                    continue;

                if (logId <= meta.LastSnapshottedLogSequence)
                    continue;

                var bytes = File.ReadAllBytes(path);
                var delta = MemoryPackSerializer.Deserialize<RepoDelta>(bytes);
                if (delta is null)
                    continue;

                ApplyDeltaToMaps(delta, dirs, files);
            }
        }

        return new RebuiltState { Dirs = dirs, Files = files };
    }

    private static void ApplyDeltaToMaps(
        RepoDelta delta,
        IDictionary<long, DirRecord> dirs,
        IDictionary<long, FileRecord> files)
    {
        foreach (var d in delta.Dirs)
            if (d.Status == ScanEntryStatus.Deleted)
                dirs.Remove(d.DirId);
            else
                dirs[d.DirId] = d;

        foreach (var f in delta.Files)
            if (f.Status == ScanEntryStatus.Deleted)
                files.Remove(f.FileId);
            else
                files[f.FileId] = f;
    }

    private static void CompareState<T>(
        string label,
        IDictionary<long, T> inMemory,
        IDictionary<long, T> rebuilt,
        Func<T, string?> nameSelector,
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
                    Message  = $"{label}: RootId {id} ('{nameSelector(value)}') exists in memory but not in rebuilt state."
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
                    Message  = $"{label}: RootId {id} ('{nameSelector(value)}') exists only in rebuilt state."
                });
            }
        }
    }

    private static void DetectDirCycle(
        long dirId,
        IReadOnlyDictionary<long, DirRecord> dirs,
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
                Message = $"Directory graph contains a cycle involving dirId {dirId}."
            });
            return;
        }

        if (!dirs.TryGetValue(dirId, out var dir))
        {
            visiting.Remove(dirId);
            return;
        }

        if (dir.ParentDirId is { } parentId && dirs.ContainsKey(parentId))
        {
            if (!visited.Contains(parentId))
                DetectDirCycle(parentId, dirs, visited, visiting, issues);
        }

        visiting.Remove(dirId);
        visited.Add(dirId);
    }
}