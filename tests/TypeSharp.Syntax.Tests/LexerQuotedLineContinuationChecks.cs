using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TypeSharp.Syntax;

namespace TypeSharp.Syntax.Tests;

internal static class LexerQuotedLineContinuationChecks
{
    internal const string Marker = "C294_QUOTED_LINE_CONTINUATION";

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Control(Action<string>? report = null) =>
        Check(false, report, ("LF", "\n"), ("CRLF", "\r\n"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Regression(Action<string>? report = null) =>
        Check(true, report, ("CR", "\r"), ("LS", "\u2028"), ("PS", "\u2029"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Check(bool regression, Action<string>? report,
        params (string Id, string Separator)[] separators)
    {
        var failures = new List<string>();
        var shapeFailures = new List<string>();
        foreach (char quote in new[] { '\'', '"' })
        foreach (var (id, separator) in separators)
        {
            string row = (quote == '\'' ? "single" : "double") + "/" + id;
            string source = quote + "ab" + "\\" + separator + "cd" + quote + " let x=1;";
            var tokens = new Lexer(source, "quoted-continuation-" + row).Tokenize();
            if (tokens.Count < 2)
            {
                shapeFailures.Add(row + ": missing tokens");
                continue;
            }

            var literal = tokens[0];
            var next = tokens[1];
            var start = literal.Location;
            var end = next.Location;
            var wrong = new List<string>();
            if (literal.Kind != TokenKind.StringLiteral ||
                next.Kind != TokenKind.LetKeyword ||
                !string.Equals(next.Text, "let", StringComparison.Ordinal))
                shapeFailures.Add(row + ": unexpected token identity");
            if (!string.Equals(literal.Text, "abcd", StringComparison.Ordinal)) wrong.Add("Text");
            if (literal.Value is not string value || !string.Equals(value, "abcd", StringComparison.Ordinal)) wrong.Add("Value");
            if (start.Line != 1) wrong.Add("Start.Line");
            if (start.Column != 1) wrong.Add("Start.Column");
            if (start.Offset != 0) wrong.Add("Start.Offset");
            if (end.Line != 2) wrong.Add("Next.Line");
            if (end.Column != 5) wrong.Add("Next.Column");
            if (end.Offset != 8 + separator.Length) wrong.Add("Next.Offset");

            string observed = $"{row} {literal.Kind} text={CodeUnits(literal.Text)} value={CodeUnits(literal.Value as string)} " +
                $"start={start.Line},{start.Column},{start.Offset} next={next.Kind}/{CodeUnits(next.Text)} " +
                $"at={end.Line},{end.Column},{end.Offset} fields={(wrong.Count == 0 ? "-" : string.Join(",", wrong))}";
            report?.Invoke("ROW " + observed);
            if (wrong.Count != 0)
                failures.Add(observed);
        }

        if (shapeFailures.Count != 0)
            throw new InvalidOperationException("Unexpected token shape:\n" + string.Join("\n", shapeFailures));
        if (failures.Count != 0)
            throw new InvalidOperationException((regression ? Marker : "Quoted line continuation control failed") +
                "\n" + string.Join("\n", failures));
    }

    private static string CodeUnits(string? value) => value is null
        ? "<not-string>"
        : string.Join(",", value.Select(c => ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));
}
