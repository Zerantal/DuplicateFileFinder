using System;
using System.Collections.Specialized;

using DuplicateFileFinder.Gui.Infrastructure.Util;
using DuplicateFileFinder.GuiTests.TestUtils;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Infrastructure.Util;

public sealed class BulkKeyedObservableCollectionTests
{
    private sealed record Item(int Id, string Name);

    [Fact]
    public void Add_AddsToIndex()
    {
        var c = new BulkKeyedObservableCollection<int, Item>(x => x.Id) { new Item(1, "a") };

        Assert.Single(c);
        Assert.Equal(1, c.IndexCount);
        Assert.True(c.ContainsKey(1));
        Assert.Equal("a", c[1].Name);
    }

    [Fact]
    public void Upsert_AddsWhenMissing()
    {
        var c = new BulkKeyedObservableCollection<int, Item>(x => x.Id);

        c.Upsert(new Item(1, "a"));

        Assert.Single(c);
        Assert.Equal("a", c[1].Name);
    }

    [Fact]
    public void Upsert_ReplacesWhenPresent()
    {
        var c = new BulkKeyedObservableCollection<int, Item>(x => x.Id) { new Item(1, "a") };

        var tracker = new NotifyTracker();
        tracker.Attach(c, c);

        c.Upsert(new Item(1, "b"));

        Assert.Single(c);
        Assert.Equal("b", c[1].Name);

        // Replacement should fire a Replace collection changed event (not Reset).
        Assert.True(tracker.CollectionChangedCount >= 1);
        Assert.Contains(tracker.CollectionEvents, e => e.Action == NotifyCollectionChangedAction.Replace);
        Assert.Equal(0, tracker.ResetCount);
    }

    [Fact]
    public void Add_DuplicateKey_Throws()
    {
        // ReSharper disable once CollectionNeverQueried.Local
        var c = new BulkKeyedObservableCollection<int, Item>(x => x.Id) { new Item(1, "a") };

        Assert.Throws<ArgumentException>(() => c.Add(new Item(1, "b")));
    }

    [Fact]
    public void RemoveByKey_RemovesFromBothCollectionAndIndex()
    {
        var c = new BulkKeyedObservableCollection<int, Item>(x => x.Id) { new Item(1, "a"), new Item(2, "b") };

        var removed = c.RemoveByKey(1);

        Assert.True(removed);
        Assert.Single(c);
        Assert.Equal(1, c.IndexCount);
        Assert.False(c.ContainsKey(1));
        Assert.True(c.ContainsKey(2));
    }

    [Fact]
    public void ResetWith_ReplacesWithSingleReset()
    {
        var c = new BulkKeyedObservableCollection<int, Item>(x => x.Id) { new Item(1, "a") };

        var tracker = new NotifyTracker();
        tracker.Attach(c, c);

        c.ResetWith([new Item(10, "x"), new Item(20, "y")]);

        Assert.Equal(2, c.Count);
        Assert.Equal(2, c.IndexCount);
        Assert.Equal("x", c[10].Name);

        Assert.Equal(1, tracker.ResetCount);
        Assert.Equal(1, tracker.CollectionChangedCount);
        Assert.Contains(tracker.CollectionEvents, e => e.Action == NotifyCollectionChangedAction.Reset);
        Assert.True(tracker.PropertyChangedCount >= 2);
    }

    [Fact]
    public void AddRange_ClearCollection_ReplacesWithSingleReset()
    {
        var c = new BulkKeyedObservableCollection<int, Item>(x => x.Id) { new Item(1, "a"), new Item(2, "b") };

        var tracker = new NotifyTracker();
        tracker.Attach(c, c);

        c.AddRange([new Item(3, "c")], clearCollection: true);

        Assert.Single(c);
        Assert.True(c.ContainsKey(3));
        Assert.False(c.ContainsKey(1));
        Assert.Equal(1, tracker.ResetCount);
    }

    [Fact]
    public void AddRange_DuplicateKey_Throws()
    {
        var c = new BulkKeyedObservableCollection<int, Item>(x => x.Id) { new Item(1, "a") };

        Assert.Throws<ArgumentException>(() => c.AddRange([new Item(1, "b")]));
    }
}
