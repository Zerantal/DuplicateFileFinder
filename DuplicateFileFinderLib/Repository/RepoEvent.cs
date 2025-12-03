using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository;

// public enum RepoEventKind
// {
//     DeltaCommitted,
//     ScanRunCompleted,
//     Compacted,
//     Opened 
// }

// public sealed record RepoEvent
// {
//     public required RepoEventKind Kind { get; init; }
//
//     // Common
//     public required long Generation { get; init; }
//     public required long NextLogSequence { get; init; }
//
//     // For DeltaCommitted
//     public long? ScanSequence { get; init; }
//     public RepoDelta? Delta { get; init; }
//
//     // For ScanRunCompleted
//     public ScanRun? ScanRun { get; init; }
//
//     // For Opened / Compacted (optional: snapshot hook)
//     public RepoViewSnapshot? Snapshot { get; init; }
// }


public abstract record RepoEvent
{
    public long Generation      { get; init; }
    public long NextLogSequence { get; init; }
}

// Initial bootstrap / “opened at current state”
public sealed record BootstrapEvent : RepoEvent
{
    public required RepoViewSnapshot Snapshot { get; init; }
}

// After a delta is committed and applied to in-memory state
public sealed record DeltaCommittedEvent : RepoEvent
{
    public required long      ScanSequence { get; init; }
    public required RepoDelta Delta        { get; init; }
}

// After a scan run completes (success/failure)
public sealed record ScanRunCompletedEvent : RepoEvent
{
    public required ScanRun Run { get; init; }
}

// After compaction writes new snapshots & bumps generation
public sealed record CompactedEvent : RepoEvent
{
    public required RepoViewSnapshot Snapshot { get; init; }
}
