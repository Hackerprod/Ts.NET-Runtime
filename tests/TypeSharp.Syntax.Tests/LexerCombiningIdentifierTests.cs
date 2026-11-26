using Xunit;

namespace TypeSharp.Syntax.Tests;

public sealed class LexerCombiningIdentifierTests
{
    internal const string File = "combining.ts";
    internal const string Witness = "let cafe\u0301 = 3;\ncafe\u0301;";

    internal static string At(string kind, string text, int offset, int line, int column) =>
        $"{kind}:{text}@{offset}/{line}/{column}/{File}";

    internal static string Vector(string input) => string.Join(" | ",
        new Lexer(input, File).Tokenize().Select(t =>
            $"{t.Kind}:{t.Text}@{t.Location.Offset}/{t.Location.Line}/{t.Location.Column}/{t.Location.Source}"));

    [Fact]
    public void CombiningMarkStaysInIdentifier()
    {
        var expected = string.Join(" | ",
            At("LetKeyword", "let", 0, 1, 1),
            At("Identifier", "cafe\u0301", 4, 1, 5),
            At("Equals", "=", 10, 1, 11),
            At("IntegerLiteral", "3", 12, 1, 13),
            At("Semicolon", ";", 13, 1, 14),
            At("Identifier", "cafe\u0301", 15, 2, 1),
            At("Semicolon", ";", 20, 2, 6),
            At("EOF", "", 21, 2, 7));
        var actual = Vector(Witness);
        Assert.True(expected == actual, $"expected [{expected}], actual [{actual}]");

        Assert.Equal(At("Identifier", "@e\u0301", 0, 1, 1) + " | " + At("EOF", "", 3, 1, 4),
            Vector("@e\u0301"));
        Assert.Equal(At("Identifier", "if\u0301", 0, 1, 1) + " | " + At("EOF", "", 3, 1, 4),
            Vector("if\u0301"));
    }
}
