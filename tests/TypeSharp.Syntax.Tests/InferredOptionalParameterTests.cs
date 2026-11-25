using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeSharp.Syntax.Diagnostics;
using SyntaxParser = TypeSharp.Syntax.Parser.Parser;
using TypeSharp.Syntax.SyntaxTree;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Syntax.Tests;

public sealed class InferredOptionalParameterTests(ITestOutputHelper output)
{
    private const string QuestionError = "Expected identifier, got Question";
    private const string CommaError = "Expected identifier, got Comma";

    [Fact]
    public void ParseFunctionDeclaration_AcceptsTerminalInferredOptionalParameter()
    {
        var bare = Observe("bare", "function sample(value?) { return 1; }");
        var trailing = Observe("trailing", "function sample(first: number, last?,) { return first; }");
        var typed = Observe("typed", "function sample(value?: number) { return 1; }");
        var required = Observe("required", "function sample(value) { return 1; }");
        var @default = Observe("default", "function sample(value: number = 1) { return value ? value : 2; }");
        var rest = Observe("rest", "function sample(...values: number[]) { return 1; }");
        var restOptional = Observe("rest-optional", "function sample(...values?) { return 1; }");
        var optionalDefault = Observe("optional-default", "function sample(value? = 1) { return 1; }");
        var method = Observe("method-lookalike", "class Sample { call(value?) { return 1; } }");
        var beforeRequired = Observe("before-required", "function sample(value?, required: number) { return 1; }");
        var missingOpen = Observe("missing-open", "function sample value?) { return 1; }");

        CheckTokens(bare, (TokenKind.Identifier, "value", 16), (TokenKind.Question, "?", 21),
            (TokenKind.CloseParen, ")", 22));
        CheckTokens(trailing, (TokenKind.Comma, ",", 29), (TokenKind.Identifier, "last", 31),
            (TokenKind.Question, "?", 35), (TokenKind.Comma, ",", 36), (TokenKind.CloseParen, ")", 37));

        var bareParams = FunctionReturningOne(bare);
        var trailingFunction = Function(trailing);
        Assert.True(Identifier(Returned(trailingFunction.Body), "first"));
        var trailingParams = trailingFunction.Parameters;
        Assert.NotEmpty(trailingParams);
        Assert.True(Parameter(trailingParams[0], "first") && Primitive(trailingParams[0].TypeAnnotation,
            TokenKind.NumberKeyword, "number", trailing.File, 23), "Expected unchanged explicit first parameter.");

        Assert.Empty(typed.Diagnostics);
        var typedParameter = Assert.Single(FunctionReturningOne(typed));
        Assert.True(Parameter(typedParameter, "value") && typedParameter.TypeAnnotation is NullableTypeSyntax typedNullable &&
            Primitive(typedNullable.ElementType, TokenKind.NumberKeyword, "number", typed.File, 24));

        Assert.Empty(required.Diagnostics);
        Assert.True(InferredAny(Assert.Single(FunctionReturningOne(required)), "value", required.File, 16));

        Assert.Empty(@default.Diagnostics);
        CheckTokens(@default, (TokenKind.CloseParen, ")", 33), (TokenKind.Question, "?", 50));
        var defaultFunction = Function(@default);
        var defaultParameter = Assert.Single(defaultFunction.Parameters);
        Assert.True(Parameter(defaultParameter, "value", hasDefault: true) &&
            Primitive(defaultParameter.TypeAnnotation, TokenKind.NumberKeyword, "number", @default.File, 23) &&
            Integer(defaultParameter.DefaultValue, 1));
        var conditional = Assert.IsType<ConditionalExpressionSyntax>(Returned(defaultFunction.Body));
        Assert.True(Identifier(conditional.Condition, "value") && Identifier(conditional.WhenTrue, "value") &&
            Integer(conditional.WhenFalse, 2));

        Assert.Empty(rest.Diagnostics);
        var restParameter = Assert.Single(FunctionReturningOne(rest));
        Assert.True(Parameter(restParameter, "values", rest: true) &&
            restParameter.TypeAnnotation is ArrayTypeSyntax { IsReadonly: false } array &&
            Primitive(array.ElementType, TokenKind.NumberKeyword, "number", rest.File, 27));

        CheckQuestionRecovery(restOptional, FunctionReturningOne(restOptional), "values", 19, 25, rest: true);
        CheckQuestionRecovery(optionalDefault, FunctionReturningOne(optionalDefault), "value", 16, 21);
        var @class = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(method.Tree.Members));
        Assert.Equal("Sample", @class.Name);
        var call = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(@class.Members));
        Assert.Equal("call", call.Name);
        Assert.Null(call.ReturnType);
        Assert.True(Integer(Returned(call.Body), 1));
        CheckQuestionRecovery(method, call.Parameters, "value", 20, 25);
        CheckQuestionRecovery(beforeRequired, FunctionReturningOne(beforeRequired), "value", 16, 21);

        Assert.DoesNotContain(missingOpen.Tokens, t => t.Kind == TokenKind.OpenParen);
        CheckTokens(missingOpen, (TokenKind.Identifier, "value", 16), (TokenKind.Question, "?", 21),
            (TokenKind.CloseParen, ")", 22));
        var missingParams = FunctionReturningOne(missingOpen);
        Assert.True(DiagnosticsAre(missingOpen, ("Expected OpenParen, got Identifier 'value'", 16), (QuestionError, 21)) &&
            missingParams.Count == 2 && InferredAny(missingParams[0], "value", missingOpen.File, 16) &&
            InferredAny(missingParams[1], "?", missingOpen.File, 16), "Missing opener must retain both stock errors and recovery.");

        var bareBase = DiagnosticsAre(bare, (QuestionError, 21)) && bareParams.Count == 2 &&
            InferredAny(bareParams[0], "value", bare.File, 16) && InferredAny(bareParams[1], "?", bare.File, 16);
        var trailingBase = DiagnosticsAre(trailing, (QuestionError, 35), (CommaError, 36)) && trailingParams.Count == 4 &&
            InferredAny(trailingParams[1], "last", trailing.File, 31) &&
            InferredAny(trailingParams[2], "?", trailing.File, 31) &&
            InferredAny(trailingParams[3], ",", trailing.File, 35);
        var bareGreen = DiagnosticsAre(bare) && bareParams.Count == 1 &&
            InferredAny(bareParams[0], "value", bare.File, 16, nullable: true);
        var trailingGreen = DiagnosticsAre(trailing) && trailingParams.Count == 2 &&
            InferredAny(trailingParams[1], "last", trailing.File, 31, nullable: true);
        var bothBase = bareBase && trailingBase;
        var bothGreen = bareGreen && trailingGreen;
        Log("witness-pair", "pre-verdict", new { bareBase, trailingBase, bareGreen, trailingGreen, bothBase, bothGreen });
        Assert.True(bothBase || bothGreen, "Both witnesses must match the exact same documented BASE or GREEN profile.");
        Assert.True(bothGreen,
            "C217_INFERRED_OPTIONAL_PARAMETER_RESULT both terminal inferred optional parameters must parse without recovery");
    }

    private Observation Observe(string row, string source)
    {
        var file = $"c217-{row}.ts";
        var bytes = Encoding.UTF8.GetBytes(source);
        Log(row, "source", new
        {
            file, text = source, byteCount = bytes.Length, bytesHex = Convert.ToHexString(bytes).ToLowerInvariant(),
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        });
        var tokens = new Lexer(source, file).Tokenize();
        Log(row, "lexer", new
        {
            errorCount = tokens.Count(t => t.Kind == TokenKind.Error),
            tokens = tokens.Select(t => new { token = Snapshot(t), reportedStartPlusTextLength = t.Location.Offset + t.Text.Length }).ToArray()
        });
        var parser = new SyntaxParser(tokens);
        var tree = parser.Parse(file);
        Log(row, "parser", new
        {
            count = parser.Diagnostics.Count,
            diagnostics = parser.Diagnostics.Select(d => new
            {
                severity = d.Severity.ToString(), code = d.Code.ToString(), codeValue = (int)d.Code,
                d.Message, d.ModuleId, location = Snapshot(d.Location)
            }).ToArray()
        });
        Log(row, "ast", Snapshot(tree));

        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Error);
        Assert.Equal(file, tree.FileName);
        Assert.Equal(TokenKind.EOF, tokens[^1].Kind);
        Assert.Equal(source.Length, tokens[^1].Location.Offset);
        return new Observation(file, tokens, parser.Diagnostics, tree);
    }

    private void Log(string row, string phase, object data) =>
        output.WriteLine("C217_INFERRED_OPTIONAL_PARAMETER " + JsonSerializer.Serialize(new { row, phase, data }));

    private static object Snapshot(SourceLocation loc) => new { loc.Source, loc.Line, loc.Column, loc.Offset };

    private static object Snapshot(Token token) =>
        new { kind = token.Kind.ToString(), token.Text, token.Value, location = Snapshot(token.Location) };

    private static object? Snapshot(SyntaxNode? node) => node is null ? null : new
    {
        kind = node.GetType().Name,
        range = new { start = Snapshot(node.Range.Start), end = Snapshot(node.Range.End) },
        detail = node switch
        {
            SourceFileSyntax file => new { file.FileName, MemberCount = file.Members.Count },
            FunctionDeclarationSyntax f => (object)new
                { f.Name, f.IsAsync, f.IsGenerator, f.IsOverloadSignature, ParameterCount = f.Parameters.Count, ReturnType = Snapshot(f.ReturnType) },
            ClassDeclarationSyntax c => new { c.Name, MemberCount = c.Members.Count },
            MethodDeclarationSyntax m => new { m.Name, ParameterCount = m.Parameters.Count, ReturnType = Snapshot(m.ReturnType) },
            ParameterSyntax p => new
                { p.Name, p.TypeWasInferred, p.IsRest, p.IsPropertyParameter, Type = Snapshot(p.TypeAnnotation), Default = Snapshot(p.DefaultValue) },
            ArrayTypeSyntax a => new { a.IsReadonly },
            PrimitiveTypeSyntax p => Snapshot(p.TypeKeyword),
            BlockStatementSyntax b => new { StatementCount = b.Statements.Count },
            LiteralExpressionSyntax l => Snapshot(l.Token),
            IdentifierExpressionSyntax i => new { i.Name },
            _ => null
        },
        children = node.GetChildren().Select(Snapshot).ToArray()
    };

    private static bool At(SourceLocation location, string file, int offset) =>
        location.Source == file && location.Line == 1 && location.Column == offset + 1 && location.Offset == offset;

    private static void CheckTokens(Observation row, params (TokenKind Kind, string Text, int Offset)[] expected)
    {
        foreach (var token in expected)
            Assert.Single(row.Tokens.Where(t => t.Kind == token.Kind && t.Text == token.Text &&
                At(t.Location, row.File, token.Offset)));
    }

    private static FunctionDeclarationSyntax Function(Observation row)
    {
        var declaration = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(row.Tree.Members));
        Assert.Equal("sample", declaration.Name);
        Assert.Null(declaration.ReturnType);
        Assert.False(declaration.IsAsync || declaration.IsGenerator || declaration.IsOverloadSignature);
        Assert.Empty(declaration.GenericParameters);
        return declaration;
    }

    private static IReadOnlyList<ParameterSyntax> FunctionReturningOne(Observation row)
    {
        var function = Function(row);
        Assert.True(Integer(Returned(function.Body), 1));
        return function.Parameters;
    }

    private static ExpressionSyntax? Returned(SyntaxNode? body) =>
        Assert.IsType<ReturnStatementSyntax>(Assert.Single(Assert.IsType<BlockStatementSyntax>(body).Statements)).Value;

    private static bool Integer(ExpressionSyntax? expression, long value) =>
        expression is LiteralExpressionSyntax literal && literal.Token.Kind == TokenKind.IntegerLiteral &&
        literal.Token.Value is long actual && actual == value;

    private static bool Identifier(ExpressionSyntax? expression, string name) =>
        expression is IdentifierExpressionSyntax identifier && identifier.Name == name;

    private static bool Parameter(ParameterSyntax parameter, string name, bool inferred = false, bool rest = false,
        bool hasDefault = false) =>
        parameter.Name == name && parameter.TypeWasInferred == inferred && parameter.IsRest == rest &&
        !parameter.IsPropertyParameter && (parameter.DefaultValue is not null) == hasDefault;

    private static bool Primitive(TypeSyntax type, TokenKind kind, string text, string file, int offset) =>
        type is PrimitiveTypeSyntax primitive && primitive.TypeKeyword.Kind == kind &&
        primitive.TypeKeyword.Text == text && At(primitive.TypeKeyword.Location, file, offset);

    private static bool InferredAny(ParameterSyntax parameter, string name, string file, int offset,
        bool nullable = false, bool rest = false)
    {
        if (!Parameter(parameter, name, inferred: true, rest: rest))
            return false;
        var outer = parameter.TypeAnnotation;
        var inner = nullable ? (outer as NullableTypeSyntax)?.ElementType : outer;
        return inner is not null && Primitive(inner, TokenKind.AnyKeyword, "any", file, offset) &&
            At(inner.Range.Start, file, offset) && At(inner.Range.End, file, offset) &&
            At(outer.Range.Start, file, offset) && At(outer.Range.End, file, offset);
    }

    private static bool IsDiagnostic(Diagnostic diagnostic, string file, string message, int offset) =>
        diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Code == DiagnosticCode.None &&
        diagnostic.ModuleId is null && diagnostic.Message == message && At(diagnostic.Location, file, offset);

    private static bool DiagnosticsAre(Observation row, params (string Message, int Offset)[] expected) =>
        row.Diagnostics.Count == expected.Length &&
        expected.Select((e, i) => IsDiagnostic(row.Diagnostics[i], row.File, e.Message, e.Offset)).All(match => match);

    private static void CheckQuestionRecovery(Observation row, IReadOnlyList<ParameterSyntax> parameters,
        string name, int nameOffset, int questionOffset, bool rest = false)
    {
        CheckTokens(row, (TokenKind.Identifier, name, nameOffset), (TokenKind.Question, "?", questionOffset));
        Assert.Single(row.Diagnostics.Where(d => IsDiagnostic(d, row.File, QuestionError, questionOffset)));
        Assert.NotEmpty(parameters);
        Assert.True(InferredAny(parameters[0], name, row.File, nameOffset, rest: rest),
            $"Expected stock plain inferred parameter in {row.File}");
    }

    private sealed record Observation(string File, IReadOnlyList<Token> Tokens,
        IReadOnlyList<Diagnostic> Diagnostics, SourceFileSyntax Tree);
}
