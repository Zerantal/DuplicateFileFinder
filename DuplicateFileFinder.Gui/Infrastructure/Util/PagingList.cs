using Avalonia.Collections;
using Avalonia.Threading;

namespace DuplicateFileFinder.Gui.Infrastructure.Util;

public sealed class PagingList<T> : AvaloniaList<T>
{
    private readonly int _pageSize;
    private readonly Func<int, int, (int total, T[] items)> _fetchPage;

    private int _total = -1; // unknown until first fetch
    private int _requestedThrough = -1;

    private bool _isFetching;
    private int _nextOffset;

    public PagingList(int pageSize, Func<int, int, (int total, T[] items)> fetchPage)
    {
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        _pageSize = pageSize;
        _fetchPage = fetchPage ?? throw new ArgumentNullException(nameof(fetchPage));
    }

    public int Total => _total;

    public void Reset()
    {
        _total = int.MaxValue;
        _requestedThrough = -1;
        _isFetching = false;
        _nextOffset = 0;

        // Clear must happen on UI thread.
        if (Dispatcher.UIThread.CheckAccess())
            Clear();
        else
            Dispatcher.UIThread.Post(Clear, DispatcherPriority.Background);
    }

    public void EnsureLoadedThroughIndex(int index)
    {
        if (index <= _requestedThrough)
            return;

        _requestedThrough = index;

        // Fire and forget (UI paging). Internal re-entrancy guard prevents overlap.
        _ = FetchUntilSatisfiedAsync();
    }

    private async Task FetchUntilSatisfiedAsync()
    {
        if (_isFetching)
            return;

        _isFetching = true;
        try
        {
            while (Count <= _requestedThrough && (_total < 0 || _nextOffset < _total))
            {
                var (total, items) = _fetchPage(_nextOffset, _pageSize);

                if (total >= 0)
                    _total = total;

                if (items.Length == 0)
                {
                    // If backend says “no items”, treat as end-of-data
                    _total = Count;
                    break;
                }

                _nextOffset += items.Length;

                // Must mutate the list on the UI thread so ItemsRepeater updates reliably.
                await Dispatcher.UIThread.InvokeAsync(() => AddRange(items), DispatcherPriority.Background);
            }
        }
        finally
        {
            _isFetching = false;
        }
    }
}
