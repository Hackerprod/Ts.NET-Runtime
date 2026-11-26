using System;
using System.Collections.Generic;
using TypeSharp.Runtime.Collections;
using Xunit;

namespace TypeSharp.Tests;

public sealed class TsDictionaryReadOnlyViewTests
{
    [Fact]
    public void StandardAdapterReportsReadOnly()
    {
        var owner = new TsDictionary<string, object>();
        var adapter = Assert.IsAssignableFrom<IDictionary<string, object>>(owner.AsReadOnly());
        Assert.True(adapter.IsReadOnly);
    }

    [Fact]
    public void StandardAdapterAddThrowsWithoutChangingOwner()
    {
        var owner = new TsDictionary<string, object>(4);
        var kept = new object();
        owner.Set("kept", kept);
        var view = owner.AsReadOnly();
        var adapter = Assert.IsAssignableFrom<IDictionary<string, object>>(view);

        Assert.Throws<NotSupportedException>(() => adapter.Add("new", new object()));
        Assert.Equal(1, owner.Count);
        Assert.Equal(1, view.Count);
        Assert.Same(kept, owner.Get("kept"));
        Assert.Same(kept, view["kept"]);
        Assert.False(owner.Contains("new"));
        Assert.False(view.ContainsKey("new"));
    }

    [Fact]
    public void DefaultCapacityViewStaysLiveAndOrdinal() =>
        AssertLiveView(new TsDictionary<string, object>());

    [Fact]
    public void PositiveCapacityViewStaysLiveAndOrdinal() =>
        AssertLiveView(new TsDictionary<string, object>(4));

    [Fact]
    public void NegativeCapacityStillThrows() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TsDictionary<string, object>(-1));

    private static void AssertLiveView(TsDictionary<string, object> owner)
    {
        var view = owner.AsReadOnly();
        Assert.Empty(view);
        Assert.Same(view, owner.AsReadOnly());

        var upper = new object();
        var lower = new object();
        var replacement = new object();
        owner.Set("Key", upper);
        owner.Set("key", lower);
        Assert.Equal(2, view.Count);
        Assert.Same(upper, owner.Get("Key"));
        Assert.Same(lower, owner.Get("key"));
        Assert.Same(upper, view["Key"]);
        Assert.Same(lower, view["key"]);
        Assert.False(view.ContainsKey("KEY"));

        owner.Set("Key", replacement);
        Assert.Equal(2, view.Count);
        Assert.Same(replacement, owner.Get("Key"));
        Assert.Same(replacement, view["Key"]);
        Assert.Same(lower, view["key"]);

        Assert.True(owner.Remove("Key"));
        Assert.False(view.ContainsKey("Key"));
        Assert.Same(lower, view["key"]);
        Assert.Equal(1, view.Count);
        Assert.Same(view, owner.AsReadOnly());
    }
}
