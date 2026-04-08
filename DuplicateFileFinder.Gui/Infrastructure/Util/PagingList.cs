using Avalonia.Collections;
using Avalonia.Threading;

namespace DuplicateFileFinder.Gui.Infrastructure.Util;

public sealed class PagingList<T> : AvaloniaList<T>
{
    private readonly int _pageSize;
    private readonly Func<int, int, (int total, T[] items)> _fetchPage;

    private int _total = -1; // -1 => unknown
    private int _requestedThrough = -1;

    private bool _isFetching;
    private int _nextOffset;

    // Incremented on every logical reset so stale in-flight fetches can be ignored.
    private int _version;

    // open-ended paging sentinel.
    // true  => keep fetching while we haven't satisfied requestedThrough
    // false => backend said "end-of-data"
    private bool _hasMore;

    public PagingList(int pageSize, Func<int, int, (int total, T[] items)> fetchPage)
    {
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        _pageSize = pageSize;
        _fetchPage = fetchPage ?? throw new ArgumentNullException(nameof(fetchPage));
        _hasMore = true;
    }

    public int Total => _total;

    public void Reset()
    {
        _version++;
        _total = -1;
        _requestedThrough = -1;
        _isFetching = false;
        _nextOffset = 0;
        _hasMore = true;

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
        var version = _version;

        try
        {
            while (Count <= _requestedThrough && _hasMore)
            {
                if (version != _version)
                    return;

                var offset = _nextOffset;
                var (total, items) = _fetchPage(offset, _pageSize);

                if (version != _version)
                    return;

                if (total >= 0)
                {
                    _total = total;
                    _hasMore = _nextOffset < _total;
                }

                if (items.Length == 0)
                {
                    // Backend says “no items”: definite end-of-data.
                    _hasMore = false;
                    if (_total < 0)
                        _total = Count;
                    break;
                }

                _nextOffset += items.Length;

                // Must mutate the list on the UI thread so ItemsRepeater updates reliably.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (version != _version)
                        return;

                    AddRange(items);
                }, DispatcherPriority.Background);

                if (version != _version)
                    return;

                // If backend didn't provide totals, treat a short page as end-of-data.
                if (_total < 0 && items.Length < _pageSize)
                {
                    _hasMore = false;
                    _total = Count;
                    break;
                }

                if (_total >= 0 && _nextOffset >= _total)
                {
                    _hasMore = false;
                    break;
                }
            }
        }
        finally
        {
            _isFetching = false;
        }
    }
}
