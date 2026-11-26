using TypeSharp.Semantics.TypeSystem;
using Xunit;

namespace TypeSharp.Tests;

public sealed class TrailingDefaultCompatibilityTests
{
    private static TsParameter P(TsType? type = null, bool defaulted = false, bool rest = false) =>
        new("p", type ?? TsType.String) { HasDefault = defaulted, DefaultValue = defaulted ? "!" : null, IsRest = rest };

    private static TsFunctionType F(TsType result, params TsParameter[] parameters) =>
        new(new System.Collections.Generic.List<TsParameter>(parameters), result);

    private static TsFunctionType Source() => F(TsType.String, P(), P(defaulted: true));
    private static TsFunctionType Target() => F(TsType.Void, P());

    private static void Check(string id, bool expected, bool actual)
    {
        var message = $"{id}: expected {expected}; actual {actual}";
        if (expected) Assert.True(actual, message);
        else Assert.False(actual, message);
    }

    [Fact]
    public void DirectDefault() => Check("direct-default", true, Source().IsAssignableTo(Target()));

    [Fact]
    public void CompatibleDefault()
    {
        var source = Source();
        var target = Target();
        Assert.False(target.IsAssignableTo(source), "reverse direction must not mask stock");
        Check("compatible-default", true, TsType.IsCompatibleWith(source, target));
    }

    [Fact]
    public void StructuralDefault()
    {
        var source = new TsClassType("DefaultOffer");
        var target = new TsInterfaceType("DefaultContract");
        source.Methods.Add("render", new TsMethod("render", TsType.String, Source().Parameters)
            { AccessModifier = TsAccessModifier.Public });
        target.Methods.Add("render", new TsMethod("render", TsType.Void, Target().Parameters)
            { AccessModifier = TsAccessModifier.Public });
        Assert.False(source.IsAssignableTo(target), "nominal direction must not mask stock");
        Assert.False(target.IsAssignableTo(source), "reverse nominal direction must not mask stock");
        Check("structural-default", true, TsType.IsCompatibleWith(source, target));
    }

    [Fact]
    public void ExactPrefix() => Check("exact-prefix", true, F(TsType.String, P()).IsAssignableTo(Target()));

    [Fact]
    public void Shorter() => Check("shorter", true, F(TsType.String).IsAssignableTo(Target()));

    [Fact]
    public void ExtraRequired() => Check("extra-required", false,
        TsType.IsCompatibleWith(F(TsType.String, P(), P()), Target()));

    [Fact]
    public void DefaultThenRequired() => Check("default-then-required", false,
        F(TsType.String, P(), P(defaulted: true), P()).IsAssignableTo(Target()));

    [Fact]
    public void ExtraNullable() => Check("extra-nullable", false,
        F(TsType.String, P(), P(new TsNullableType(TsType.String))).IsAssignableTo(Target()));

    [Fact]
    public void ExtraRest() => Check("extra-rest", false,
        F(TsType.String, P(), P(rest: true)).IsAssignableTo(Target()));

    [Fact]
    public void ExtraDefaultRest() => Check("extra-default-rest", false,
        F(TsType.String, P(), P(defaulted: true, rest: true)).IsAssignableTo(Target()));

    [Fact]
    public void BadPrefix() => Check("bad-prefix", false,
        F(TsType.String, P(TsType.Bool), P(defaulted: true)).IsAssignableTo(Target()));

    [Fact]
    public void BadReturn() => Check("bad-return", false,
        F(TsType.String, P()).IsAssignableTo(F(TsType.Bool, P())));
}
