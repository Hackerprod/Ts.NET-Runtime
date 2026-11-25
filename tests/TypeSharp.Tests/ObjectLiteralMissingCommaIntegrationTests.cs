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

public sealed class ObjectLiteralMissingCommaIntegrationTests(ITestOutputHelper output)
{
    private const string Marker = "C221_OBJECT_LITERAL_MISSING_COMMA";
    private const string MissingComma = "Expected Comma, got Identifier 'y'";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Row[] Rows =
    [
        new("literal", "let values = { x: 1 y: 2 }",
            Local(Pair(51, 54, 56, 59, "1", "2")), 71, new Anchor(1, 57, 56), true),
        new("newline", "let values = { x: 1\n y: 2 }",
            Local(Pair(51, 54, 57, 60, "1", "2")), 72, new Anchor(2, 2, 57), true),
        new("declaration-rhs", "let { x: a y: b } = { x: 1 y: 2 }",
            Declaration(Pair(42, 45, 47, 50, "a", "b"), Pair(58, 61, 63, 66, "1", "2")), 78, new Anchor(1, 64, 63), false),
        new("assignment-rhs", "({ x: a y: b } = { x: 1 y: 2 })",
            Expression(EQ(51, Pair(39, 42, 44, 47, "a", "b"), Pair(55, 58, 60, 63, "1", "2"))), 76, new Anchor(1, 61, 60), false),
        new("nested-default-rhs", "let { outer: { x: a y: b } = { x: 1 y: 2 } } = { outer: { x: 3, y: 4 } }",
            Declaration(Obj(P("outer", 42, EQ(63, Pair(51, 54, 56, 59, "a", "b"), Pair(67, 70, 72, 75, "1", "2")))), Obj(P("outer", 85, Pair(94, 97, 100, 103, "3", "4")))), 117, new Anchor(1, 73, 72), false),
        new("deep-declaration-rhs", "let { outer: [{ x: a y: b }] } = { outer: [{ x: 1 y: 2 }] }",
            Declaration(Obj(P("outer", 42, ArrayOf(Pair(52, 55, 57, 60, "a", "b")))), Obj(P("outer", 71, ArrayOf(Pair(81, 84, 86, 89, "1", "2"))))), 104, new Anchor(1, 87, 86), false),
        new("computed-key-expression", "let { [({ x: 1 y: 2 })]: a } = { x: 3 }",
            Declaration(Obj(Computed(42, Pair(46, 49, 51, 54, "1", "2"), I(61, "a"))), Obj(P("x", 69, L(72, "3")))), 84, new Anchor(1, 52, 51), false),
        new("speculation", "let values = (n = { x: 1 y: 2 })",
            Local(EQ(52, I(50, "n"), Pair(56, 59, 61, 64, "1", "2"))), 77, new Anchor(1, 62, 61), false),
        new("comma", "let values = { x: 1, y: 2 }",
            Local(Pair(51, 54, 57, 60, "1", "2")), 72, null, true),
        new("trailing", "let values = { x: 1, y: 2, }",
            Local(Pair(51, 54, 57, 60, "1", "2")), 73, null, true),
        new("comma-newline", "let values = { x: 1,\n y: 2 }",
            Local(Pair(51, 54, 58, 61, "1", "2")), 73, null, true),
        new("empty", "let values = {}",
            Local(Obj()), 60, null, false),
        new("singleton", "let values = { x: 1 }",
            Local(Obj(P("x", 51, L(54, "1")))), 66, null, false),
        new("shorthand-spread-computed", "let values = { x: 1 z, ...more, [\"k\"]: 2, y: 3 }",
            Local(Obj(P("x", 51, L(54, "1")), P("z", 56, I(56, "z")), Spread(59, I(62, "more")), Computed(68, L(69, "k", TokenKind.StringLiteral), L(75, "2")), P("y", 78, L(81, "3")))), 93, null, false),
        new("quoted", "let values = { x: 1 \"k\": 2 y: 3 }",
            Local(Obj(P("x", 51, L(54, "1")), P("k", 56, L(61, "2"), TokenKind.StringLiteral), P("y", 63, L(66, "3")))), 78, null, false),
        new("malformed", "let values = { x: 1 y: ; }",
            Local(Obj(P("x", 51, L(54, "1")), P("y", 56, L(59, ";", TokenKind.Semicolon)))), 71, new Anchor(1, 60, 59), false),
        new("declaration-target", "let { x: a y: b } = { x: 1, y: 2 }",
            Declaration(Pair(42, 45, 47, 50, "a", "b"), Pair(58, 61, 64, 67, "1", "2")), 79, null, false),
        new("assignment-target", "({ x: a y: b } = { x: 1, y: 2 })",
            Expression(EQ(51, Pair(39, 42, 44, 47, "a", "b"), Pair(55, 58, 61, 64, "1", "2"))), 77, null, false),
        new("deep-declaration-target", "let { outer: [{ x: a y: b }] } = { outer: [{ x: 1, y: 2 }] }",
            Declaration(Obj(P("outer", 42, ArrayOf(Pair(52, 55, 57, 60, "a", "b")))), Obj(P("outer", 71, ArrayOf(Pair(81, 84, 87, 90, "1", "2"))))), 105, null, false),
        new("deep-assignment-target", "({ outer: [{ x: a y: b }] } = { outer: [{ x: 1, y: 2 }] })",
            Expression(EQ(64, Obj(P("outer", 39, ArrayOf(Pair(49, 52, 54, 57, "a", "b")))), Obj(P("outer", 68, ArrayOf(Pair(78, 81, 84, 87, "1", "2")))))), 103, null, false),
        new("interpolation", "let values = `${({ x: 1 y: 2 })}`",
            Local(Template(49, Pair(3, 6, 8, 11, "1", "2"))), 78, null, false),
    ];

    [Fact]
    public void Compilation_RejectsMissingCommaBetweenExplicitObjectProperties()
    {
        var observations = Rows.Select(Observe).ToArray();
        for (int i = 8; i < Rows.Length; i++)
        {
            var row = Rows[i];
            var observed = observations[i];
            Assert.True(row.Name == "malformed"
                ? IsError(row, observed, "Unexpected token: Semicolon ';'")
                : IsClean(row, observed), $"Unexpected invariant profile: {row.Name}");
        }

        var allBase = Enumerable.Range(0, 8).All(i => IsClean(Rows[i], observations[i]));
        var allGreen = Enumerable.Range(0, 8).All(i => IsError(Rows[i], observations[i], MissingComma));
        Log("aggregate", "pre-verdict", new { allBase, allGreen });
        Assert.True(allBase || allGreen, "Expected either the entire stock lane or the entire precise rejection lane.");
        Assert.True(allGreen,
            "C221_OBJECT_LITERAL_MISSING_COMMA_RESULT expected every ordinary object boundary to reject the missing comma");
    }

    private Observation Observe(Row row)
    {
        var directory = Directory.CreateTempSubdirectory("typesharp-c221-");
        try
        {
            var path = Path.Combine(directory.FullName, "app.ts");
            var source = $"export function observe(): number {{ {row.Body}; return 9; }}\n";
            var expectedBytes = Utf8.GetBytes(source);
            File.WriteAllBytes(path, expectedBytes);
            var bytes = File.ReadAllBytes(path);
            var text = Utf8.GetString(bytes);
            Log(row.Name, "source", new
            {
                path, bytes = bytes.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), text
            });

            var tokens = new Lexer(text, path).Tokenize();
            var errors = tokens.Where(token => token.Kind == TokenKind.Error).Select(Snapshot).ToArray();
            Log(row.Name, "lexer", new { errorCount = errors.Length, errors, tokens = tokens.Select(Snapshot).ToArray() });

            var parser = new Parser(tokens);
            var tree = parser.Parse(path);
            var diagnostics = parser.Diagnostics.Select(Snapshot).ToArray();
            Log(row.Name, "parser", new { count = diagnostics.Length, diagnostics });
            Log(row.Name, "ast", new { members = tree.Members.Select(Describe).ToArray() });

            CompilationSnapshot? hosting = null;
            if (row.Compile)
            {
                var compilation = new TypeScriptCompilation(directory.FullName);
                compilation.AddSourceFile(path);
                var modules = compilation.Compile().Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
                var compilerDiagnostics = compilation.Diagnostics.All.Select(Snapshot).ToArray();
                hosting = new CompilationSnapshot(compilerDiagnostics, compilation.Diagnostics.HasErrors, modules);
                Log(row.Name, "compile", new
                {
                    count = compilerDiagnostics.Length, diagnostics = compilerDiagnostics,
                    hasErrors = hosting.HasErrors, modules
                });
            }

            Assert.True(Path.IsPathFullyQualified(path));
            Assert.Equal(expectedBytes, bytes);
            Assert.Empty(errors);
            var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Members));
            Assert.Equal("observe", function.Name);
            Assert.Contains(function.Modifiers, modifier => modifier.Token.Kind == TokenKind.ExportKeyword);
            Assert.Empty(function.Parameters);
            var body = Assert.IsType<BlockStatementSyntax>(function.Body);
            Assert.Equal(2, body.Statements.Count);
            var lex = new Lexed(path, source, tokens);
            row.Statement(body.Statements[0], lex);
            var returned = Assert.IsType<ReturnStatementSyntax>(body.Statements[1]);
            L(row.ReturnOffset, "9")(returned.Value, lex);
            if (row.Diagnostic is { } anchor)
            {
                var token = row.Name == "malformed"
                    ? lex.At(anchor.Offset, TokenKind.Semicolon, ";")
                    : lex.At(anchor.Offset, TokenKind.Identifier, "y");
                Assert.Equal((anchor.Line, anchor.Column, anchor.Offset), (token.Line, token.Column, token.Offset));
            }
            return new Observation(path, diagnostics, hosting);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static Check Local(Check initializer) => (node, lex) =>
    {
        var let = Assert.IsType<VariableDeclarationSyntax>(node);
        Assert.Equal("values", let.Name);
        lex.At(40, TokenKind.Identifier, "values");
        initializer(let.Initializer, lex);
    };

    private static Check Declaration(Check pattern, Check initializer) => (node, lex) =>
    {
        var let = Assert.IsType<DestructuringVariableDeclarationSyntax>(node);
        Assert.False(let.IsArray);
        Assert.Empty(let.Elements);
        pattern(let.Pattern, lex);
        initializer(let.Initializer, lex);
    };

    private static Check Expression(Check expression) => (node, lex) =>
        expression(Assert.IsType<ExpressionStatementSyntax>(node).Expression, lex);

    private static Check EQ(int offset, Check target, Check value) => (node, lex) =>
    {
        var assignment = Assert.IsType<AssignmentExpressionSyntax>(node);
        Assert.Equal(lex.At(offset, TokenKind.Equals, "="), Snapshot(assignment.OperatorToken));
        target(assignment.Target, lex);
        value(assignment.Value, lex);
    };

    private static Check ArrayOf(Check item) => (node, lex) =>
        item(Assert.Single(Assert.IsType<ArrayLiteralExpressionSyntax>(node).Elements), lex);

    private static Check Obj(params Check[] properties) => (node, lex) =>
    {
        var literal = Assert.IsType<ObjectLiteralExpressionSyntax>(node);
        Assert.Equal(properties.Length, literal.Properties.Count);
        for (int i = 0; i < properties.Length; i++)
            properties[i](literal.Properties[i], lex);
    };

    private static Check Pair(int x, int xv, int y, int yv, string xText, string yText) =>
        Obj(P("x", x, xText == "a" ? I(xv, xText) : L(xv, xText)),
            P("y", y, yText == "b" ? I(yv, yText) : L(yv, yText)));

    private static Check P(string key, int offset, Check value, TokenKind kind = TokenKind.Identifier) =>
        (node, lex) =>
        {
            var property = Assert.IsType<ObjectPropertySyntax>(node);
            Assert.Equal(key, property.Key);
            lex.At(offset, kind, key);
            value(property.Value, lex);
        };

    private static Check Spread(int offset, Check value) => (node, lex) =>
    {
        var property = Assert.IsType<ObjectSpreadPropertySyntax>(node);
        var raw = lex.At(offset, TokenKind.DotDotDot, "...");
        Assert.Equal(Coordinates(raw), Snapshot(property.Range.Start));
        value(property.Value, lex);
    };

    private static Check Computed(int offset, Check key, Check value) => (node, lex) =>
    {
        var property = Assert.IsType<ComputedObjectPropertySyntax>(node);
        var raw = lex.At(offset, TokenKind.OpenBracket, "[");
        Assert.Equal(Coordinates(raw), Snapshot(property.Range.Start));
        key(property.KeyExpression, lex);
        value(property.Value, lex);
    };

    private static Check Template(int offset, Check expression) => (node, lex) =>
    {
        const string inner = "({ x: 1 y: 2 })";
        var token = lex.At(offset, TokenKind.TemplateLiteral, "${" + inner + "}");
        Assert.Single(lex.Tokens.Where(t => t.Kind == TokenKind.TemplateLiteral));
        var part = Assert.IsType<TemplatePartSyntax>(node);
        Assert.Equal(Coordinates(token), Snapshot(part.Range.Start));
        var innerTokens = new Lexer(inner, lex.Path).Tokenize();
        Assert.DoesNotContain(innerTokens, t => t.Kind == TokenKind.Error);
        expression(part.Expression, new Lexed(lex.Path, inner, innerTokens));
    };

    private static Check I(int offset, string name) => (node, lex) =>
    {
        var identifier = Assert.IsType<IdentifierExpressionSyntax>(node);
        Assert.Equal(name, identifier.Name);
        var raw = lex.At(offset, TokenKind.Identifier, name);
        Assert.Equal(Coordinates(raw), Snapshot(identifier.Range.Start));
        Assert.Equal(Coordinates(raw), Snapshot(identifier.Range.End));
    };

    private static Check L(int offset, string text, TokenKind kind = TokenKind.IntegerLiteral) => (node, lex) =>
        Assert.Equal(lex.At(offset, kind, text), Snapshot(Assert.IsType<LiteralExpressionSyntax>(node).Token));

    private static object Describe(SyntaxNode node) => new
    {
        type = node.GetType().Name,
        start = Snapshot(node.Range.Start),
        end = Snapshot(node.Range.End),
        name = node switch
        {
            FunctionDeclarationSyntax function => function.Name,
            VariableDeclarationSyntax variable => variable.Name,
            IdentifierExpressionSyntax identifier => identifier.Name,
            ObjectPropertySyntax property => property.Key,
            _ => null
        },
        token = node switch
        {
            LiteralExpressionSyntax literal => Snapshot(literal.Token),
            AssignmentExpressionSyntax assignment => Snapshot(assignment.OperatorToken),
            _ => null
        },
        isArray = node is DestructuringVariableDeclarationSyntax declaration ? (bool?)declaration.IsArray : null,
        bindingElements = node is DestructuringVariableDeclarationSyntax binding ? (int?)binding.Elements.Count : null,
        children = node.GetChildren().Select(Describe).ToArray()
    };

    private static bool IsClean(Row row, Observation observation) =>
        observation.Parser.Length == 0 &&
        (row.Compile
            ? observation.Compilation is { HasErrors: false, Diagnostics.Length: 0, Modules: ["app"] }
            : observation.Compilation is null);

    private static bool IsError(Row row, Observation observed, string message)
    {
        if (row.Diagnostic is not { } anchor || observed.Parser is not [var parserError])
            return false;
        var expected = new DiagnosticSnapshot("Error", "None", null, observed.Path,
            anchor.Line, anchor.Column, anchor.Offset, message);
        return parserError == expected && (row.Compile
            ? observed.Compilation is { HasErrors: true, Diagnostics: [var compilerError], Modules.Length: 0 } &&
              compilerError == (expected with { ModuleId = "app" })
            : observed.Compilation is null);
    }

    private void Log(string row, string stage, object observed) =>
        output.WriteLine($"{Marker} {JsonSerializer.Serialize(new { row, stage, observed })}");

    private static PositionSnapshot Snapshot(SourceLocation location) =>
        new(location.Source, location.Line, location.Column, location.Offset);

    private static TokenSnapshot Snapshot(Token token) =>
        new(token.Kind.ToString(), token.Text, token.Location.Source, token.Location.Line, token.Location.Column, token.Location.Offset);

    private static PositionSnapshot Coordinates(TokenSnapshot token) =>
        new(token.Source, token.Line, token.Column, token.Offset);

    private static DiagnosticSnapshot Snapshot(Diagnostic diagnostic) =>
        new(diagnostic.Severity.ToString(), diagnostic.Code.ToString(), diagnostic.ModuleId, diagnostic.Location.Source,
            diagnostic.Location.Line, diagnostic.Location.Column, diagnostic.Location.Offset, diagnostic.Message);

    private delegate void Check(SyntaxNode? node, Lexed lex);
    private sealed record Row(string Name, string Body, Check Statement, int ReturnOffset, Anchor? Diagnostic, bool Compile);
    private sealed record Anchor(int Line, int Column, int Offset);

    private sealed record Lexed(string Path, string ExpectedSource, IReadOnlyList<Token> Tokens)
    {
        public TokenSnapshot At(int offset, TokenKind kind, string spelling)
        {
            int line = 1, column = 1;
            for (int i = 0; i < offset; i++)
            {
                if (ExpectedSource[i] == '\n') { line++; column = 1; }
                else { column++; }
            }
            var expected = new TokenSnapshot(kind.ToString(), spelling, Path, line, column, offset);
            var observed = Snapshot(Assert.Single(Tokens.Where(token => token.Location.Offset == offset)));
            Assert.Equal(expected, observed);
            return observed;
        }
    }

    private sealed record Observation(string Path, DiagnosticSnapshot[] Parser, CompilationSnapshot? Compilation);
    private sealed record CompilationSnapshot(DiagnosticSnapshot[] Diagnostics, bool HasErrors, string[] Modules);
    private sealed record DiagnosticSnapshot(string Severity, string Code, string? ModuleId, string? Source,
        int Line, int Column, int Offset, string Message);
    private sealed record TokenSnapshot(string Kind, string Text, string? Source, int Line, int Column, int Offset);
    private sealed record PositionSnapshot(string? Source, int Line, int Column, int Offset);
}
