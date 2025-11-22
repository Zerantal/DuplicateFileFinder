using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DuplicateFileFinder.Gui.Util;

/// <summary>
///     ObservableCollection that:
///     - Supports bulk updates with BeginUpdate/EndUpdate (single Reset notification).
///     - Maintains an internal dictionary keyed by TKey for O(1) lookup/update/remove.
///     Keys must be stable for the lifetime of the item (do not mutate the key-defining fields).
/// </summary>
public class BulkKeyedObservableCollection<TKey, TItem> : ObservableCollection<TItem>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TItem> _index;
    private readonly Func<TItem, TKey> _keySelector;

    private bool _suppressNotification;

    public BulkKeyedObservableCollection(Func<TItem, TKey> keySelector, IEqualityComparer<TKey>? comparer = null)
    {
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        _index = new Dictionary<TKey, TItem>(comparer ?? EqualityComparer<TKey>.Default);
    }

    /// <summary>
    ///     Returns the number of items in the internal index (same as Count).
    /// </summary>
    public int IndexCount => _index.Count;

    /// <summary>
    ///     Gets the item for a given key or throws if not present.
    /// </summary>
    public TItem this[TKey key] => _index[key];

    /// <summary>
    ///     Try to get the item with the given key.
    /// </summary>
    public bool TryGetValue(TKey key, out TItem value)
    {
        return _index.TryGetValue(key, out value!);
    }

    /// <summary>
    ///     Returns true if the collection contains an item with the given key.
    /// </summary>
    public bool ContainsKey(TKey key)
    {
        return _index.ContainsKey(key);
    }

    /// <summary>
    ///     Removes the item with the given key, if present.
    /// </summary>
    public bool RemoveByKey(TKey key)
    {
        if (!_index.TryGetValue(key, out var item))
            return false;

        // Remove from collection (which will call RemoveItem and update index as well).
        var idx = IndexOf(item);
        if (idx >= 0)
        {
            RemoveAt(idx);
            return true;
        }

        // Fallback: out-of-sync case, just remove from index.
        _index.Remove(key);
        return false;
    }

    /// <summary>
    ///     Adds a new item or replaces the existing item with the same key.
    /// </summary>
    public void Upsert(TItem item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        var key = _keySelector(item);

        if (_index.TryGetValue(key, out var existing))
        {
            // Replace existing item in-place (so bindings to that index see a change).
            var idx = IndexOf(existing);
            if (idx >= 0)
            {
                SetItem(idx, item);
                return;
            }

            // Index out-of-sync fallback: overwrite dictionary and add.
            _index[key] = item;
            Add(item);
        }
        else
        {
            Add(item);
        }
    }

    /// <summary>
    ///     Begin a bulk update; change notifications are suppressed until EndUpdate.
    /// </summary>
    public void BeginUpdate()
    {
        _suppressNotification = true;
    }

    /// <summary>
    ///     End a bulk update and emit a single Reset notification.
    /// </summary>
    public void EndUpdate()
    {
        if (!_suppressNotification) return;
        _suppressNotification = false;
        RaiseReset();
    }

    /// <summary>
    ///     Clears current items and replaces them with <paramref name="items" /> in a single update transaction.
    /// </summary>
    public void ResetWith(IEnumerable<TItem> items)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));

        BeginUpdate();
        try
        {
            base.ClearItems();
            _index.Clear();

            foreach (var i in items) AddInternal(i);
        }
        finally
        {
            EndUpdate();
        }
    }

    /// <summary>
    ///     Adds a range of items, optionally clearing first, with a single Reset notification.
    /// </summary>
    public void AddRange(IEnumerable<TItem> items, bool clearCollection = false)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));

        BeginUpdate();
        try
        {
            if (clearCollection)
            {
                base.ClearItems();
                _index.Clear();
            }

            foreach (var i in items) AddInternal(i);
        }
        finally
        {
            EndUpdate();
        }
    }

    // ---------- core overrides to keep index in sync ----------

    protected override void ClearItems()
    {
        base.ClearItems();
        _index.Clear();
    }

    protected override void InsertItem(int index, TItem item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        var key = _keySelector(item);

        // Ensure no duplicate keys; if needed, treat as upsert.
        if (_index.ContainsKey(key))
            throw new ArgumentException($"An item with the same key already exists: {key}", nameof(item));

        base.InsertItem(index, item);
        _index[key] = item;
    }

    protected override void RemoveItem(int index)
    {
        var item = this[index];
        var key = _keySelector(item);

        base.RemoveItem(index);
        _index.Remove(key);
    }

    protected override void SetItem(int index, TItem item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));

        var oldItem = this[index];
        var oldKey = _keySelector(oldItem);
        var newKey = _keySelector(item);

        base.SetItem(index, item);

        if (!EqualityComparer<TKey>.Default.Equals(oldKey, newKey)) _index.Remove(oldKey);

        _index[newKey] = item;
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppressNotification)
            return;

        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suppressNotification)
            return;

        base.OnPropertyChanged(e);
    }

    // ---------- helpers ----------

    private void AddInternal(TItem item)
    {
        // Bypass virtual Add to avoid double dictionary work; use base.InsertItem path.
        var key = _keySelector(item);
        if (_index.ContainsKey(key))
            throw new ArgumentException($"An item with the same key already exists: {key}", nameof(item));

        base.InsertItem(Count, item);
        _index[key] = item;
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}