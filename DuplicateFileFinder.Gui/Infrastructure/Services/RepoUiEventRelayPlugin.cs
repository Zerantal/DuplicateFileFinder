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

    protected override void OnRepoFileDeletedEvent(RepoFileDeletedEvent evt)
        => RaiseOnUiThread(FileDeleted, evt);

    protected override void OnRepoDirDeletedEvent(RepoDirDeletedEvent evt)
        => RaiseOnUiThread(DirDeleted, evt);

    protected override void OnRepoScanRootRemovedEvent(RepoScanRootRemovedEvent evt)
        => RaiseOnUiThread(ScanRootRemoved, evt);

    protected override void OnScanRootSnapshotReplacedEvent(ScanRootSnapshotReplacedEvent evt)
        => RaiseOnUiThread(SnapshotReplaced, evt);

    protected override void OnScanRunFinalisedEvent(ScanRunFinalisedEvent evt)
        => RaiseOnUiThread(ScanRunFinalised, evt);

    protected override void OnScanRootMetaChangedEvent(ScanRootMetaChangedEvent evt)
        => RaiseOnUiThread(ScanRootMetaChanged, evt);

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

