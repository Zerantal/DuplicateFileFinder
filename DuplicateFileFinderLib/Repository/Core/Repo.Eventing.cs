using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Interfaces;

using NLog;

namespace DuplicateFileFinderLib.Repository.Core;

public sealed partial class Repo
{
    private static readonly Logger s_log = LogManager.GetCurrentClassLogger();

    private readonly List<IRepoEventSink> _eventSinks = new();

    public void RegisterEventSink(IRepoEventSink sink)
    {
        lock (_sync)
        {
            _eventSinks.Add(sink);
        }
    }

    public void RegisterEventSinkWithBootstrap(IRepoEventSink sink)
    {
        BootstrapEvent bootstrap;

        lock (_sync)
        {
            var snapshots = GetRepoSnapshotView();

            bootstrap = new BootstrapEvent
            {
                Generation = _meta.Generation,
                RepoSnapshotView = snapshots
            };

            _eventSinks.Add(sink);
        }

        sink.Post(bootstrap);
    }

    private void PublishEvent(RepoEvent evt)
    {
        s_log.Info("Publishing repo event: " + evt);

        IRepoEventSink[] sinks;
        lock (_sync)
        {
            sinks = _eventSinks.ToArray();
        }

        foreach (var sink in sinks)
            sink.Post(evt);
    }
}
