using System.Linq;
using Xunit;

namespace TypeSharp.Syntax.Tests;

public sealed class LexerUnicodeEscapeTests
{
    [Theory]
    [InlineData("fixed-width", "\"\\u0041B\"", "0041 0042")]
    [InlineData("braced-pair", "'\\u{1f600}!'", "D83D DE00 0021")]
    [InlineData("fixed-pair", "\"\\uD83D\\uDE00\"", "D83D DE00")]
    [InlineData("fixed-lone", "\"\\uD800\"", "D800")]
    [InlineData("braced-lone", "'\\u{dfff}'", "DFFF")]
    [InlineData("braced-max", "'\\u{00000000000010FFFF}'", "DBFF DFFF")]
    [InlineData("cooked-data", "\"\\u0000\\u000A\\u0022\"", "0000 000A 0022")]
    [InlineData("hex", "\"\\x41\\x4a\"", "0041 004A")]
    [InlineData("escaped-backslash", "\"\\\\u0041\"", "005C 0075 0030 0030 0034 0031")]
    [InlineData("fixed-nonhex", "\"\\u01G2\"", "0075 0030 0031 0047 0032")]
    [InlineData("fixed-short", "'\\u123'", "0075 0031 0032 0033")]
    [InlineData("braced-empty", "'\\u{}'", "0075 007B 007D")]
    [InlineData("braced-unclosed", "'\\u{12'", "0075 007B 0031 0032")]
    [InlineData("braced-over-max", "'\\u{110000}'", "0075 007B 0031 0031 0030 0030 0030 0030 007D")]
    [InlineData("braced-overflow", "'\\u{100000041}'", "0075 007B 0031 0030 0030 0030 0030 0030 0030 0034 0031 007D")]
    public void CookedStringKeepsRawTokenBoundaries(string id, string literal, string expectedUnits)
    {
        var tokens = new Lexer(literal + " z").Tokenize();
        Assert.Equal(new[] { TokenKind.StringLiteral, TokenKind.Identifier, TokenKind.EOF },
            tokens.Select(token => token.Kind).ToArray());
        Assert.Equal("z", tokens[1].Text);
        AssertPosition(tokens[0].Location, 0);
        AssertPosition(tokens[1].Location, literal.Length + 1);
        AssertPosition(tokens[2].Location, literal.Length + 2);

        var value = Assert.IsType<string>(tokens[0].Value);
        var valueUnits = Units(value);
        var textUnits = Units(tokens[0].Text);
        Assert.True(expectedUnits == valueUnits && expectedUnits == textUnits,
            $"{id}: expected UTF-16 [{expectedUnits}]; Value [{valueUnits}]; Text [{textUnits}]");
    }

    private static string Units(string text) => string.Join(" ", text.Select(unit => ((int)unit).ToString("X4")));

    private static void AssertPosition(SourceLocation location, int offset)
    {
        Assert.Equal(offset, location.Offset);
        Assert.Equal(offset + 1, location.Column);
        Assert.Equal(1, location.Line);
    }
}
