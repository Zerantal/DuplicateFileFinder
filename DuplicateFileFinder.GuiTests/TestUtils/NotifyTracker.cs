using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DuplicateFileFinder.GuiTests.TestUtils;

internal sealed class NotifyTracker
{
    public int CollectionChangedCount { get; private set; }
    public int ResetCount { get; private set; }
    public int PropertyChangedCount { get; private set; }

    public readonly List<NotifyCollectionChangedEventArgs> CollectionEvents = new();
    public readonly List<PropertyChangedEventArgs> PropertyEvents = new();

    public void Attach(INotifyCollectionChanged collection, INotifyPropertyChanged? props = null)
    {
        collection.CollectionChanged += OnCollectionChanged;
        if (props is not null)
            props.PropertyChanged += OnPropertyChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        CollectionChangedCount++;
        CollectionEvents.Add(e);
        if (e.Action == NotifyCollectionChangedAction.Reset)
            ResetCount++;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        PropertyChangedCount++;
        PropertyEvents.Add(e);
    }
}
