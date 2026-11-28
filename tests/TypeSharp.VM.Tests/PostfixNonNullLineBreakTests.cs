using System.Text;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Syntax;
using TypeSharp.Syntax.SyntaxTree;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using ScriptParser = TypeSharp.Syntax.Parser.Parser;
using Vm = TypeSharp.VM.Interpreter.Interpreter;

namespace TypeSharp.VM.Tests;

public sealed class PostfixNonNullLineBreakTests
{
    internal sealed record Row(string Id, string Source, string Shape, string Value, bool Red = false);

    internal static IEnumerable<Row> Rows()
    {
        const string negate = "!x && (x = true);";
        foreach (var (id, trivia) in new (string, string)[]
        {
            ("lf", "\n"), ("cr", "\r"), ("crlf", "\r\n"), ("ls", "\u2028"), ("ps", "\u2029"),
            ("block-lf", " /* comment\n */ "), ("line-lf", " // comment\n"),
        })
            yield return Bool(id, "x" + trivia + negate, "split", "true", true);
        yield return Bool("paren-break", "(x\n)\n" + negate, "split", "true", true);
        yield return Bool("template-break", "`a\nb`\n" + negate, "split-template", "true", true);
        yield return Bool("explicit-semi", "x; " + negate, "split", "true");
        yield return Bool("same-line", "x!; " + negate, "postfix", "true");
        yield return Bool("inline-block", "x /* comment */ !; " + negate, "postfix", "true");
        yield return Bool("paren-same", "(x\n)!; " + negate, "postfix", "true");
        yield return Bool("no-negation", "x; x && (x = true);", "no-negation", "false");
        yield return new("prefix", "function main(): boolean { return !false; }", "prefix", "true");
        yield return new("equality", "function main(): boolean { return 1 != 2 && 2 !== 3; }", "equality", "true");
        yield return new("template-postfix", "function main(): string { return `a\nb`!; }", "return-postfix", "\"a\\nb\"");
        yield return new("quoted-postfix", "function main(): string { return 'a\\\nb'!; }", "return-postfix", "\"ab\"");
        yield return new("cooked-bang", """function main(): string { let x: string = "hey"; return `${x /* decoded\n */ !}`; }""", "template-bang", "\"hey\"");
        yield return new("cooked-member", """function main(): string { let x: string = "hey"; return `${x /* decoded\n */ !.length}` + ""; }""", "template-member", "\"3\"");
        yield return Update("inc-break", "a = b\n++c;", "447");
        yield return Update("dec-break", "a = b\n--c;", "445");
        yield return Update("inc-postfix", "a = b++;", "456");
        yield return Update("dec-postfix", "a = b--;", "436");
        yield return new("escaped-dollar", """function main(): string { let label: string = "ready"; return `draft:\${label} / live:${label}`; }""", "runtime", "\"draft:${label} / live:ready\"");
        yield return new("normal-dollar", """function main(): string { let label: string = "ready"; return `live:${label}`; }""", "runtime", "\"live:ready\"");
        yield return new("escaped-slash", """function main(): string { let label: string = "ready"; return `live:\\${label}`; }""", "runtime", "\"live:\\\\ready\"");
    }

    private static Row Bool(string id, string body, string shape, string value, bool red = false) =>
        new(id, $"function main(): boolean {{ let x = false; {body} return x; }}", shape, value, red);

    private static Row Update(string id, string body, string value) =>
        new(id, $"function main(): number {{ let a: number = 1; let b: number = 4; let c: number = 6; {body} return a * 100 + b * 10 + c; }}", "runtime", value);

    private static IEnumerable<SyntaxNode> Descendants(SyntaxNode node) =>
        new[] { node }.Concat(node.GetChildren().SelectMany(Descendants));

    private static bool IsBang(ExpressionSyntax? value) =>
        value is UnaryExpressionSyntax { IsPrefix: true, OperatorToken.Kind: TokenKind.Bang };

    private static string Shape(Row row, List<SyntaxNode> statements)
    {
        var ret = Assert.IsType<ReturnStatementSyntax>(statements[^1]).Value!;
        switch (row.Shape)
        {
            case "runtime": return "runtime";
            case "return-postfix": return ret is NonNullAssertionSyntax ? "return-postfix" : "other";
            case "prefix": return IsBang(ret) ? "prefix" : "other";
            case "equality":
                return ret is BinaryExpressionSyntax
                {
                    OperatorToken.Kind: TokenKind.AmpersandAmpersand,
                    Left: BinaryExpressionSyntax { OperatorToken.Text: "!=" },
                    Right: BinaryExpressionSyntax { OperatorToken.Text: "!==" }
                } ? "equality" : "other";
            case "template-bang":
            case "template-member":
                byte[] bytes = Encoding.UTF8.GetBytes(row.Source);
                var start = bytes.AsSpan().IndexOf("/*"u8);
                var end = bytes.AsSpan().IndexOf("*/"u8);
                Assert.True(start >= 0 && end > start);
                Assert.True(bytes.AsSpan(start, end + 2 - start).SequenceEqual("/* decoded\\n */"u8),
                    "Raw comment contains byte 5C followed by byte 6E and no source line break");
                var inner = Assert.Single(Descendants(ret).OfType<TemplatePartSyntax>()).Expression;
                return inner switch
                {
                    NonNullAssertionSyntax { Expression: IdentifierExpressionSyntax } => "template-bang",
                    MemberAccessExpressionSyntax { MemberName: "length", Object: NonNullAssertionSyntax { Expression: IdentifierExpressionSyntax } } => "template-member",
                    _ => "other",
                };
            default:
                Assert.Equal(4, statements.Count);
                var first = Assert.IsType<ExpressionStatementSyntax>(statements[1]).Expression;
                var last = Assert.IsType<BinaryExpressionSyntax>(
                    Assert.IsType<ExpressionStatementSyntax>(statements[2]).Expression);
                Assert.Equal(TokenKind.AmpersandAmpersand, last.OperatorToken.Kind);
                return (first, IsBang(last.Left), last.Left is IdentifierExpressionSyntax) switch
                {
                    (NonNullAssertionSyntax { Expression: IdentifierExpressionSyntax }, true, false) => "postfix",
                    (IdentifierExpressionSyntax, true, false) => "split",
                    (LiteralExpressionSyntax, true, false) => "split-template",
                    (IdentifierExpressionSyntax, false, true) => "no-negation",
                    (NonNullAssertionSyntax { Expression: IdentifierExpressionSyntax or LiteralExpressionSyntax }, false, true) => "stolen-bang",
                    _ => "other",
                };
        }
    }

    internal static (string Shape, string Value) Observe(Row row)
    {
        var tokens = new Lexer(row.Source).Tokenize();
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Error);
        var parser = new ScriptParser(tokens);
        var program = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var mainSyntax = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(program.Members));
        Assert.Equal("main", mainSyntax.Name);
        Assert.Empty(mainSyntax.Parameters);
        var binder = new Binder();
        var bound = binder.Bind(program);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        Assert.Empty(Assert.Single(ir.Functions, f => f.Name == "main").Parameters);
        var bytecode = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(bytecode);
        var actual = new Vm().Execute(bytecode, "main", []);
        string value = row.Value.StartsWith('\"')
            ? JsonSerializer.Serialize(Assert.IsType<TsStringValue>(actual).Value)
            : row.Value is "true" or "false" ? JsonSerializer.Serialize(Assert.IsType<TsBoolValue>(actual).Value)
            : actual switch
            {
                TsFloat64Value n => JsonSerializer.Serialize(n.Value),
                TsInt32Value n => JsonSerializer.Serialize(n.Value),
                _ => throw new InvalidOperationException($"Unexpected numeric VM result: {actual?.GetType()}"),
            };
        return (Shape(row, Assert.IsType<BlockStatementSyntax>(mainSyntax.Body).Statements), value);
    }

    [Fact]
    public void PostfixBangDoesNotCrossAnOrdinarySourceLineTerminator()
    {
        var failed = new List<string>();
        foreach (Row row in Rows())
        {
            var actual = Observe(row);
            if (actual != (row.Shape, row.Value))
                failed.Add($"{row.Id}:{actual.Shape}:{actual.Value}");
        }
        Assert.True(failed.Count == 0, "C312 " + string.Join(" ", failed));
    }
}
