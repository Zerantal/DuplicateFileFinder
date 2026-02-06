using System.Text;

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Util;

using NLog;

using FileRecordV2 = DuplicateFileFinderLib.Repository.Storage.Models.FileRecordV2;

namespace DuplicateFileFinderLib.Repository.Core.Scan;

// using BaseLineMapValue = (long id, string name, ScanEntryStatus status, long lastSeen);

internal record BaseLineMapValue(string name, ScanEntryStatus status, long lastSeen);
internal sealed record BaseLineDirMapValue(
    DirId dirId,
    string name,
    ScanEntryStatus status,
    long lastSeen) : BaseLineMapValue(name, status, lastSeen);
internal sealed record BaseLineFileMapValue(
    FileId fileId,
    string name,
    ScanEntryStatus status,
    long lastSeen) : BaseLineMapValue(name, status, lastSeen);


internal sealed class BaselineIndex
{
    private static readonly Logger s_log = LogManager.GetCurrentClassLogger();

    private long _baselineNameCollisions;
    private const int BaselineCollisionTraceLimit = 25;

    private readonly Dictionary<DirId, Dictionary<string, BaseLineDirMapValue>> _childDirsByParentName = new();
    private readonly Dictionary<FileId, Dictionary<string, BaseLineFileMapValue>> _childFilesByDirName = new();
    private readonly Dictionary<FileId, FileRecordV2> _fileById = new();

    public BaselineIndex(ScanRootSnapshotView? view)
    {
        if (view is null)
            return;

        // dirs
        foreach (var d in view.Dirs)
        {
            if (d.Status == ScanEntryStatus.None)
                continue;

            if (d.ParentDirId < 0)
                continue;

            var name = d.NameStrIdx >= 0 ? view.StringPool.GetString(d.NameStrIdx) : string.Empty;
            if (string.IsNullOrEmpty(name))
                continue;

            if (!_childDirsByParentName.TryGetValue(d.ParentDirId, out var byName))
            {
                byName = new Dictionary<string, BaseLineDirMapValue>(PathUtils.PathComparer);
                _childDirsByParentName.Add(d.ParentDirId, byName);
            }

            var cand = new BaseLineDirMapValue(d.DirId, name, d.Status, d.LastSeenScanSequence);

            if (!byName.TryGetValue(name, out var existing))
            {
                byName[name] = cand;
                continue;
            }

            // Collision handling (integrity logging + deterministic choice)
            var keepCandidate = PreferCandidate(existing, cand);
            if (keepCandidate)
                byName[name] = cand;

            RecordCollision(
                kind: "dir",
                parentOrDirId: d.ParentDirId,
                name: name,
                existing: keepCandidate ? existing : cand,   // the one not kept
                kept: keepCandidate ? cand : existing);
        }

        // files
        foreach (var f in view.Files)
        {
            if (f.Status == ScanEntryStatus.None)
                continue;

            _fileById[f.FileId] = f;

            var name = f.NameStrIdx >= 0 ? view.StringPool.GetString(f.NameStrIdx) : string.Empty;
            if (string.IsNullOrEmpty(name))
                continue;

            if (!_childFilesByDirName.TryGetValue(f.DirId, out var byName))
            {
                byName = new Dictionary<string, BaseLineFileMapValue>(PathUtils.PathComparer);
                _childFilesByDirName.Add(f.DirId, byName);
            }

            var cand = new BaseLineFileMapValue(f.FileId, name, f.Status, f.LastSeenScanSequence);

            if (!byName.TryGetValue(name, out var existing))
            {
                byName[name] = cand;
                continue;
            }

            // Collision handling (integrity logging + deterministic choice)
            var keepCandidate = PreferCandidate(existing, cand);
            if (keepCandidate)
                byName[name] = cand;

            RecordCollision(
                kind: "file",
                parentOrDirId: f.DirId,
                name: name,
                existing: keepCandidate ? existing : cand,
                kept: keepCandidate ? cand : existing);
        }
    }

    public bool TryGetChildDirMap(DirId parentDirId, out Dictionary<string, BaseLineDirMapValue> map)
        => _childDirsByParentName.TryGetValue(parentDirId, out map!);

    public bool TryGetChildFileMap(DirId dirId, out Dictionary<string, BaseLineFileMapValue> map)
        => _childFilesByDirName.TryGetValue(dirId, out map!);

    public bool TryGetBaselineFile(FileId fileId, out FileRecordV2 file)
        => _fileById.TryGetValue(fileId, out file);

    private static bool IsDeleted(ScanEntryStatus s) => s == ScanEntryStatus.Deleted;

    /// <summary>
    /// Deterministic collision resolver.
    /// Prefer:
    ///  1) non-deleted over deleted
    ///  2) higher lastSeen
    ///  3) otherwise keep existing
    /// </summary>
    private static bool PreferCandidate(in BaseLineMapValue existing, in BaseLineMapValue candidate)
    {
        var existingDeleted = IsDeleted(existing.status);
        var candDeleted = IsDeleted(candidate.status);

        // non-deleted beats deleted
        if (existingDeleted != candDeleted)
            return existingDeleted; // if existing is deleted and candidate isn't -> prefer candidate

        // newer beats older
        if (candidate.lastSeen != existing.lastSeen)
            return candidate.lastSeen > existing.lastSeen;

        // tie: keep existing (stable)
        return false;
    }

    private void RecordCollision(
        string kind,
        long parentOrDirId,
        string name,
        in BaseLineMapValue existing,
        in BaseLineMapValue kept)
    {
        var collisions = Interlocked.Increment(ref _baselineNameCollisions);

        // Lightweight counters you can grep in logs.
        TimingLog.Counter("baseline_name_collisions_total");
        TimingLog.Counter(kind == "dir" ? "baseline_dir_name_collisions" : "baseline_file_name_collisions");

        // Only trace first N collisions to avoid log spam.
        if (collisions <= BaselineCollisionTraceLimit)
        {
            var traceStrBuilder = new StringBuilder($"Baseline collision ({kind}): container={parentOrDirId} name='{name}' ");
            if (existing is BaseLineDirMapValue existingDir && kept is BaseLineDirMapValue keptDir)
                traceStrBuilder.Append($"keptId={keptDir.dirId} keptStatus={keptDir.status} keptLastSeen={keptDir.lastSeen} " +
                    $"droppedId={existingDir.dirId} droppedStatus={existingDir.status} droppedLastSeen={existingDir.lastSeen}");
            else if (existing is BaseLineFileMapValue existingFile && kept is BaseLineFileMapValue keptFile)
                traceStrBuilder.Append($"keptId={keptFile.fileId} keptStatus={keptFile.status} keptLastSeen={keptFile.lastSeen} " +
                    $"droppedId={existingFile.fileId} droppedStatus={existingFile.status} droppedLastSeen={existingFile.lastSeen}");

            s_log.Trace(traceStrBuilder.ToString());
        }
    }
}
