using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Xunit;
using static TypeSharp.Syntax.TokenKind;

namespace TypeSharp.Syntax.Tests;

public sealed class LexerNumericSuffixColumnsTests
{
    private const string File = "numeric-columns.ts";

    private readonly record struct Cell(TokenKind Kind, string Text, object? Value,
        string Source, int Offset, int Line, int Column)
    {
        public string Show() =>
            $"{Kind}:{Text}@{Source}/offset={Offset}/line={Line}/column={Column}=" +
            (Value is null ? "null" :
                $"{Value.GetType().FullName}:{Convert.ToString(Value, CultureInfo.InvariantCulture)}");
    }

    private static Cell At(TokenKind kind, string text, object? value, int offset, int column,
        int line = 1) => new(kind, text, value, File, offset, line, column);

    private static void Check(string input, params Cell[] expected)
    {
        var actual = new Lexer(input, File).Tokenize().Select(t =>
            new Cell(t.Kind, t.Text, t.Value, t.Location.Source, t.Location.Offset,
                t.Location.Line, t.Location.Column)).ToArray();

        var want = $"expected[{expected.Length}] " + string.Join(" | ", expected.Select(t => t.Show()));
        var got = $"actual[{actual.Length}] " + string.Join(" | ", actual.Select(t => t.Show()));
        Console.WriteLine(want);
        Console.WriteLine(got);
        Assert.True(expected.SequenceEqual(actual), "Token vector mismatch");
    }

    private static void CheckSuffix(string literal, TokenKind kind, object value)
    {
        var end = 2 + literal.Length;
        Check("  " + literal + "; §",
            At(kind, literal, value, 2, 3),
            At(Semicolon, ";", null, end, end + 1),
            At(Error, "§", null, end + 2, end + 3),
            At(EOF, "", null, end + 3, end + 4));
    }

    [Fact] public void BigIntegerSuffix() => CheckSuffix("123n", IntegerLiteral, new BigInteger(123));
    [Fact] public void Unsigned64Suffix() => CheckSuffix("123u64", IntegerLiteral, 123UL);
    [Fact] public void Signed64Suffix() => CheckSuffix("123i64", IntegerLiteral, 123L);
    [Fact] public void DecimalSuffix() => CheckSuffix("12.5m", FloatLiteral, 12.5m);

    [Fact]
    public void OrdinaryInteger() =>
        Check("123;", At(IntegerLiteral, "123", 123L, 0, 1),
            At(Semicolon, ";", null, 3, 4), At(EOF, "", null, 4, 5));

    [Fact]
    public void OrdinaryFraction() =>
        Check("12.5;", At(FloatLiteral, "12.5", 12.5d, 0, 1),
            At(Semicolon, ";", null, 4, 5), At(EOF, "", null, 5, 6));

    [Fact]
    public void GroupedInteger() =>
        Check("1_234;", At(IntegerLiteral, "1_234", 1234L, 0, 1),
            At(Semicolon, ";", null, 5, 6), At(EOF, "", null, 6, 7));

    [Fact]
    public void PrefixedBigInteger() =>
        Check("0x7bn;", At(IntegerLiteral, "0x7bn", new BigInteger(123), 0, 1),
            At(Semicolon, ";", null, 5, 6), At(EOF, "", null, 6, 7));

    [Fact]
    public void CrLfResetsColumns() =>
        Check("123n\r\n  x;", At(IntegerLiteral, "123n", new BigInteger(123), 0, 1),
            At(Identifier, "x", null, 8, 3, 2), At(Semicolon, ";", null, 9, 4, 2),
            At(EOF, "", null, 10, 5, 2));

    [Fact]
    public void GroupingRollsBackBeforeSuffix() =>
        Check("1_23n;", At(IntegerLiteral, "1", 1L, 0, 1),
            At(Identifier, "_23n", null, 1, 2), At(Semicolon, ";", null, 5, 6),
            At(EOF, "", null, 6, 7));
}
