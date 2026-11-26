using System.Collections.Generic;
using TypeSharp.Semantics.TypeSystem;
using Xunit;

namespace TypeSharp.Tests;

public sealed class NullableUnionCompatibilityTests
{
    private static TsLiteralType Literal(string value) => new(value, TsType.String);
    private static TsUnionType PlainPrimitiveUnion() => new(new List<TsType> { TsType.String, TsType.Number });
    private static TsNullableType PrimitiveUnion() => new(PlainPrimitiveUnion());
    private static TsUnionType LiteralUnion() => new(new List<TsType> { Literal("fast"), Literal("slow") });
    private static TsNullableType WrappedLiteralUnion() => new(LiteralUnion());

    [Fact]
    public void StringMember()
    {
        bool actual = TsType.IsCompatibleWith(TsType.String, PrimitiveUnion());
        Assert.True(actual, $"string-member: expected True; actual {actual}");
    }

    [Fact]
    public void NumericWidening()
    {
        bool actual = TsType.IsCompatibleWith(TsType.Int32, PrimitiveUnion());
        Assert.True(actual, $"numeric-widening: expected True; actual {actual}");
    }

    [Fact]
    public void LiteralMemberOrder()
    {
        bool actual = TsType.IsCompatibleWith(Literal("fast"), WrappedLiteralUnion());
        Assert.True(actual, $"literal-member-order: expected True; actual {actual}");
    }

    [Fact]
    public void BoolNonmember()
    {
        bool actual = TsType.IsCompatibleWith(TsType.Bool, PrimitiveUnion());
        Assert.False(actual, $"bool-nonmember: expected False; actual {actual}");
    }

    [Fact]
    public void NullValue()
    {
        bool actual = TsType.IsCompatibleWith(TsType.Null, PrimitiveUnion());
        Assert.True(actual, $"null: expected True; actual {actual}");
    }

    [Fact]
    public void UndefinedValue()
    {
        bool actual = TsType.IsCompatibleWith(TsType.Undefined, PrimitiveUnion());
        Assert.True(actual, $"undefined: expected True; actual {actual}");
    }

    [Fact]
    public void UnwrappedLiteral()
    {
        bool actual = TsType.IsCompatibleWith(Literal("fast"), LiteralUnion());
        Assert.True(actual, $"unwrapped-literal: expected True; actual {actual}");
    }

    [Fact]
    public void UnwrappedNumeric()
    {
        bool actual = TsType.IsCompatibleWith(TsType.Int32, PlainPrimitiveUnion());
        Assert.True(actual, $"unwrapped-numeric: expected True; actual {actual}");
    }

    [Fact]
    public void PlainNullableLiteral()
    {
        bool actual = TsType.IsCompatibleWith(Literal("fast"), new TsNullableType(TsType.String));
        Assert.True(actual, $"plain-nullable-literal: expected True; actual {actual}");
    }

    [Fact]
    public void EqualWrapper()
    {
        var target = WrappedLiteralUnion();
        bool actual = TsType.IsCompatibleWith(target, target);
        Assert.True(actual, $"equal-wrapper: expected True; actual {actual}");
    }

    [Fact]
    public void LiteralNonmember()
    {
        bool actual = TsType.IsCompatibleWith(Literal("other"), WrappedLiteralUnion());
        Assert.False(actual, $"literal-nonmember: expected False; actual {actual}");
    }
}
