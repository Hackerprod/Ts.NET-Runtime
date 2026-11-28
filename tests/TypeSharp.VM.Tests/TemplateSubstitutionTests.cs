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

public sealed class TemplateSubstitutionTests
{
    internal sealed record Row(string Id, string Source, string Expected, int Concat, string? Stock = null);

    internal static IEnumerable<Row> Rows()
    {
        yield return Run("integer", "let n = 37; return `${n}`;", "string:\"37\"", 1, "number:37");
        yield return new("typed-main", "function main(): string { let n = 19; return `${n}`; }", "string:\"19\"", 1, "number:19");
        yield return Run("false", "let flag = false; return `${flag}`;", "string:\"false\"", 1, "boolean:false");
        yield return Run("null", "return `${null}`;", "string:\"null\"", 1, "null");
        yield return Run("undefined", "return `${undefined}`;", "string:\"undefined\"", 1, "undefined");
        yield return Run("member", "let s = 'four'; return `${s.length}`;", "string:\"4\"", 1, "number:4");
        yield return Run("once", "let n = 4; let s = `${++n}`; return [n, s];", "[number:5,string:\"5\"]", 1, "[number:5,number:5]");
        yield return Run("array-place", "let n = 17; return [n, `${n}`];", "[number:17,string:\"17\"]", 1, "[number:17,number:17]");
        yield return Run("string", "let s = 'abc'; return `${s}`;", "string:\"abc\"", 1, "string:\"abc\"");
        yield return Run("text", "return `plain`;", "string:\"plain\"");
        yield return Run("empty", "return ``;", "string:\"\"");
        yield return Run("prefix", "return `p${37}`;", "string:\"p37\"", 1);
        yield return Run("suffix", "return `${37}s`;", "string:\"37s\"", 1);
        yield return Run("adjacent", "return `${37}${false}`;", "string:\"37false\"", 1);
        yield return Run("longer", "return `p${37}:${false}s`;", "string:\"p37:falses\"", 4);
        yield return Run("escaped-only", """return `\${label}`;""", "string:\"${label}\"");
        yield return Run("escaped-dollar", """let label = "ready"; return `draft:\${label} / live:${label}`;""", "string:\"draft:${label} / live:ready\"", 1);
        yield return Run("escaped-slash", """let label = "ready"; return `live:\\${label}`;""", "string:\"live:\\\\ready\"", 1);
        yield return Run("assertion", "return 37 as unknown as string;", "number:37");
        yield return Run("numeric-plus", "return 37 + 1;", "number:38");
        yield return new("cooked-member-alone", """function main(): string { let x: string = "hey"; return `${x /* decoded\n */ !.length}`; }""", "string:\"3\"", 1, "number:3");
        yield return new("cooked-member-plus", """function main(): string { let x: string = "hey"; return `${x /* decoded\n */ !.length}` + ""; }""", "string:\"3\"", 2, "string:\"3\"");
    }

    private static Row Run(string id, string body, string expected, int concat = 0, string? stock = null) =>
        new(id, $"function main() {{ {body} }}", expected, concat, stock);

    internal static (string Value, int Concat) Observe(Row row)
    {
        var tokens = new Lexer(row.Source).Tokenize();
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Error);
        var parser = new ScriptParser(tokens);
        var program = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var main = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(program.Members));
        Assert.Equal("main", main.Name);
        Assert.Empty(main.Parameters);
        var statements = Assert.IsType<BlockStatementSyntax>(main.Body).Statements;
        var expression = Assert.IsType<ReturnStatementSyntax>(statements[^1]).Value;
        if (row.Id is "integer" or "typed-main" or "member" or "cooked-member-alone" or "cooked-member-plus")
        {
            if (row.Id == "cooked-member-plus")
            {
                var plus = Assert.IsType<BinaryExpressionSyntax>(expression);
                Assert.Equal(TokenKind.Plus, plus.OperatorToken.Kind);
                expression = plus.Left;
            }
            var template = Assert.IsType<TemplatePartSyntax>(expression);
            var token = Assert.Single(tokens, t => t.Kind == TokenKind.TemplateLiteral);
            Assert.Equal(token.Location.Offset, template.Range.Start.Offset);
            Assert.Equal(token.Location.Offset, template.Range.End.Offset);
            Assert.Same(template.Expression, Assert.Single(template.GetChildren()));
            if (row.Id.StartsWith("cooked-member", StringComparison.Ordinal))
            {
                Assert.True(row.Source.Contains("/* decoded\\n */", StringComparison.Ordinal));
                Assert.True(template.Expression is MemberAccessExpressionSyntax
                    { MemberName: "length", Object: NonNullAssertionSyntax { Expression: IdentifierExpressionSyntax { Name: "x" } } });
            }
        }
        var binder = new Binder();
        var bound = binder.Bind(program);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var entry = Assert.Single(ir.Functions, f => f.Name == "main");
        Assert.Empty(entry.Parameters);
        int concat = entry.Blocks.SelectMany(b => b.Instructions).Count(i => i.Opcode == Opcode.ConcatString);
        var bytecode = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(bytecode);
        return (Format(new Vm().Execute(bytecode, "main", [])), concat);
    }

    private static string Format(TsValue? value) => value switch
    {
        TsStringValue s => "string:" + JsonSerializer.Serialize(s.Value),
        TsInt32Value n => "number:" + JsonSerializer.Serialize(n.Value),
        TsFloat64Value n => "number:" + JsonSerializer.Serialize(n.Value),
        TsBoolValue b => "boolean:" + JsonSerializer.Serialize(b.Value),
        TsNull => "null",
        TsVoid => "undefined",
        TsArrayValue a => "[" + string.Join(",", Enumerable.Range(0, a.Value.Count).Select(i => Format(a.Value.Get(i)))) + "]",
        _ => throw new InvalidOperationException($"Unexpected runtime value type: {value?.GetType()}")
    };

    [Fact]
    public void InterpolationsReturnRuntimeStringsAndEvaluateOnce()
    {
        var failures = new List<string>();
        foreach (var row in Rows())
        {
            var observed = Observe(row);
            if (observed != (row.Expected, row.Concat))
                failures.Add(row.Id);
        }

        Assert.True(failures.Count == 0, "Template interpolation runtime value/concat: " + string.Join(" ", failures));
    }
}
