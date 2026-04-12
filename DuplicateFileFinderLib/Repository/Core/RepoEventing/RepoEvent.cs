using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Core.RepoEventing;

public abstract record RepoEvent
{
    public long Generation { get; init; }
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

    // Reserved for the future “don’t rebuild everything” path.
    Mutation
}

/// <summary>
/// Used when the scan-root snapshot is materially replaced (scan/import/repair),
/// i.e. a change that index plugins should rebuild from.
/// </summary>
public sealed record ScanRootSnapshotReplacedEvent : RepoEvent
{
    public required ScanRootId ScanRootId { get; init; }
    public required RepoSnapshotView RepoSnapshotView { get; init; }
    public required RepoSnapshotCommitReason Reason { get; init; }
}

/// <summary>
/// A single file was deleted (marked deleted in snapshot + persisted).
/// Consumers should do incremental removal (UI, indexes) without full rebuild.
/// </summary>
public sealed record RepoFileDeletedEvent : RepoEvent
{
    public required FileHandle File { get; init; }
    public required FileId FileId { get; init; }
}

/// <summary>
/// A directory subtree was deleted (marked deleted in snapshot + persisted).
/// Includes counts to update aggregates quickly.
/// </summary>
public sealed record RepoDirDeletedEvent : RepoEvent
{
    public required DirHandle Dir { get; init; }

    public required DirId[] DeletedDirIds { get; init; }
    public required FileId[] DeletedFileIds { get; init; }

    public int DeletedDirs => DeletedDirIds.Length;
    public int DeletedFiles => DeletedFileIds.Length;
}

/// <summary>
/// A scan-root entry was removed/tombstoned.
/// </summary>
public sealed record RepoScanRootRemovedEvent : RepoEvent
{
    public required ScanRootId ScanRootId { get; init; }
}
