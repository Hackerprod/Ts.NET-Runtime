using TypeSharp.Syntax.SyntaxTree;
using Xunit;

namespace TypeSharp.Syntax.Tests;

public sealed class LexerBomWhitespaceTests
{
    private const string DeclarationWithBom = "\uFEFFlet\uFEFFx=1;\uFEFF";

    [Fact(Timeout = 5000)]
    public void BomIsTriviaWithoutLosingSourceCoordinates()
    {
        var tokens = new Lexer(DeclarationWithBom).Tokenize();

        Assert.Equal(TokenKind.LetKeyword, tokens[0].Kind);
        Assert.Equal(6, tokens.Count);
        AssertToken(tokens[0], TokenKind.LetKeyword, "let", 1, 2, 1);
        AssertToken(tokens[1], TokenKind.Identifier, "x", 1, 6, 5);
        AssertToken(tokens[2], TokenKind.Equals, "=", 1, 7, 6);
        AssertToken(tokens[3], TokenKind.IntegerLiteral, "1", 1, 8, 7);
        AssertToken(tokens[4], TokenKind.Semicolon, ";", 1, 9, 8);
        AssertToken(tokens[5], TokenKind.EOF, "", 1, 11, 10);
    }

    [Fact(Timeout = 5000)]
    public void MixedBomTriviaAdvancesToEof()
    {
        var tokens = new Lexer("\uFEFF \t\u0085\uFEFF\uFEFF").Tokenize();

        Assert.Equal(TokenKind.EOF, tokens[0].Kind);
        AssertToken(Assert.Single(tokens), TokenKind.EOF, "", 1, 7, 6);
    }

    [Fact(Timeout = 5000)]
    public void DeclarationParsesAcrossBomTrivia()
    {
        AssertDeclaration(DeclarationWithBom, 7);
    }

    [Fact]
    public void ExistingHorizontalWhitespaceStillParses()
    {
        const string source = " \tlet\u0085x=1;\t";
        var tokens = new Lexer(source).Tokenize();
        Assert.Equal(6, tokens.Count);
        AssertToken(tokens[0], TokenKind.LetKeyword, "let", 1, 3, 2);
        AssertToken(tokens[1], TokenKind.Identifier, "x", 1, 7, 6);
        AssertToken(tokens[5], TokenKind.EOF, "", 1, 12, 11);
        AssertDeclaration(source, 8);
    }

    [Theory]
    [InlineData("'", TokenKind.StringLiteral)]
    [InlineData("\"", TokenKind.StringLiteral)]
    [InlineData("`", TokenKind.TemplateLiteral)]
    public void BomInsideLiteralIsPreserved(string quote, TokenKind kind)
    {
        const string content = "a\uFEFFb";
        var tokens = new Lexer(quote + content + quote + " x").Tokenize();

        Assert.Equal(3, tokens.Count);
        AssertToken(tokens[0], kind, content, 1, 1, 0);
        Assert.Equal(content, Assert.IsType<string>(tokens[0].Value));
        AssertToken(tokens[1], TokenKind.Identifier, "x", 1, 7, 6);
        AssertToken(tokens[2], TokenKind.EOF, "", 1, 8, 7);
    }

    [Fact]
    public void BomInsideCommentsDoesNotEndThemOrChangeLocations()
    {
        var tokens = new Lexer("//a\uFEFFb\r\n/*c\uFEFFd*/x").Tokenize();

        Assert.Equal(2, tokens.Count);
        AssertToken(tokens[0], TokenKind.Identifier, "x", 2, 8, 14);
        AssertToken(tokens[1], TokenKind.EOF, "", 2, 9, 15);
    }

    private static void AssertDeclaration(string source, int integerOffset)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new Lexer(source).Tokenize());
        var file = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var declaration = Assert.IsType<VariableDeclarationSyntax>(Assert.Single(file.Members));
        Assert.Equal("x", declaration.Name);
        var initializer = Assert.IsType<LiteralExpressionSyntax>(declaration.Initializer);
        AssertToken(initializer.Token, TokenKind.IntegerLiteral, "1", 1, integerOffset + 1, integerOffset);
        Assert.Equal(1L, Assert.IsType<long>(initializer.Token.Value));
    }

    private static void AssertToken(Token token, TokenKind kind, string text, int line, int column, int offset)
    {
        Assert.Equal(kind, token.Kind);
        Assert.Equal(text, token.Text);
        Assert.Equal(line, token.Location.Line);
        Assert.Equal(column, token.Location.Column);
        Assert.Equal(offset, token.Location.Offset);
    }
}
