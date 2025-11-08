using DuplicateFileFinderLib.Util;
using NLog;

namespace DuplicateFileFinderLib.Logging;

public sealed class PhaseScope : IDisposable
{
    private readonly IDisposable _p;
    private readonly IDisposable _n;

    private PhaseScope(ScanPhase phase)
    {
        var name = phase.ToString();
        _p = ScopeContext.PushProperty("phase", name);
        _n = ScopeContext.PushNestedState($"phase:{name}");
    }

    public static PhaseScope Begin(ScanPhase phase) => new(phase);

    public void Dispose()
    {
        _n.Dispose();
        _p.Dispose();
    }
}