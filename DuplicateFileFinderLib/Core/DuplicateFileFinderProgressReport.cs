namespace DuplicateFileFinderLib.Core;

public class DuplicateFileFinderProgressReport(bool isRunning = true)
{
    private readonly double _percentComplete;

    // current progress (between 0 and 1) of task
    public double PercentComplete
    {
        get => _percentComplete;
        init => _percentComplete = Math.Max(0, Math.Min(1, value));
    }

    public bool IsRunning { get; } = isRunning;

    public string StatusMessage { get; init; } = string.Empty;
}