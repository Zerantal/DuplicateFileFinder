using CommunityToolkit.Mvvm.ComponentModel;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Application;

public sealed class DuplicateExplorerSelectionContext : ObservableObject
{
    private SelectionTarget? _current;

    public SelectionTarget? Current
    {
        get => _current;
        set => SetCurrent(value);
    }

    public void SetCurrent(SelectionTarget? value, bool forceNotify = false)
    {
        if (!forceNotify && Nullable.Equals(_current, value))
            return;

        _current = value;

        OnPropertyChanged(nameof(Current));
    }

    public readonly record struct SelectionTarget(
        SelectionKind Kind,
        DirId? DirId,
        FileId? FileId,
        DirId? ParentDirId)
    {
        public static SelectionTarget ForDirectory(DirId dirId, DirId? parentDirId = null)
            => new(SelectionKind.Directory, dirId, null, parentDirId);

        public static SelectionTarget ForFile(FileId fileId, DirId? parentDirId)
            => new(SelectionKind.File, null, fileId, parentDirId);

        public static SelectionTarget ForSyntheticDirectoryBucket(DirId parentDirId)
            => new(SelectionKind.SyntheticDirectoryBucket, null, null, parentDirId);
    }

    public enum SelectionKind
    {
        None = 0,
        Directory = 1,
        File = 2,
        SyntheticDirectoryBucket = 3
    }
}
