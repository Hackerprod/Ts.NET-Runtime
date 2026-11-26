using System;
using System.Collections.Generic;
using TypeSharp.Runtime.Collections;
using Xunit;

namespace TypeSharp.Tests;

public sealed class TsListReadOnlyViewTests
{
    [Fact]
    public void StandardGenericIndexerRejectsReplacementWithoutChangingOwner()
    {
        var owner = new TsList<string>(4);
        owner.Add("first");
        owner.Add("kept");
        var view = owner.AsReadOnly();
        var adapter = Assert.IsAssignableFrom<IList<string>>(view);
        Assert.Equal(2, owner.Count);
        Assert.Equal(2, adapter.Count);
        Assert.Equal("kept", owner[1]);
        Assert.Equal("kept", view[1]);
        Assert.Equal("kept", adapter[1]);

        Assert.Throws<NotSupportedException>(() => { adapter[1] = "unexpected"; });

        Assert.Equal(2, owner.Count);
        Assert.Equal(new[] { "first", "kept" }, owner.AsReadOnly());
        Assert.Equal(new[] { "first", "kept" }, view);
    }

    [Fact]
    public void CapturedLengthAndInPlaceOwnerReplacement()
    {
        var owner = new TsList<string>(4);
        owner.Add("before");
        owner.Add("second");
        var initialCapacity = owner.Capacity;
        var view = owner.AsReadOnly();
        Assert.Equal(2, view.Count);
        Assert.Equal("before", view[0]);
        Assert.Equal(new[] { "before", "second" }, view);

        owner[0] = "changed";
        owner.Add("third");

        Assert.Equal(initialCapacity, owner.Capacity);
        Assert.Equal(3, owner.Count);
        Assert.Equal(2, view.Count);
        Assert.Equal("changed", view[0]);
        Assert.Equal(new[] { "changed", "second" }, view);
        Assert.Equal(new[] { "changed", "second", "third" }, owner.AsReadOnly());
    }

    [Fact]
    public void RetainedSlotsObserveShiftsAndClear()
    {
        var owner = new TsList<string>(4);
        owner.Add("first");
        owner.Add("middle");
        owner.Add("last");
        var initialCapacity = owner.Capacity;
        var view = owner.AsReadOnly();

        Assert.True(owner.Remove("first"));
        Assert.Equal(2, owner.Count);
        Assert.Equal(3, view.Count);
        Assert.Equal<string?>(new string?[] { "middle", "last", null }, view);

        owner.RemoveAt(0);
        Assert.Equal(1, owner.Count);
        Assert.Equal<string?>(new string?[] { "last", null, null }, view);

        owner.Clear();
        Assert.Equal(initialCapacity, owner.Capacity);
        Assert.Equal(0, owner.Count);
        Assert.Equal(3, view.Count);
        Assert.Equal<string?>(new string?[] { null, null, null }, view);
        Assert.Empty(owner.AsReadOnly());
    }

    [Fact]
    public void ForcedGrowthSeparatesOldAndNewStorage()
    {
        var owner = new TsList<string>(1);
        owner.Add("old");
        var initialCapacity = owner.Capacity;
        Assert.Equal(initialCapacity, owner.Count);
        var oldView = owner.AsReadOnly();

        owner.Add("new");
        Assert.True(owner.Capacity > initialCapacity);
        var newView = owner.AsReadOnly();
        owner[0] = "new first";

        Assert.Equal(1, oldView.Count);
        Assert.Equal("old", oldView[0]);
        Assert.Equal(new[] { "old" }, oldView);
        Assert.Equal(2, newView.Count);
        Assert.Equal(new[] { "new first", "new" }, newView);
    }

    [Fact]
    public void InitiallyEmptyViewStaysEmptyAfterAdd()
    {
        var owner = new TsList<string>(2);
        var view = owner.AsReadOnly();
        Assert.Empty(view);

        owner.Add("first");

        Assert.Equal(0, view.Count);
        Assert.Empty(view);
        Assert.Equal(new[] { "first" }, owner.AsReadOnly());
    }
}
