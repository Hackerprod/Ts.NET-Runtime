using Xunit;

namespace TypeSharp.Syntax.Tests;

public sealed class LexerLineTerminatorTriviaTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void LineCommentStopsAtTerminator(string separator)
    {
        var tokens = new Lexer("// note" + separator + "let x=1;").Tokenize();
        AssertToken(tokens[0], TokenKind.LetKeyword, "let", 2, 1, 7 + separator.Length);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void WhitespaceTracksTerminator(string separator)
    {
        var tokens = new Lexer("return " + separator + "  x").Tokenize();
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.ReturnKeyword, tokens[0].Kind);
        AssertToken(tokens[1], TokenKind.Identifier, "x", 2, 3, 9 + separator.Length);
        Assert.Equal(TokenKind.EOF, tokens[2].Kind);
    }

    [Fact]
    public void LineCommentWithoutTerminatorEndsAtEof()
    {
        var tokens = new Lexer("// note").Tokenize();
        AssertToken(Assert.Single(tokens), TokenKind.EOF, "", 1, 8, 7);
    }

    [Fact]
    public void BlockCommentTracksMixedTerminators()
    {
        var tokens = new Lexer("/*a\r\nb\rc\u2028d\u2029e\n*/ \tlet x=1;").Tokenize();
        AssertToken(tokens[0], TokenKind.LetKeyword, "let", 6, 5, 17);
    }

    private static void AssertToken(Token token, TokenKind kind, string text, int line, int column, int offset)
    {
        Assert.Equal(kind, token.Kind);
        Assert.Equal(text, token.Text);
        Assert.Equal(offset, token.Location.Offset);
        Assert.Equal(line, token.Location.Line);
        Assert.Equal(column, token.Location.Column);
    }
}
