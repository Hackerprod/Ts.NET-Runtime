using System.Collections.Generic;
using TypeSharp.Semantics.TypeSystem;
using Xunit;

namespace TypeSharp.Tests;

public sealed class GenericRestSubstitutionTests
{
    private static TsFunctionType Apply(TsFunctionType source, Dictionary<string, TsType> map) =>
        Assert.IsType<TsFunctionType>(TsType.Substitute(source, map));

    private static (TsFunctionType Fn, TsTypeParameter T, TsParameter Prefix, TsParameter Tail, object Sentinel) Direct()
    {
        var t = new TsTypeParameter("T");
        var sentinel = new object();
        var prefix = new TsParameter("prefix", t) { HasDefault = true, DefaultValue = sentinel };
        var tail = new TsParameter("tail", new TsArrayType(t)) { IsRest = true };
        return (new TsFunctionType([prefix, tail], t), t, prefix, tail, sentinel);
    }

    private static TsType Element(TsParameter parameter) => Assert.IsType<TsArrayType>(parameter.Type).ElementType;

    [Fact]
    public void DirectRestFlag()
    {
        var (source, _, _, tail, _) = Direct();
        var result = Apply(source, new() { ["T"] = TsType.String });
        Assert.Equal(2, result.Parameters.Count);
        Assert.Same(TsType.String, result.Parameters[0].Type);
        Assert.Same(TsType.String, Element(result.Parameters[1]));
        Assert.Same(TsType.String, result.ReturnType);
        Assert.True(tail.IsRest);
        Assert.True(result.Parameters[1].IsRest, "Direct rest tail must remain true.");
    }

    [Fact]
    public void NestedReturnRestIndex()
    {
        var u = new TsTypeParameter("U");
        var v = new TsTypeParameter("V");
        var callback = new TsFunctionType(
            [new("ordinary", new TsArrayType(v)), new("nestedTail", new TsArrayType(u)) { IsRest = true }], v);
        var outer = new TsFunctionType([], callback);
        var nested = Assert.IsType<TsFunctionType>(
            Apply(outer, new() { ["U"] = TsType.String, ["V"] = TsType.Bool }).ReturnType);
        Assert.Equal(2, nested.Parameters.Count);
        Assert.Same(TsType.Bool, Element(nested.Parameters[0]));
        Assert.Same(TsType.String, Element(nested.Parameters[1]));
        Assert.False(nested.Parameters[0].IsRest);
        Assert.Equal(1, callback.Parameters.FindIndex(p => p.IsRest));
        Assert.Equal(1, nested.Parameters.FindIndex(p => p.IsRest));
    }

    [Fact]
    public void ShapeDefaultsAndOriginals()
    {
        var (source, t, prefix, tail, sentinel) = Direct();
        var list = source.Parameters;
        var result = Apply(source, new() { ["T"] = TsType.String });
        Assert.NotSame(source, result);
        Assert.NotSame(list, result.Parameters);
        var first = result.Parameters[0]; var last = result.Parameters[1];
        Assert.Equal("prefix", first.Name);
        Assert.Equal("tail", last.Name);
        Assert.Same(TsType.String, result.ReturnType);
        Assert.True(first.HasDefault);
        Assert.False(first.IsRest);
        Assert.Same(sentinel, first.DefaultValue);
        Assert.False(last.HasDefault);
        Assert.Null(last.DefaultValue);
        Assert.False(result.HasDynamicSignature);
        Assert.Same(prefix, list[0]);
        Assert.Same(tail, list[1]);
        Assert.Same(t, Element(tail));
        Assert.True(tail.IsRest);
    }

    [Fact]
    public void DynamicOrdinaryUnmappedAndEmptyMap()
    {
        var t = new TsTypeParameter("Unmapped");
        var ordinary = new TsParameter("ordinary", new TsArrayType(t));
        var source = new TsFunctionType([ordinary], t) { HasDynamicSignature = true };
        var result = Apply(source, new() { ["Other"] = TsType.String });
        Assert.True(result.HasDynamicSignature);
        Assert.Same(t, result.ReturnType);
        var parameter = Assert.Single(result.Parameters);
        Assert.False(parameter.IsRest);
        Assert.Same(t, Element(parameter));
        var (restSource, _, _, restTail, _) = Direct();
        var same = Apply(restSource, new());
        Assert.Same(restSource, same);
        Assert.Same(restTail, same.Parameters[1]);
    }
}
