using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
// ReSharper disable MemberCanBePrivate.Global

namespace DuplicateFileFinder.Gui.Util;

/// <summary>
/// ObservableCollection that supports batching/bulk updates without flooding the UI with change events.
/// Call <see cref="BeginUpdate"/> before bulk modifications and <see cref="EndUpdate"/> afterwards.
/// During a bulk update, change notifications are suppressed, then one Reset event is fired at the end.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

    public void BeginUpdate()
    {
        _suppressNotification = true;
    }

    public void EndUpdate()
    {
        if (!_suppressNotification) return;
        _suppressNotification = false;
        
        RaiseReset();
        
    }

    /// <summary>
    /// Clears current items and replaces them with <paramref name="items"/> in a single update transaction.
    /// </summary>
    public void AddRange(IEnumerable<T> items, bool clearCollection = false)
    {
        BeginUpdate();
        try
        {
            if (clearCollection) Items.Clear();
            foreach (var i in items)
                Items.Add(i);
        }
        finally
        {
            EndUpdate();
        }
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

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
