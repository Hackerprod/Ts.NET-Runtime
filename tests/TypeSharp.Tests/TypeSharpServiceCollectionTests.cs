using System;
using TypeSharp.Hosting.DependencyInjection;
using Xunit;

namespace TypeSharp.Tests;

public class TypeSharpServiceCollectionTests
{
    [Fact]
    public void FactoryResolvesOnce()
    {
        var di = new TypeSharpServiceCollection();
        var value = new object();
        var count = 0;
        Assert.Same(di, di.RegisterSingleton("svc", () => { count++; return value; }));
        Assert.Equal(0, count);
        Assert.Same(value, di.GetService<object>("svc"));
        Assert.Same(value, di.GetService("svc"));
        Assert.Equal(1, count);
    }
    [Fact]
    public void GetAllResolvesSnapshot()
    {
        var di = new TypeSharpServiceCollection();
        var view = di.GetAll();
        var value = new object();
        var late = new object();
        var count = 0;
        var added = 0;
        di.Register("dep", value);
        di.RegisterSingleton("head", () =>
        {
            count++;
            di.RegisterSingleton("late", () => { added++; return late; });
            return di.GetService<object>("dep")!;
        });
        Assert.False(view.ContainsKey("head"));
        Assert.Equal(0, count);
        Assert.Same(view, di.GetAll());
        Assert.True(view.TryGetValue("head", out var actual));
        Assert.Same(value, actual);
        Assert.Equal(1, count);
        Assert.Equal(0, added);
        Assert.False(view.ContainsKey("late"));
        di.Register("other", value);
        Assert.Same(value, view["other"]);
        Assert.Same(view, di.GetAll());
        Assert.Same(late, view["late"]);
        Assert.Equal(1, added);
        Assert.Equal(1, count);
    }
    [Fact]
    public void LatestRegistrationWinsOrdinally()
    {
        var di = new TypeSharpServiceCollection();
        var head = new object();
        var last = new object();
        var stale = 0;
        var view = di.GetAll();
        di.Register("key", head).Register("Key", head);
        di.RegisterSingleton("key", () => { stale++; return head; });
        Assert.False(view.ContainsKey("key"));
        di.RegisterSingleton("key", () => last);
        di.RegisterSingleton("direct", () => { stale++; return head; });
        di.Register("direct", last);
        Assert.Null(di.GetService<string>("key"));
        Assert.Same(last, view["key"]);
        Assert.Same(head, di.GetService("Key"));
        Assert.Null(di.GetService("KEY"));
        Assert.Same(last, di.GetService("direct"));
        Assert.Equal(0, stale);
    }
    [Fact]
    public void FailureIsCachedAndReplaceable()
    {
        var di = new TypeSharpServiceCollection();
        var view = di.GetAll();
        var boom = new InvalidOperationException("boom");
        var count = 0;
        di.RegisterSingleton<object>("bad", () => { count++; throw boom; });
        Assert.Same(boom, Assert.Throws<InvalidOperationException>(() => di.GetService("bad")));
        Assert.Same(boom, Assert.Throws<InvalidOperationException>(() => di.GetAll()));
        Assert.Equal(1, count);
        Assert.False(view.ContainsKey("bad"));
        di.Register("bad", boom);
        Assert.Same(boom, di.GetService("bad"));
        var loops = 0;
        di.RegisterSingleton("cycle", () => { loops++; return di.GetService<object>("cycle")!; });
        Assert.Throws<InvalidOperationException>(() => di.GetService("cycle"));
        Assert.Equal(1, loops);
        Assert.False(view.ContainsKey("cycle"));
    }
    [Fact]
    public void ReentrantSuccessorIsNotOverwritten()
    {
        var di = new TypeSharpServiceCollection();
        var view = di.GetAll();
        var old = new object();
        var next = new object();
        var count = 0;
        di.RegisterSingleton("key", () =>
        {
            di.RegisterSingleton("key", () => { count++; return next; });
            return old;
        });
        Assert.Same(old, di.GetService("key"));
        Assert.Equal(0, count);
        Assert.False(view.ContainsKey("key"));
        Assert.Same(next, di.GetService("key"));
        Assert.Same(next, view["key"]);
        Assert.Equal(1, count);
    }
}
