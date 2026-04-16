using System.Diagnostics.CodeAnalysis;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Core.RepoEventing;

public abstract record RepoEvent
{
    public long Generation { get; init; }
}

// Used for tracked generations. i.e., to ensure index rebuilt event is raised
// after all plugins have handled a particular generation
public abstract record IndexGenerationTrackedEvent : RepoEvent
{
    public required ScanRootId? ScanRootId { get; init; }
}

// Initial bootstrap / “opened at current state”
public sealed record BootstrapEvent : RepoEvent
{
    public required RepoSnapshotView RepoSnapshotView { get; init; }
}

// After a scan run is finalised (success/failure/cancel)
public sealed record ScanRunFinalisedEvent : RepoEvent
{
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public required ScanRun Run { get; init; }
}

public sealed record ScanRootMetaChangedEvent : RepoEvent
{
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public required ScanRoot UpdatedScanRoot { get; init; }
}

public enum RepoSnapshotCommitReason
{
    ScanCompleted,
    Maintenance,

    Mutation
}

/// <summary>
/// Used when the scan-root snapshot is materially replaced (scan/import/repair),
/// i.e. a change that index plugins should rebuild from.
/// </summary>
public sealed record ScanRootSnapshotReplacedEvent : IndexGenerationTrackedEvent
{
    public required RepoSnapshotView RepoSnapshotView { get; init; }
    public required RepoSnapshotCommitReason Reason { get; init; }
}

/// <summary>
/// A single file was deleted (marked deleted in snapshot + persisted).
/// Consumers should do incremental removal (UI, indexes) without full rebuild.
/// </summary>
public sealed record RepoFileDeletedEvent : IndexGenerationTrackedEvent
{
    public required FileHandle File { get; init; }
    public required FileId FileId { get; init; }
}

/// <summary>
/// A directory subtree was deleted (marked deleted in snapshot + persisted).
/// Includes counts to update aggregates quickly.
/// </summary>
public sealed record RepoDirDeletedEvent : IndexGenerationTrackedEvent
{
    public required DirHandle Dir { get; init; }

    public required DirId[] DeletedDirIds { get; init; }
    public required (FileId FileId, FileHandle FileHandle)[] DeletedFiles { get; init; }

    public int DeletedDirsCount => DeletedDirIds.Length;
    public int DeletedFilesCount => DeletedFiles.Length;
}

/// <summary>
/// A scan-root entry was removed/tombstoned.
/// </summary>
public sealed record RepoScanRootRemovedEvent : IndexGenerationTrackedEvent
{
    public ScanRootId ScanRootIdValue => ScanRootId!.Value;

    [SetsRequiredMembers]
    public RepoScanRootRemovedEvent(long generation, ScanRootId scanRootId)
    {
        Generation = generation;
        ScanRootId = scanRootId;
    }
}
