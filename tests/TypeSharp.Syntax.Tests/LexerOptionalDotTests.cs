using System.Linq;
using Xunit;

namespace TypeSharp.Syntax.Tests;

public sealed class LexerOptionalDotTests
{
    [Theory]
    [InlineData("true?.3:0", ".3", 5, 0.3)]
    [InlineData("true?.0:0", ".0", 5, 0.0)]
    [InlineData("true? .3:0", ".3", 6, 0.3)]
    public void DotBeforeAsciiDigitStartsTernaryFloat(
        string source, string floatText, int floatOffset, double floatValue)
    {
        var tokens = new Lexer(source).Tokenize();
        Assert.Equal(TokenKind.Question, tokens[1].Kind);
        Assert.Equal(new[]
        {
            TokenKind.TrueLiteral, TokenKind.Question, TokenKind.FloatLiteral,
            TokenKind.Colon, TokenKind.IntegerLiteral, TokenKind.EOF
        }, tokens.Select(token => token.Kind).ToArray());
        Assert.Equal("?", tokens[1].Text);
        Assert.Equal(4, tokens[1].Location.Offset);
        Assert.Equal(floatText, tokens[2].Text);
        Assert.Equal(floatOffset, tokens[2].Location.Offset);
        Assert.Equal(floatValue, Assert.IsType<double>(tokens[2].Value));
    }

    [Fact]
    public void MemberAccessAndNullishCoalescingRetainTheirOperators() =>
        AssertKinds("x?.y ?? z", TokenKind.Identifier, TokenKind.QuestionDot,
            TokenKind.Identifier, TokenKind.QuestionQuestion, TokenKind.Identifier, TokenKind.EOF);

    [Fact]
    public void OptionalIndexAndOptionalCallRetainTheirOperators() =>
        AssertKinds("x?.[0] ?? x?.(0)", TokenKind.Identifier, TokenKind.QuestionDot,
            TokenKind.OpenBracket, TokenKind.IntegerLiteral, TokenKind.CloseBracket,
            TokenKind.QuestionQuestion, TokenKind.Identifier, TokenKind.QuestionDot,
            TokenKind.OpenParen, TokenKind.IntegerLiteral, TokenKind.CloseParen, TokenKind.EOF);

    private static void AssertKinds(string source, params TokenKind[] expected) =>
        Assert.Equal(expected, new Lexer(source).Tokenize().Select(token => token.Kind).ToArray());
}
