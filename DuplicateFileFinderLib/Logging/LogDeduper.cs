// Logging/LogDeduper.cs

using System.Collections.Concurrent;

namespace DuplicateFileFinderLib.Logging;

public sealed class LogDeduper(TimeSpan window)
{
    private readonly ConcurrentDictionary<string, DateTime> _last = new();

    public bool ShouldLog(string key)
    {
        var now = DateTime.UtcNow;
        var last = _last.GetOrAdd(key, _ => DateTime.MinValue);
        if (now - last > window) { _last[key] = now; return true; }
        return false;
    }
}