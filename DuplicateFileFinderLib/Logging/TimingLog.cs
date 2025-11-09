// Logging/TimingLog.cs

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using NLog;

namespace DuplicateFileFinderLib.Logging;

public sealed class TimingLog : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static readonly AsyncLocal<PhaseContext?> CurrentPhase = new();
    private readonly string? _detail;

    private readonly string _operation;
    private readonly Stopwatch _sw;

    private TimingLog(string operation, string? detail, bool asPhase)
    {
        _operation = operation;
        _detail = detail;

        if (asPhase)
        {
            // New phase scope; nested phases overwrite AsyncLocal for this flow.
            var ctx = new PhaseContext(operation, detail);
            CurrentPhase.Value = ctx;
            _sw = ctx.Sw; // share the stopwatch
            Log.Debug("Started {operation}{detail}", _operation, FormatDetail(detail));
        }
        else
        {
            _sw = Stopwatch.StartNew();
            Log.Debug("Started {operation}{detail}", _operation, FormatDetail(detail));
        }
    }

    public void Dispose()
    {
        _sw.Stop();

        // If this scope owns the phase context, emit counters with the timing.
        var ctx = CurrentPhase.Value;
        if (ctx != null && ReferenceEquals(ctx.Sw, _sw))
        {
            var sb = new StringBuilder();
            if (!ctx.Counters.IsEmpty)
                foreach (var kv in ctx.Counters.OrderBy(k => k.Key))
                    sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);

            Log.Info("Completed {operation}{detail} in {elapsedMs:N0} ms{counters}",
                _operation,
                FormatDetail(_detail),
                _sw.Elapsed.TotalMilliseconds,
                sb.Length == 0 ? "" : sb.ToString());

            // Clear the phase for this async flow
            CurrentPhase.Value = null;
        }
        else
        {
            Log.Info("Completed {operation}{detail} in {elapsedMs:N0} ms",
                _operation,
                FormatDetail(_detail),
                _sw.Elapsed.TotalMilliseconds);
        }
    }

    private static string FormatDetail(string? d)
    {
        return string.IsNullOrEmpty(d) ? "" : $" ({d})";
    }

    /// <summary>Starts timing for an arbitrary operation. Use in a using-block.</summary>
    public static TimingLog Start(string operation, string? detail = null)
    {
        return new TimingLog(operation, detail, false);
    }

    /// <summary>Starts a "phase" timing scope that can collect counters via TimingLog.Counter(...).</summary>
    public static TimingLog StartPhase(string phaseName, string? detail = null)
    {
        return new TimingLog(phaseName, detail, true);
    }

    /// <summary>Convenience for enum phases.</summary>
    public static TimingLog StartPhase(Enum phase, string? detail = null)
    {
        return StartPhase(phase.ToString(), detail);
    }

    /// <summary>Increment a named counter in the current phase.</summary>
    public static void Counter(string name, long delta = 1)
    {
        var ctx = CurrentPhase.Value;
        if (ctx is null) return; // no active phase
        ctx.Counters.AddOrUpdate(name, delta, (_, v) => v + delta);
    }

    // Holds per-async-flow phase context (name + counters)
    private sealed class PhaseContext(string op, string? detail)
    {
        public string Operation { get; } = op;
        public string? Detail { get; } = detail;
        public Stopwatch Sw { get; } = Stopwatch.StartNew();
        public ConcurrentDictionary<string, long> Counters { get; } = new();
    }
}