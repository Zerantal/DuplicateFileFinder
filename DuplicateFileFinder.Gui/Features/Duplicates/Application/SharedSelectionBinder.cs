namespace DuplicateFileFinder.Gui.Features.Duplicates.Application;

public sealed class SharedSelectionBinder<TLocalSelection>(
    DuplicateExplorerSelectionContext selectionContext,
    Func<TLocalSelection?> getLocalSelection,
    Func<TLocalSelection?, DuplicateExplorerSelectionContext.SelectionTarget?> toSharedSelection,
    Action<DuplicateExplorerSelectionContext.SelectionTarget?> applySharedSelection)
{
    private readonly DuplicateExplorerSelectionContext _selectionContext =
        selectionContext ?? throw new ArgumentNullException(nameof(selectionContext));

    private readonly Func<TLocalSelection?> _getLocalSelection =
        getLocalSelection ?? throw new ArgumentNullException(nameof(getLocalSelection));

    private readonly Func<TLocalSelection?, DuplicateExplorerSelectionContext.SelectionTarget?> _toSharedSelection =
        toSharedSelection ?? throw new ArgumentNullException(nameof(toSharedSelection));

    private readonly Action<DuplicateExplorerSelectionContext.SelectionTarget?> _applySharedSelection =
        applySharedSelection ?? throw new ArgumentNullException(nameof(applySharedSelection));

    private bool _syncing;

    public void PublishFromLocal()
    {
        if (_syncing)
            return;

        var next = _toSharedSelection(_getLocalSelection());
        if (Nullable.Equals(_selectionContext.Current, next))
            return;

        _selectionContext.SetCurrent(next);
    }

    public void ApplyFromShared()
    {
        if (_syncing)
            return;

        _syncing = true;
        try
        {
            _applySharedSelection(_selectionContext.Current);
        }
        finally
        {
            _syncing = false;
        }
    }
}
