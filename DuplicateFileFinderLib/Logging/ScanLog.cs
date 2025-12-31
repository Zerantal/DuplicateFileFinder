using NLog;

namespace DuplicateFileFinderLib.Logging;

public static class ScanLog
{
    public static IDisposable BeginScanScope(string rootPath)
    {
        var scanId = Guid.NewGuid().ToString("N");
        var d1 = ScopeContext.PushProperty("scanId", scanId);
        var d2 = ScopeContext.PushProperty("root", rootPath);
        var d3 = ScopeContext.PushNestedState($"scan:{scanId}");
        return new AggregateDisposable(d3, d2, d1);
    }

    private sealed class AggregateDisposable(params IDisposable[] items) : IDisposable
    {
        public void Dispose() { for (int i = items.Length - 1; i >= 0; i--) items[i].Dispose(); }
    }
}
