using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Core;

public sealed class ThrottledProgress(
    IProgress<DuplicateFileFinderProgressReport> inner,
    TimeSpan? minInterval = null,
    double minDelta = 0.01)
    : IProgress<DuplicateFileFinderProgressReport>
{
    private readonly Lock _gate = new();
    private readonly TimeSpan _minInterval = minInterval ?? TimeSpan.FromMilliseconds(120);
    private double _lastPercent = double.NaN;

    private DateTime _lastSent = DateTime.MinValue;
    private bool _sentAny;
    
    public void Report(DuplicateFileFinderProgressReport value)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var pct = value.PercentComplete;

            var isTerminal = pct >= 1.0 || !value.IsRunning;
            var timeOk = now - _lastSent >= _minInterval;
            var deltaOk = double.IsNaN(_lastPercent) || Math.Abs(pct - _lastPercent) >= minDelta;

            if (!_sentAny || isTerminal || timeOk || deltaOk)
            {
                inner.Report(value);
                _lastSent = now;
                _lastPercent = pct;
                _sentAny = true;
            }
        }
    }
}