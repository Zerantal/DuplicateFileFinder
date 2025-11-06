namespace DuplicateFileFinderLib.Core;

public enum ScanPhase
{
    Preparing,
    Enumerating,
    Hashing,
    Grouping,
    Committing,
    RecomputingAggregates,
    Completed
}

public class DuplicateFileFinderProgressReport
{
    private readonly double _percentComplete;

    // current progress (between 0 and 1) of task
    public double PercentComplete
    {
        get => _percentComplete;
        init => _percentComplete = Math.Max(0, Math.Min(1, value));
    }

    public string StatusMessage { get; init; } = string.Empty;

    public bool IsRunning { get; init; } = true;
    public ScanPhase Phase { get; init; }
    public bool IsIndeterminate { get; init; } // true when we don't know totals yet
    public long Processed { get; init; } // items processed in phase
    public long Total { get; init; } // total known items in phase (0 if unknown)
}