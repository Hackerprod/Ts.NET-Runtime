using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TypeSharp.Hosting;
using TypeSharp.Hosting.Compilation;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Diagnostics;
using TypeSharp.Syntax.Parser;
using TypeSharp.Syntax.SyntaxTree;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Tests;

public sealed class UnparenthesizedNegationExponentIntegrationTests(ITestOutputHelper output)
{
    private const string Marker = "C216_UNPARENTHESIZED_NEGATION_EXPONENT";
    private const string ExpectedMessage =
        "Parentheses are required around a unary minus expression used as the base of exponentiation";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [Fact]
    public async Task Compilation_RejectsBareUnaryMinusAsExponentBase()
    {
        var bare = await Observe("bare", "-2 ** 2",
            new NegativePositions((45, 44), (46, 45), (48, 47), (51, 50), (61, 60)));
        var operand = await Observe("grouped-operand-only", "-(2) ** 2",
            new NegativePositions((45, 44), (47, 46), (50, 49), (53, 52), (63, 62)));

        await Observe("grouped-base", "(-2) ** 2", shape: "Pow(Neg(2),2)", nativeValue: 4);
        await Observe("grouped-power", "-(2 ** 2)", shape: "Neg(Pow(2,2))", nativeValue: -4);
        await Observe("negative-exponent", "2 ** -2", shape: "Pow(2,Neg(2))", nativeValue: 0.25);
        await Observe("right-associative", "2 ** 3 ** 2", shape: "Pow(2,Pow(3,2))", nativeValue: 512);
        await Observe("plain-negation", "-2", shape: "Neg(2)", nativeValue: -2);

        var bothBase = IsClean(bare) && IsClean(operand);
        var bothGreen = IsExactError(bare, 48, 47) && IsExactError(operand, 50, 49);
        Log("negative-pair", "pre-verdict", new { bothBase, bothGreen });
        Assert.True(bothBase || bothGreen, "Expected either both stock clean compilations or both exact parser/compiler rejections.");
        Assert.True(bothGreen,
            "C216_UNPARENTHESIZED_NEGATION_EXPONENT_RESULT expected both syntactically bare unary-minus bases to reject exponentiation");
    }

    private async Task<Observation> Observe(string row, string expression, NegativePositions? negative = null,
        string? shape = null, double? nativeValue = null)
    {
        var directory = Directory.CreateTempSubdirectory("typesharp-c216-");
        try
        {
            var path = Path.Combine(directory.FullName, "app.ts");
            var source = negative is null
                ? $"export function observe(): number {{ return {expression}; }}\n"
                : $"export function observe(): number {{ let n = {expression}; return 9; }}\n";
            var expected = Utf8.GetBytes(source);
            File.WriteAllBytes(path, expected);
            var bytes = File.ReadAllBytes(path);
            var text = Utf8.GetString(bytes);
            Log(row, "source", new
            {
                path,
                bytes = bytes.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                text
            });
            Assert.True(Path.IsPathFullyQualified(path));
            Assert.Equal(expected, bytes);

            var tokens = new Lexer(text, path).Tokenize();
            var lexerErrors = tokens.Where(token => token.Kind == TokenKind.Error).Select(Snapshot).ToArray();
            Log(row, "lexer", new { errorCount = lexerErrors.Length, errors = lexerErrors, tokens = tokens.Select(Snapshot).ToArray() });
            Assert.Empty(lexerErrors);

            var parser = new Parser(tokens);
            var tree = parser.Parse(path);
            var parserDiagnostics = parser.Diagnostics.Select(Snapshot).ToArray();
            Log(row, "parser", new { count = parserDiagnostics.Length, diagnostics = parserDiagnostics });

            var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Members));
            Assert.Equal("observe", function.Name);
            var body = Assert.IsType<BlockStatementSyntax>(function.Body);
            if (negative is not null)
            {
                InspectRecovery(row, path, tokens, function.Name, body, negative);
            }
            else
            {
                var returned = Assert.IsType<ReturnStatementSyntax>(Assert.Single(body.Statements));
                var actualShape = Describe(returned.Value);
                Log(row, "ast", new { function = function.Name, statements = new[] { returned.GetType().Name }, shape = actualShape });
                Assert.Equal(shape, actualShape);
            }

            var compilation = new TypeScriptCompilation(directory.FullName);
            compilation.AddSourceFile(path);
            var modules = compilation.Compile().Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var compilationDiagnostics = compilation.Diagnostics.All.Select(Snapshot).ToArray();
            var observation = new Observation(path, parserDiagnostics, compilationDiagnostics, compilation.Diagnostics.HasErrors, modules);
            Log(row, "compile", new
            {
                count = compilationDiagnostics.Length,
                diagnostics = compilationDiagnostics,
                hasErrors = observation.HasErrors,
                modules
            });

            if (negative is null)
            {
                Assert.True(IsClean(observation), $"Expected clean control: {row}");
                await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(directory.FullName).BuildAsync();
                var actual = await runtime.InvokeAsync<double>("app", "observe");
                Log(row, "native", new { expected = nativeValue, actual });
                Assert.Equal(nativeValue!.Value, actual);
            }
            return observation;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private void InspectRecovery(string row, string path, IReadOnlyList<Token> tokens, string function,
        BlockStatementSyntax body, NegativePositions positions)
    {
        Assert.Equal(2, body.Statements.Count);
        var local = Assert.IsType<VariableDeclarationSyntax>(body.Statements[0]);
        Assert.Equal("n", local.Name);
        var power = Assert.IsType<BinaryExpressionSyntax>(local.Initializer);
        var unary = Assert.IsType<UnaryExpressionSyntax>(power.Left);
        var left = Assert.IsType<LiteralExpressionSyntax>(unary.Operand);
        var right = Assert.IsType<LiteralExpressionSyntax>(power.Right);
        var returned = Assert.IsType<ReturnStatementSyntax>(body.Statements[1]);
        var following = Assert.IsType<LiteralExpressionSyntax>(returned.Value);
        Log(row, "ast", new
        {
            function,
            statements = body.Statements.Select(statement => statement.GetType().Name).ToArray(),
            local = local.Name,
            shape = Describe(power),
            prefix = unary.IsPrefix,
            minus = Snapshot(unary.OperatorToken),
            left = Snapshot(left.Token),
            starStar = Snapshot(power.OperatorToken),
            right = Snapshot(right.Token),
            following = Snapshot(following.Token)
        });
        Assert.True(unary.IsPrefix);

        AssertToken(tokens, unary.OperatorToken, path, TokenKind.Minus, "-", positions.Minus);
        AssertToken(tokens, left.Token, path, TokenKind.IntegerLiteral, "2", positions.Left);
        AssertToken(tokens, power.OperatorToken, path, TokenKind.StarStar, "**", positions.StarStar);
        AssertToken(tokens, right.Token, path, TokenKind.IntegerLiteral, "2", positions.Right);
        AssertToken(tokens, following.Token, path, TokenKind.IntegerLiteral, "9", positions.Following);
    }

    private static void AssertToken(IReadOnlyList<Token> tokens, Token astToken, string path, TokenKind kind, string spelling,
        (int Column, int Offset) position)
    {
        var rawToken = Assert.Single(tokens.Where(token => token.Location.Offset == position.Offset));
        var expected = new TokenSnapshot(kind.ToString(), spelling, path, 1, position.Column, position.Offset);
        Assert.Equal(expected, Snapshot(rawToken));
        Assert.Equal(expected, Snapshot(astToken));
    }

    private static string Describe(ExpressionSyntax? expression) => expression switch
    {
        LiteralExpressionSyntax literal when literal.Token.Kind == TokenKind.IntegerLiteral => literal.Token.Text,
        UnaryExpressionSyntax unary when unary.IsPrefix && unary.OperatorToken.Kind == TokenKind.Minus =>
            $"Neg({Describe(unary.Operand)})",
        BinaryExpressionSyntax binary when binary.OperatorToken.Kind == TokenKind.StarStar =>
            $"Pow({Describe(binary.Left)},{Describe(binary.Right)})",
        _ => expression?.GetType().Name ?? "<none>"
    };

    private static bool IsClean(Observation observation) =>
        observation is { Parser.Length: 0, Compiler.Length: 0, HasErrors: false, Modules: ["app"] };

    private static bool IsExactError(Observation observation, int column, int offset) =>
        observation is { Parser: [var parserError], Compiler: [var compilerError], HasErrors: true, Modules.Length: 0 } &&
        parserError == new DiagnosticSnapshot("Error", "None", null, observation.Path, 1, column, offset, ExpectedMessage) &&
        compilerError == new DiagnosticSnapshot("Error", "None", "app", observation.Path, 1, column, offset, ExpectedMessage);

    private void Log(string row, string stage, object observed) =>
        output.WriteLine($"{Marker} {JsonSerializer.Serialize(new { row, stage, observed })}");

    private static TokenSnapshot Snapshot(Token token) =>
        new(token.Kind.ToString(), token.Text, token.Location.Source, token.Location.Line, token.Location.Column, token.Location.Offset);

    private static DiagnosticSnapshot Snapshot(Diagnostic diagnostic) =>
        new(diagnostic.Severity.ToString(), diagnostic.Code.ToString(), diagnostic.ModuleId, diagnostic.Location.Source,
            diagnostic.Location.Line, diagnostic.Location.Column, diagnostic.Location.Offset, diagnostic.Message);

    private sealed record Observation(string Path, DiagnosticSnapshot[] Parser, DiagnosticSnapshot[] Compiler,
        bool HasErrors, string[] Modules);
    private sealed record NegativePositions((int Column, int Offset) Minus, (int Column, int Offset) Left,
        (int Column, int Offset) StarStar, (int Column, int Offset) Right, (int Column, int Offset) Following);
    private sealed record DiagnosticSnapshot(string Severity, string Code, string? ModuleId, string? Source,
        int Line, int Column, int Offset, string Message);
    private sealed record TokenSnapshot(string Kind, string Text, string? Source, int Line, int Column, int Offset);
}
