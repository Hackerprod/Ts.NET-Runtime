using System.Collections.Generic;
using System.Reflection;
using TypeSharp.Interop.HostExports;
using TypeSharp.Interop.Proxies;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

public sealed class TypeSharpProxyWrappingTests
{
    internal const string StockWitness =
        "ListIndexer: WrapObject(List<int>) raised TargetParameterCountException";

    private static TypeSharpProxy Proxy() => new(new HostRegistry());

    [Fact]
    public void ListIndexerIsNotInvokedOrExported()
    {
        var list = new List<int>(7) { 11, 22 };
        var proxy = Proxy();
        TsValue? wrapped = null;
        TargetParameterCountException? indexed = null;
        try { wrapped = proxy.WrapObject("ManagedList", list); }
        catch (TargetParameterCountException ex) { indexed = ex; }

        if (indexed is not null &&
            (indexed.GetType() != typeof(TargetParameterCountException) ||
             indexed.StackTrace?.Contains("TypeSharpProxy.WrapObject") != true))
            throw indexed;
        Assert.True(indexed is null, StockWitness);
        var obj = Assert.IsType<TsObjectValue>(wrapped).Value;
        Assert.Equal("ManagedList", obj.TypeName);
        Assert.Equal(list.Count, Assert.IsType<TsInt32Value>(obj.Fields["Count"]).Value);
        Assert.Equal(list.Capacity, Assert.IsType<TsInt32Value>(obj.Fields["Capacity"]).Value);
        Assert.False(obj.Fields.ContainsKey("Item"));
    }

    [Fact]
    public void OrdinaryGetterKeepsScalarAndTypeName()
    {
        var obj = Assert.IsType<TsObjectValue>(Proxy().WrapObject("ManagedScalar", new Scalar())).Value;
        Assert.Equal("ManagedScalar", obj.TypeName);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(obj.Fields["Number"]).Value);
        Assert.False(obj.Fields.ContainsKey("Static"));
        Assert.False(obj.Fields.ContainsKey("WriteOnly"));
    }

    [Fact]
    public void OrdinaryThrowingGetterPreservesInnerException()
    {
        var source = new Throwing();
        var ex = Assert.Throws<TargetInvocationException>(() => { Proxy().WrapObject("ManagedThrowing", source); });
        Assert.Same(source.Sentinel, ex.InnerException);
    }

    private sealed class Scalar
    {
        public int Number => 42;
        public static int Static => 9;
        public int WriteOnly { set { } }
    }

    private sealed class Throwing
    {
        internal readonly System.InvalidOperationException Sentinel = new("getter sentinel");
        public int Number => throw Sentinel;
    }
}
