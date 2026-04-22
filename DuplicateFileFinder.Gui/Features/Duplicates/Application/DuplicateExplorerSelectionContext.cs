using System.Collections.Immutable;

using CommunityToolkit.Mvvm.ComponentModel;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Application;

public sealed class DuplicateExplorerSelectionContext : ObservableObject
{
    private SelectionTarget? _current;
    private int _notificationSuppressionCount;
    private bool _currentChangedDuringSuppression;

    public SelectionTarget? Current
    {
        get => _current;
        set => SetCurrent(value);
    }

    public void SetCurrent(SelectionTarget? value, bool forceNotify = false)
    {
        var changed = !Nullable.Equals(_current, value);

        if (!changed && !forceNotify)
            return;

        _current = value;

        if (_notificationSuppressionCount > 0)
        {
            if (changed || forceNotify)
                _currentChangedDuringSuppression = true;

            return;
        }

        OnPropertyChanged(nameof(Current));
    }

    public IDisposable SuspendNotifications()
    {
        _notificationSuppressionCount++;
        return new NotificationSuspension(this);
    }

    private void ResumeNotifications()
    {
        if (_notificationSuppressionCount == 0)
            throw new InvalidOperationException("Notifications are not currently suspended.");

        _notificationSuppressionCount--;

        if (_notificationSuppressionCount > 0)
            return;

        if (!_currentChangedDuringSuppression)
            return;

        _currentChangedDuringSuppression = false;
        OnPropertyChanged(nameof(Current));
    }

    private sealed class NotificationSuspension(DuplicateExplorerSelectionContext owner) : IDisposable
    {
        private DuplicateExplorerSelectionContext? _owner = owner;

        public void Dispose()
        {
            var owner = _owner;
            if (owner is null)
                return;

            _owner = null;
            owner.ResumeNotifications();
        }
    }

    public readonly record struct SelectionTarget(
        SelectionKind Kind,
        FileId? FileId,
        ImmutableArray<DirId> DirectoryChain)
    {
        public DirId? ContextDirectoryId =>
            DirectoryChain.Length > 0 ? DirectoryChain[^1] : null;

        public DirId? ParentOfContextDirectoryId =>
            DirectoryChain.Length >= 2 ? DirectoryChain[^2] : null;

        public static SelectionTarget ForDirectory(ImmutableArray<DirId> directoryChain) =>
            new(SelectionKind.Directory, null, directoryChain);

        public static SelectionTarget ForFile(FileId fileId, ImmutableArray<DirId> directoryChain) =>
            new(SelectionKind.File, fileId, directoryChain);

        public static SelectionTarget ForSyntheticDirectoryBucket(ImmutableArray<DirId> directoryChain) =>
            new(SelectionKind.SyntheticDirectoryBucket, null, directoryChain);
    }

    public enum SelectionKind
    {
        None = 0,
        Directory = 1,
        File = 2,
        SyntheticDirectoryBucket = 3
    }
}
