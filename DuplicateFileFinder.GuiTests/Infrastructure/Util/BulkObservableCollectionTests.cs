using System.Collections.Specialized;
using System.Linq;

using DuplicateFileFinder.Gui.Infrastructure.Util;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Infrastructure.Util;

public sealed class BulkObservableCollectionTests
{
    [Fact]
    public void AddRange_ClearCollection_ReplacesWithSingleReset()
    {
        var c = new BulkObservableCollection<int> { 1, 2, 3 };

        var tracker = new NotifyTracker();
        tracker.Attach(c, c);

        c.AddRange([10, 20], clearCollection: true);

        Assert.Equal([10, 20], c.ToArray());

        // During AddRange, notifications are suppressed; EndUpdate emits a single Reset and related PropertyChanged.
        Assert.Equal(1, tracker.ResetCount);
        Assert.Equal(1, tracker.CollectionChangedCount);
        Assert.Contains(tracker.CollectionEvents, e => e.Action == NotifyCollectionChangedAction.Reset);

        // RaiseReset also raises Count + Item[] property changes.
        Assert.True(tracker.PropertyChangedCount >= 2);
    }

    [Fact]
    public void BeginUpdate_SuppressesIntermediateEvents_UntilEndUpdate()
    {
        var c = new BulkObservableCollection<int>();

        var tracker = new NotifyTracker();
        tracker.Attach(c, c);

        c.BeginUpdate();
        c.Add(1);
        c.Add(2);
        c.Add(3);

        Assert.Equal(3, c.Count);
        Assert.Equal(0, tracker.CollectionChangedCount);
        Assert.Equal(0, tracker.PropertyChangedCount);

        c.EndUpdate();

        Assert.Equal(1, tracker.ResetCount);
        Assert.Equal(1, tracker.CollectionChangedCount);
        Assert.True(tracker.PropertyChangedCount >= 2);
    }

    [Fact]
    public void EndUpdate_WhenNotInUpdate_IsNoOp()
    {
        var c = new BulkObservableCollection<int> { 1 };

        var tracker = new NotifyTracker();
        tracker.Attach(c, c);

        c.EndUpdate();

        Assert.Equal(0, tracker.CollectionChangedCount);
        Assert.Equal(0, tracker.PropertyChangedCount);
    }
}
