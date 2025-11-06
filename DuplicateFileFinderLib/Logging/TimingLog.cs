using System.Diagnostics;
using NLog;

namespace DuplicateFileFinderLib.Logging;

public sealed class TimingLog : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly string _operation;
    private readonly Stopwatch _sw;
    private readonly string? _detail;

    private TimingLog(string operation, string? detail)
    {
        _operation = operation;
        _detail = detail;
        _sw = Stopwatch.StartNew();

        Log.Debug("Started {operation}{detail}", _operation,
            string.IsNullOrEmpty(_detail) ? "" : $" ({_detail})");
    }

    /// <summary>
    /// Starts timing for a given operation.  Use in a using-block.
    /// </summary>
    public static TimingLog Start(string operation, string? detail = null)
        => new(operation, detail);

    public void Dispose()
    {
        _sw.Stop();
        Log.Info("Completed {operation}{detail} in {elapsedMs:N0} ms",
            _operation,
            string.IsNullOrEmpty(_detail) ? "" : $" ({_detail})",
            _sw.Elapsed.TotalMilliseconds);
    }
}