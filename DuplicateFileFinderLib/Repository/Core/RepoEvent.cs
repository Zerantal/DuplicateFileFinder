
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Core;

public abstract record RepoEvent
{
    public long Generation      { get; init; }
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

// After a scan-root snapshot is committed (new snapshot persisted + meta persisted)
public sealed record ScanRootSnapshotCommittedEvent : RepoEvent
{
    public required long ScanRootId { get; init; }
    
    public required RepoSnapshotView RepoSnapshotView { get; init; }
}
