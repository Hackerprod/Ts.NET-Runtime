using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeSharp.Hosting.Compilation;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Diagnostics;
using TypeSharp.Syntax.Parser;
using TypeSharp.Syntax.SyntaxTree;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Tests;

public sealed class ArrayLiteralMissingCommaIntegrationTests(ITestOutputHelper output)
{
    private const string Marker = "C212_ARRAY_LITERAL_MISSING_COMMA";
    private const string MissingComma = "Expected Comma, got IntegerLiteral '7'";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [Fact]
    public void Compilation_RejectsMissingCommaBetweenArrayElements()
    {
        Assert.True(IsClean(ObserveLiteral("comma", "[4, 7]", "IntegerLiteral:4", "IntegerLiteral:7")));
        Assert.True(IsClean(ObserveLiteral("trailing", "[4, 7,]", "IntegerLiteral:4", "IntegerLiteral:7")));
        Assert.True(IsClean(ObserveLiteral("empty", "[]")));
        Assert.True(IsClean(ObserveLiteral("spread", "[4, ...[7],]", "IntegerLiteral:4", "spread(array(IntegerLiteral:7))")));
        Assert.True(IsError(
            ObserveLiteral("existing malformed elision", "[4, , 7]", "IntegerLiteral:4", "Comma:,", "IntegerLiteral:7"),
            "Unexpected token: Comma ','", 54, 53));

        Assert.True(IsClean(ObserveAssignment("assignment-target",
            "export function observe(): number { let a = 0; let b = 0; [a b] = [4, 7]; return 9; }")));
        var nested = ObserveAssignment("nested-default-target",
            "export function observe(): number { let a = 0; let b = 0; [[a b] = [0 0]] = [[4, 7]]; return 9; }",
            nestedDefault: true);
        Assert.Empty(nested.Parser);
        Assert.Null(nested.Compilation);

        var missing = ObserveLiteral("missing", "[4 7]", "IntegerLiteral:4", "IntegerLiteral:7");
        var rhs = ObserveAssignment("assignment-rhs",
            "export function observe(): number { let a = 0; let b = 0; [a b] = [4 7]; return 9; }");

        var missingRejects = IsError(missing, MissingComma, 53, 52);
        var rhsRejects = IsError(rhs, MissingComma, 70, 69);
        Assert.True((IsClean(missing) && IsClean(rhs)) || (missingRejects && rhsRejects),
            "Expected either both clean baseline routes or both precise new parser/compiler errors.");
        Assert.True(missingRejects && rhsRejects,
            "C212_ARRAY_LITERAL_MISSING_COMMA_RESULT expected both literal and assignment RHS to reject the missing comma");
    }

    private Observation ObserveLiteral(string row, string expression, params string[] expectedElements) =>
        Observe(row, $"export function observe(): number {{ let values = {expression}; return 9; }}",
            (tokens, statements, path) =>
            {
                Assert.Equal(2, statements.Count);
                var local = Assert.IsType<VariableDeclarationSyntax>(statements[0]);
                Assert.Equal("values", local.Name);
                var array = Assert.IsType<ArrayLiteralExpressionSyntax>(local.Initializer);
                var returnToken = GetReturnToken(statements[1]);
                Log(row, "ast", new
                {
                    local = local.Name,
                    initializer = array.GetType().Name,
                    elements = array.Elements.Select(Describe).ToArray(),
                    returned = Snapshot(returnToken)
                });

                Assert.Equal(expectedElements, array.Elements.Select(Describe).ToArray());
                Assert.Equal("9", returnToken.Text);
                if (row == "missing")
                {
                    AssertToken(tokens, path, TokenKind.IntegerLiteral, "4", 51, 50);
                    AssertToken(tokens, path, TokenKind.IntegerLiteral, "7", 53, 52);
                    AssertPosition(Assert.IsType<LiteralExpressionSyntax>(array.Elements[1]).Token.Location, path, 53, 52);
                    AssertPosition(returnToken.Location, path, 64, 63);
                }
            });

    private Observation ObserveAssignment(string row, string source, bool nestedDefault = false) =>
        Observe(row, source, (tokens, statements, path) =>
        {
            Assert.Equal(4, statements.Count);
            var first = Assert.IsType<VariableDeclarationSyntax>(statements[0]);
            var second = Assert.IsType<VariableDeclarationSyntax>(statements[1]);
            var assignment = Assert.IsType<AssignmentExpressionSyntax>(
                Assert.IsType<ExpressionStatementSyntax>(statements[2]).Expression);
            var target = Assert.IsType<ArrayLiteralExpressionSyntax>(assignment.Target);
            var value = Assert.IsType<ArrayLiteralExpressionSyntax>(assignment.Value);
            var returnToken = GetReturnToken(statements[3]);

            Log(row, "ast", new
            {
                locals = new[] { first.Name, second.Name },
                assignment = Describe(assignment),
                returned = Snapshot(returnToken)
            });

            Assert.Equal("a", first.Name);
            Assert.Equal("b", second.Name);
            Assert.Equal("IntegerLiteral:0", Describe(first.Initializer));
            Assert.Equal("IntegerLiteral:0", Describe(second.Initializer));
            Assert.Equal(TokenKind.Equals, assignment.OperatorToken.Kind);
            Assert.Equal("9", returnToken.Text);

            if (nestedDefault)
            {
                var defaultAssignment = Assert.IsType<AssignmentExpressionSyntax>(Assert.Single(target.Elements));
                Assert.Equal(TokenKind.Equals, defaultAssignment.OperatorToken.Kind);
                AssertIdentifiers(Assert.IsType<ArrayLiteralExpressionSyntax>(defaultAssignment.Target));
                var defaultValue = Assert.IsType<ArrayLiteralExpressionSyntax>(defaultAssignment.Value);
                Assert.Equal(new[] { "IntegerLiteral:0", "IntegerLiteral:0" }, defaultValue.Elements.Select(Describe).ToArray());
                AssertNumericPair(Assert.IsType<ArrayLiteralExpressionSyntax>(Assert.Single(value.Elements)));
            }
            else
            {
                AssertIdentifiers(target);
                AssertNumericPair(value);
            }

            if (row == "assignment-rhs")
            {
                AssertToken(tokens, path, TokenKind.Identifier, "b", 62, 61);
                AssertToken(tokens, path, TokenKind.IntegerLiteral, "7", 70, 69);
                AssertPosition(Assert.IsType<LiteralExpressionSyntax>(value.Elements[1]).Token.Location, path, 70, 69);
            }
        }, compile: !nestedDefault);

    private Observation Observe(string row, string source,
        Action<IReadOnlyList<Token>, IReadOnlyList<SyntaxNode>, string> inspectAst, bool compile = true)
    {
        var directory = Directory.CreateTempSubdirectory("typesharp-c212-");
        try
        {
            var path = Path.Combine(directory.FullName, "app.ts");
            var expectedBytes = Utf8.GetBytes(source + "\n");
            File.WriteAllBytes(path, expectedBytes);
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
            Assert.Equal(expectedBytes, bytes);

            var tokens = new Lexer(text, path).Tokenize();
            var errors = tokens.Where(token => token.Kind == TokenKind.Error).Select(Snapshot).ToArray();
            Log(row, "lexer", new { errorCount = errors.Length, errors, tokens = tokens.Select(Snapshot).ToArray() });
            Assert.Empty(errors);

            var parser = new Parser(tokens);
            var tree = parser.Parse(path);
            var parserDiagnostics = parser.Diagnostics.Select(Snapshot).ToArray();
            Log(row, "parser", new { count = parserDiagnostics.Length, diagnostics = parserDiagnostics });

            var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Members));
            Assert.Equal("observe", function.Name);
            var body = Assert.IsType<BlockStatementSyntax>(function.Body);
            inspectAst(tokens, body.Statements, path);

            if (!compile)
                return new Observation(path, parserDiagnostics, null);

            var compilation = new TypeScriptCompilation(directory.FullName);
            compilation.AddSourceFile(path);
            var modules = compilation.Compile().Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var compilationDiagnostics = compilation.Diagnostics.All.Select(Snapshot).ToArray();
            var observedCompilation = new CompilationSnapshot(compilationDiagnostics, compilation.Diagnostics.HasErrors, modules);
            Log(row, "compile", new
            {
                count = compilationDiagnostics.Length,
                diagnostics = compilationDiagnostics,
                hasErrors = observedCompilation.HasErrors,
                modules
            });
            return new Observation(path, parserDiagnostics, observedCompilation);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static Token GetReturnToken(SyntaxNode statement)
    {
        var returned = Assert.IsType<ReturnStatementSyntax>(statement);
        var literal = Assert.IsType<LiteralExpressionSyntax>(returned.Value);
        Assert.Equal(TokenKind.IntegerLiteral, literal.Token.Kind);
        return literal.Token;
    }

    private static void AssertIdentifiers(ArrayLiteralExpressionSyntax target)
    {
        Assert.Equal(2, target.Elements.Count);
        Assert.Equal("a", Assert.IsType<IdentifierExpressionSyntax>(target.Elements[0]).Name);
        Assert.Equal("b", Assert.IsType<IdentifierExpressionSyntax>(target.Elements[1]).Name);
    }

    private static void AssertNumericPair(ArrayLiteralExpressionSyntax value) =>
        Assert.Equal(new[] { "IntegerLiteral:4", "IntegerLiteral:7" }, value.Elements.Select(Describe).ToArray());

    private static string Describe(ExpressionSyntax? expression) => expression switch
    {
        LiteralExpressionSyntax literal => $"{literal.Token.Kind}:{literal.Token.Text}",
        IdentifierExpressionSyntax identifier => $"identifier:{identifier.Name}",
        ArrayLiteralExpressionSyntax array => $"array({string.Join(",", array.Elements.Select(Describe))})",
        SpreadExpressionSyntax spread => $"spread({Describe(spread.Expression)})",
        AssignmentExpressionSyntax assignment =>
            $"assignment({assignment.OperatorToken.Kind},{Describe(assignment.Target)},{Describe(assignment.Value)})",
        _ => expression?.GetType().Name ?? "<none>"
    };

    private static void AssertToken(IReadOnlyList<Token> tokens, string path, TokenKind kind, string text, int column, int offset)
    {
        var token = Assert.Single(tokens.Where(token => token.Location.Offset == offset));
        Assert.Equal(kind, token.Kind);
        Assert.Equal(text, token.Text);
        AssertPosition(token.Location, path, column, offset);
    }

    private static void AssertPosition(SourceLocation location, string path, int column, int offset)
    {
        Assert.Equal(path, location.Source);
        Assert.Equal(1, location.Line);
        Assert.Equal(column, location.Column);
        Assert.Equal(offset, location.Offset);
    }

    private static bool IsClean(Observation observation) =>
        observation.Parser.Length == 0 &&
        observation.Compilation is { HasErrors: false, Diagnostics.Length: 0, Modules: ["app"] };

    private static bool IsError(Observation observation, string message, int column, int offset) =>
        observation.Parser is [var parserError] &&
        parserError == new DiagnosticSnapshot("Error", "None", null, observation.Path, 1, column, offset, message) &&
        observation.Compilation is { HasErrors: true, Diagnostics: [var compilerError], Modules.Length: 0 } &&
        compilerError == new DiagnosticSnapshot("Error", "None", "app", observation.Path, 1, column, offset, message);

    private void Log(string row, string stage, object observed) =>
        output.WriteLine($"{Marker} {JsonSerializer.Serialize(new { row, stage, observed })}");

    private static TokenSnapshot Snapshot(Token token) =>
        new(token.Kind.ToString(), token.Text, token.Location.Source, token.Location.Line, token.Location.Column, token.Location.Offset);

    private static DiagnosticSnapshot Snapshot(Diagnostic diagnostic) =>
        new(diagnostic.Severity.ToString(), diagnostic.Code.ToString(), diagnostic.ModuleId, diagnostic.Location.Source,
            diagnostic.Location.Line, diagnostic.Location.Column, diagnostic.Location.Offset, diagnostic.Message);

    private sealed record Observation(string Path, DiagnosticSnapshot[] Parser, CompilationSnapshot? Compilation);
    private sealed record CompilationSnapshot(DiagnosticSnapshot[] Diagnostics, bool HasErrors, string[] Modules);
    private sealed record DiagnosticSnapshot(string Severity, string Code, string? ModuleId, string? Source,
        int Line, int Column, int Offset, string Message);
    private sealed record TokenSnapshot(string Kind, string Text, string? Source, int Line, int Column, int Offset);
}
