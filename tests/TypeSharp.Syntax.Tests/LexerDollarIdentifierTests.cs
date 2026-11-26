using TypeSharp.Syntax;
using Xunit;

namespace TypeSharp.Syntax.Tests;

public sealed class LexerDollarIdentifierTests
{
    private const string File = "dollar-c280.ts";

    private static string At(TokenKind kind, string spelling, int offset) =>
        $"{kind}:{spelling}@{offset}/1/{offset + 1}/{File}";

    private static string Tail(int end) =>
        $" | {At(TokenKind.Semicolon, ";", end)} | {At(TokenKind.EOF, "", end + 1)}";

    [Theory]
    [InlineData("dollar-start", "$state")]
    [InlineData("dollar-medial", "cost$2")]
    [InlineData("dollar-alone", "$")]
    [InlineData("dollar-terminal", "end$")]
    [InlineData("dollar-keyword-prefix", "let$")]
    public void DollarIdentifier(string id, string name) =>
        Verify(id, name, TokenKind.Identifier, name);

    [Theory]
    [InlineData("keyword", "let", TokenKind.LetKeyword, "let")]
    [InlineData("underscore", "_x2", TokenKind.Identifier, "_x2")]
    [InlineData("decorator", "@decorator", TokenKind.Identifier, "@decorator")]
    [InlineData("number", "42", TokenKind.IntegerLiteral, "42")]
    [InlineData("quoted-astral", "\"😀$\"", TokenKind.StringLiteral, "😀$")]
    [InlineData("template", "\u0060$\u0060", TokenKind.TemplateLiteral, "$")]
    public void UnchangedTokens(string id, string raw, TokenKind kind, string text) =>
        Verify(id, raw, kind, text);

    private static void Verify(string id, string raw, TokenKind kind, string text)
    {
        var actual = string.Join(" | ", new Lexer(raw + ";", File).Tokenize()
            .Select(t => $"{t.Kind}:{t.Text}@{t.Location.Offset}/{t.Location.Line}/{t.Location.Column}/{t.Location.Source}"));
        var expected = At(kind, text, 0) + Tail(raw.Length);
        Assert.True(actual == expected, $"{id} expected [{expected}] actual [{actual}]");
    }
}
