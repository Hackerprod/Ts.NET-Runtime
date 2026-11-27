using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace TypeSharp.Syntax.Tests;

internal static class LexerConnectorIdentifierChecks
{
    internal const string Marker = "C295_CONNECTOR_IDENTIFIER";
    private const string FileName = "connector-identifier.ts";
    private readonly record struct Item(TokenKind Kind, string Text, string Source, int Offset, int Line, int Column);
    private sealed record Row(string Name, string Source, Item[] Expected, Item[]? Stock = null);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Control(Action<string>? report = null) => Check(report,
        One("keyword", "if", At(TokenKind.IfKeyword, "if", 0)),
        One("ascii", "_a$9", Id("_a$9", 0)),
        One("nonspacing", "e\u0301", Id("e\u0301", 0)),
        One("spacing", "x\u093E", Id("x\u093E", 0)),
        One("legacy-keyword", "@if", Id("@if", 0)),
        One("operator", "a-b", Id("a", 0), At(TokenKind.Minus, "-", 1), Id("b", 2)),
        One("nonconnector", "a\u2053b", Id("a", 0), Err("\u2053", 1), Id("b", 2)),
        One("leading", "\u203Fa", Err("\u203F", 0), Id("a", 1)),
        One("legacy-leading", "@\u203Fa", Id("@", 0), Err("\u203F", 1), Id("a", 2)));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Regression(Action<string>? report = null) => Check(report,
        Middle("U+203F", "\u203F"),
        Middle("U+2040", "\u2040"),
        Middle("U+2054", "\u2054"),
        new Row("keyword-suffix", "if\u203F", End(3, Id("if\u203F", 0)),
            End(3, At(TokenKind.IfKeyword, "if", 0), Err("\u203F", 2))),
        new Row("legacy-middle", "@e\u2040x", End(4, Id("@e\u2040x", 0)),
            End(4, Id("@e", 0), Err("\u2040", 2), Id("x", 3))));

    private static Row Middle(string name, string connector)
    {
        string identifier = "a" + connector + "b";
        var let = At(TokenKind.LetKeyword, "let", 0);
        var equals = At(TokenKind.Equals, "=", 8);
        var number = At(TokenKind.IntegerLiteral, "3", 10);
        var firstSemicolon = At(TokenKind.Semicolon, ";", 11);
        var secondSemicolon = At(TokenKind.Semicolon, ";", 16, 2, 4);
        var eof = At(TokenKind.EOF, "", 17, 2, 5);
        return new Row(name, $"let {identifier} = 3;\n{identifier};",
            new[] { let, Id(identifier, 4), equals, number, firstSemicolon, Id(identifier, 13, 2, 1),
                secondSemicolon, eof },
            new[] { let, Id("a", 4), Err(connector, 5), Id("b", 6), equals, number, firstSemicolon,
                Id("a", 13, 2, 1), Err(connector, 14, 2, 2), Id("b", 15, 2, 3), secondSemicolon, eof });
    }

    private static Row One(string name, string source, params Item[] expected) =>
        new(name, source, End(source.Length, expected));

    private static Item[] End(int offset, params Item[] items) => items.Append(At(TokenKind.EOF, "", offset)).ToArray();

    private static Item At(TokenKind kind, string text, int offset, int line = 1, int? column = null) =>
        new(kind, text, FileName, offset, line, column ?? offset + 1);

    private static Item Id(string text, int offset, int line = 1, int? column = null) =>
        At(TokenKind.Identifier, text, offset, line, column);

    private static Item Err(string text, int offset, int line = 1, int? column = null) =>
        At(TokenKind.Error, text, offset, line, column);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Check(Action<string>? report, params Row[] rows)
    {
        var failures = new List<string>();
        var stockMatches = 0;
        foreach (Row row in rows)
        {
            var actual = new Lexer(row.Source, FileName).Tokenize().Select(t =>
                new Item(t.Kind, t.Text, t.Location.Source, t.Location.Offset, t.Location.Line, t.Location.Column)).ToArray();
            bool pass = actual.SequenceEqual(row.Expected);
            bool stockMatch = !pass && row.Stock is not null && actual.SequenceEqual(row.Stock);
            string status = pass ? "PASS" : stockMatch ? "EXACT_STOCK" : "MISMATCH";
            string detail = $"{row.Name} {status} actual=[{Vector(actual)}] expected=[{Vector(row.Expected)}]";
            report?.Invoke("ROW " + detail);
            if (!pass)
                failures.Add(detail);
            if (stockMatch)
                stockMatches++;
        }

        if (failures.Count > 0)
        {
            string marker = stockMatches == rows.Length ? Marker : "Unexpected connector identifier token vector";
            throw new InvalidOperationException(marker + "\n" + string.Join("\n", failures));
        }
    }

    private static string Vector(IEnumerable<Item> items) => string.Join(";", items.Select(item =>
        FormattableString.Invariant($"{item.Kind}/{Hex(item.Text)}@{item.Source}:{item.Offset}/{item.Line}:{item.Column}")));

    private static string Hex(string text) =>
        string.Join(",", text.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)));
}
