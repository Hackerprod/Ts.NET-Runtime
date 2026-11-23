using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Syntax;
using TypeSharp.Syntax.SyntaxTree;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class LabelledEnumerationContinueTests(ITestOutputHelper output)
{
    private const string Prefix = "C188 LabelledEnumerationContinue ";

    [Fact]
    public void LabelledContinue_ResumesTheNamedEnumerationLoop()
    {
        Probe[] probes =
        [
            new("for-of", "function main(): int32 { let total: int32 = 0; outer: for (const value of [1, 2]) { for (let j: int32 = 0; j < 1; j = j + 1) { total = total + 1; continue outer; } total = total + 100; } return total; }",
                typeof(ForOfStatementSyntax), typeof(TsInt32Value), TsValueType.Int32, 2),
            new("for-in", "function main(): string { let seen: string = ''; outer: for (let key in { first: 1, second: 2 }) { for (let j: int32 = 0; j < 1; j = j + 1) { seen = seen + key; continue outer; } seen = seen + 'wrong'; } return seen; }",
                typeof(ForInStatementSyntax), typeof(TsStringValue), TsValueType.String, "firstsecond")
        ];
        var observations = new List<Observation>();
        foreach (var probe in probes)
        {
            var observation = Run(probe);
            observations.Add(observation);
            output.WriteLine(Prefix + JsonSerializer.Serialize(new
            {
                row = probe.Id,
                lexerCount = observation.LexerCount,
                parserCount = observation.ParserCount,
                binderCount = observation.BinderCount,
                failedStage = observation.GenerateException is null ? null : "Generate",
                exceptionType = observation.GenerateException?.GetType().FullName,
                exceptionMessage = observation.GenerateException?.Message,
                expectedWrapper = probe.Wrapper.Name,
                expectedTag = probe.Tag.ToString(),
                expectedValue = probe.Value,
                actualWrapper = observation.Result?.GetType().Name,
                actualTag = observation.Result?.ValueType.ToString(),
                actualValue = ReadValue(observation.Result)
            }));
        }

        for (var i = 0; i < probes.Length; i++)
        {
            var observation = observations[i];
            Assert.Equal(0, observation.LexerCount);
            Assert.Equal(0, observation.ParserCount);
            Assert.Equal(0, observation.BinderCount);
            Assert.True(observation.HasExpectedSyntax);
            Assert.True(observation.HasExpectedBoundLabels);
        }

        for (var i = 0; i < probes.Length; i++)
        {
            var probe = probes[i];
            var observation = observations[i];
            Assert.True(observation.GenerateException is null &&
                        observation.Result is { } result && result.GetType() == probe.Wrapper &&
                        result.ValueType == probe.Tag &&
                        Equals(ReadValue(result), probe.Value),
                "C188_NAMED_ENUMERATION_CONTINUE_EXPECTED_OUTER_ITERATOR " + probe.Id);
        }
    }

    private static Observation Run(Probe probe)
    {
        var tokens = new Lexer(probe.Source).Tokenize();
        var lexerCount = tokens.Count(token => token.Kind == TokenKind.Error);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        var parserCount = parser.Diagnostics.Count;
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(syntax);
        var binderCount = binder.Diagnostics.All.Count;

        var syntaxFunction = syntax.Members.OfType<FunctionDeclarationSyntax>().SingleOrDefault();
        var syntaxLabel = (syntaxFunction?.Body as BlockStatementSyntax)?.Statements
            .OfType<LabelledStatementSyntax>().SingleOrDefault();
        var syntaxOk = syntaxLabel is { Label: "outer" } &&
                       syntaxLabel.Statement.GetType() == probe.SyntaxKind;
        var boundFunction = bound.Members.OfType<BoundFunctionDeclaration>().SingleOrDefault();
        var boundLabel = (boundFunction?.Body as BoundBlockStatement)?.Statements
            .OfType<BoundLabelledStatement>().SingleOrDefault();
        var boundOk = boundLabel is { Label: "outer" } &&
                      ContinuesIn(boundLabel.Statement).Select(statement => statement.Label)
                          .SequenceEqual(new[] { "outer" });

        Exception? generateException = null;
        TsValue? result = null;
        if (lexerCount == 0 && parserCount == 0 && binderCount == 0)
        {
            var generator = new IRGenerator();
            ModuleIR? ir = null;
            generateException = Record.Exception(() => { ir = generator.Generate(bound); });
            if (generateException is null)
            {
                var code = BytecodeCompiler.Compile(ir!);
                result = new TypeSharp.VM.Interpreter.Interpreter().Execute(code, "main");
            }
        }

        return new(lexerCount, parserCount, binderCount, syntaxOk, boundOk, generateException, result);
    }

    private static IEnumerable<BoundContinueStatement> ContinuesIn(BoundNode node) => node switch
    {
        BoundContinueStatement statement => new[] { statement },
        BoundBlockStatement block => block.Statements.SelectMany(ContinuesIn),
        BoundForStatement loop => ContinuesIn(loop.Body),
        _ => Enumerable.Empty<BoundContinueStatement>()
    };

    private static object? ReadValue(TsValue? result) => result switch
    {
        TsInt32Value value => value.Value,
        TsStringValue value => value.Value,
        _ => null
    };

    private sealed record Probe(string Id, string Source, Type SyntaxKind, Type Wrapper, TsValueType Tag, object Value);
    private sealed record Observation(int LexerCount, int ParserCount, int BinderCount, bool HasExpectedSyntax,
        bool HasExpectedBoundLabels, Exception? GenerateException, TsValue? Result);
}
