// Logging/TimingLog.cs

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

using NLog;

namespace DuplicateFileFinderLib.Logging;

public sealed class TimingLog : IDisposable
{
    private static readonly Logger _log = LogManager.GetCurrentClassLogger();

    // One stack per async flow. Top = current scope.
    private static readonly AsyncLocal<Stack<PhaseContext>?> _scopeStack = new();

    // global dictionary of counter formatters
    private static ConcurrentDictionary<string, Func<long, string>> CounterFormatter { get; } = new();

    private TimingLog(string operation, string? detail)
    {
        var stack = _scopeStack.Value ??= new Stack<PhaseContext>(4);
        var ctx = new PhaseContext(operation, detail);
        stack.Push(ctx);

        if (detail is null)
            _log.Debug("Started {operation}", operation);

        else
        {
            _log.Debug("Started {operation} ({detail})", operation, detail);
        }
    }

    public void Dispose()
    {
        var stack = _scopeStack.Value;
        if (stack is null || stack.Count == 0)
            return;

        var ctx = stack.Pop();
        ctx.Sw.Stop();

        var counters = BuildCounters(ctx.Counters);
        if (ctx.Detail is null)
            _log.Info("Completed {operation} in {elapsedMs:N0} ms{counters}",
                ctx.Operation, ctx.Sw.Elapsed.TotalMilliseconds, counters);
        else
            _log.Info("Completed {operation} ({detail}) in {elapsedMs:N0} ms{counters}",
                ctx.Operation, ctx.Detail, ctx.Sw.Elapsed.TotalMilliseconds, counters);

        // If the stack is empty, clear it so downstream awaits don’t hold onto objects.
        if (stack.Count == 0)
            _scopeStack.Value = null;
    }


    /// <summary>Starts timing for an arbitrary operation. Use in a using-block.</summary>
    public static TimingLog Start(string operation, string? detail = null)
        => new(operation, Normalize(detail));

    /// <summary>Starts a "phase" timing scope that can collect counters via TimingLog.Counter(...).</summary>
    public static TimingLog StartPhase(string phaseName, string? detail = null)
        => new(phaseName, Normalize(detail));


    /// <summary>Convenience for enum phases.</summary>
    public static TimingLog StartPhase(Enum phase, string? detail = null)
        => StartPhase(phase.ToString(), detail);

    /// <summary>Increment a named counter in the current phase.</summary>
    public static void Counter(string name, long delta = 1)
    {
        var stack = _scopeStack.Value;
        if (stack is null || stack.Count == 0)
            return;
        var ctx = stack.Peek();
        ctx.Counters.AddOrUpdate(name, delta, (_, v) => v + delta);
    }

    public static void AddCounterFormatter(string name, Func<long, string> formatter)
    {
        CounterFormatter[name] = formatter;
    }

    // ---- helpers ----

    private static string? Normalize(string? d) => string.IsNullOrWhiteSpace(d) ? null : d;

    private static string BuildCounters(ConcurrentDictionary<string, long> counters)
    {
        if (counters.IsEmpty)
            return string.Empty;
        var sb = new StringBuilder();
        foreach (var kv in counters.OrderBy(k => k.Key))
        {
            string value;
            if (CounterFormatter.TryGetValue(kv.Key, out var formatter))
                value = formatter(kv.Value);
            else
                value = kv.Value.ToString();
            sb.Append(' ').Append(kv.Key).Append('=').Append(value);
        }

        return sb.ToString();
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
