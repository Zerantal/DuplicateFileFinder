// DuplicateFileFinder.Gui/Infrastructure/Services/RepoUiEventRelayPlugin.cs

using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins;

namespace DuplicateFileFinder.Gui.Infrastructure.Services;

/// <summary>
/// Receives repo events on the plugin channel thread, then forwards selected ones to the UI thread.
/// </summary>
public sealed class RepoUiEventRelayPlugin(IUiDispatcher ui) : ChannelRepoPlugin
{
    private readonly IUiDispatcher _ui = ui ?? throw new ArgumentNullException(nameof(ui));

    public event EventHandler<RepoFileDeletedEvent>? FileDeleted;
    public event EventHandler<RepoDirDeletedEvent>? DirDeleted;
    public event EventHandler<RepoScanRootRemovedEvent>? ScanRootRemoved;

    public event EventHandler<ScanRootSnapshotReplacedEvent>? SnapshotReplaced;
    public event EventHandler<ScanRunFinalisedEvent>? ScanRunFinalised;
    public event EventHandler<ScanRootMetaChangedEvent>? ScanRootMetaChanged;

    protected override ValueTask OnRepoFileDeletedEventAsync(RepoFileDeletedEvent evt, CancellationToken ct)
    {
        RaiseOnUiThread(FileDeleted, evt);
        return  ValueTask.CompletedTask;
    }

    protected override ValueTask OnRepoDirDeletedEventAsync(RepoDirDeletedEvent evt, CancellationToken ct)
    {
        RaiseOnUiThread(DirDeleted, evt);
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnRepoScanRootRemovedEventAsync(RepoScanRootRemovedEvent evt, CancellationToken ct)
    {
        RaiseOnUiThread(ScanRootRemoved, evt);
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnScanRootSnapshotReplacedEventAsync(ScanRootSnapshotReplacedEvent evt, CancellationToken ct)
    {
        RaiseOnUiThread(SnapshotReplaced, evt);
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnScanRunFinalisedEventAsync(ScanRunFinalisedEvent evt, CancellationToken ct)
    {
        RaiseOnUiThread(ScanRunFinalised, evt);
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnScanRootMetaChangedEventAsync(ScanRootMetaChangedEvent evt, CancellationToken ct)
    {
        RaiseOnUiThread(ScanRootMetaChanged, evt);
        return ValueTask.CompletedTask;
    }

    private void RaiseOnUiThread<T>(EventHandler<T>? handler, T evt) where T : RepoEvent
    {
        if (handler is null)
            return;

        // Capture invocation list now; avoid races if handlers unsubscribe before post runs.
        _ui.Post(() => handler.Invoke(this, evt));
    }

    protected override ValueTask DisposeAsyncCore()
    {
        // break handler chains
        FileDeleted = null;
        DirDeleted = null;
        ScanRootRemoved = null;
        SnapshotReplaced = null;
        ScanRunFinalised = null;
        ScanRootMetaChanged = null;

        return base.DisposeAsyncCore();
    }
}

