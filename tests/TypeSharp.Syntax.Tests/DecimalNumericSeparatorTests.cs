using System;
using System.Numerics;
using TypeSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Syntax.Tests;

public class DecimalNumericSeparatorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("grouped-whole", "1_000_000", TokenKind.IntegerLiteral, 1000000L)]
    [InlineData("grouped-fraction", "12.5_0", TokenKind.FloatLiteral, 12.5)]
    [InlineData("grouped-exponent", "1.25e+0_2", TokenKind.FloatLiteral, 125.0)]
    [InlineData("plain-whole", "1000000", TokenKind.IntegerLiteral, 1000000L)]
    [InlineData("plain-fraction", "12.50", TokenKind.FloatLiteral, 12.5)]
    [InlineData("plain-exponent", "1.25e+02", TokenKind.FloatLiteral, 125.0)]
    [InlineData("prefixed-control", "0xFF_FF", TokenKind.IntegerLiteral, 65535L)]
    public void Tokenize_PreservesDecimalNumericSpellingAndValue(
        string label, string spelling, TokenKind expectedKind, object expectedValue)
    {
        var tokens = new Lexer(spelling + " + 2").Tokenize();
        output.WriteLine($"C141_SYNTAX row={label} source={spelling} + 2");
        foreach (var token in tokens)
            output.WriteLine(FormattableString.Invariant(
                $"kind={token.Kind} text='{token.Text}' type={token.Value?.GetType().FullName ?? "null"} value={token.Value ?? "null"} line={token.Location.Line} column={token.Location.Column} offset={token.Location.Offset}"));

        Assert.True(tokens.Count == 4, $"C141_DECIMAL_TOKEN_SHAPE row={label} expected=4 actual={tokens.Count}");
        var literal = tokens[0];
        Assert.Equal(spelling, literal.Text);
        Assert.Equal(expectedKind, literal.Kind);
        Assert.Equal(expectedValue, literal.Value);
        Assert.Equal(1, literal.Location.Line);
        Assert.Equal(1, literal.Location.Column);
        Assert.Equal(0, literal.Location.Offset);
        Assert.Equal(TokenKind.Plus, tokens[1].Kind);
        Assert.Equal(1, tokens[1].Location.Line);
        Assert.Equal(spelling.Length + 2, tokens[1].Location.Column);
        Assert.Equal(spelling.Length + 1, tokens[1].Location.Offset);
        Assert.Equal(TokenKind.IntegerLiteral, tokens[2].Kind);
        Assert.Equal(2L, tokens[2].Value);
        Assert.Equal(TokenKind.EOF, tokens[3].Kind);
    }

    [Fact]
    public void Tokenize_PreservesOrdinaryDecimalBigInt()
    {
        var tokens = new Lexer("1000n").Tokenize();
        foreach (var token in tokens)
            output.WriteLine(FormattableString.Invariant(
                $"C141_BIGINT kind={token.Kind} text='{token.Text}' type={token.Value?.GetType().FullName ?? "null"} value={token.Value ?? "null"} line={token.Location.Line} column={token.Location.Column} offset={token.Location.Offset}"));

        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenKind.IntegerLiteral, tokens[0].Kind);
        Assert.Equal("1000n", tokens[0].Text);
        Assert.Equal(new BigInteger(1000), Assert.IsType<BigInteger>(tokens[0].Value));
        Assert.Equal(0, tokens[0].Location.Offset);
        Assert.Equal(TokenKind.EOF, tokens[1].Kind);
        Assert.Equal(5, tokens[1].Location.Offset);
    }
}
