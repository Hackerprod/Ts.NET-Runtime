using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Diagnostics;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class NumberIsFiniteBindingTests(ITestOutputHelper output)
{
    private const string Prefix = "C186 Number.isFinite binding ";

    [Fact]
    public void NumberIsFinite_BindsDefaultDirectCall()
    {
        const string shadowSource = "function shadow(Number: any, input: any): any { return Number.isFinite(input); }";
        var shadow = Bind(shadowSource);
        Assert.Empty(shadow.Diagnostics);
        var shadowCall = GetReturnedCall(shadow.Source, "shadow");
        var shadowMember = Assert.IsType<BoundMemberAccessExpression>(shadowCall.Callee);
        var shadowReceiver = Assert.IsType<BoundVariableExpression>(shadowMember.Object);
        var parameter = Assert.IsType<ParameterSymbol>(shadowReceiver.Symbol);
        var property = Assert.IsType<PropertySymbol>(shadowMember.Member);
        Assert.Equal("Number", parameter.Name);
        Assert.Equal("isFinite", property.Name);
        Assert.Same(TsType.Any, shadowCall.Type);
        Write(new
        {
            probe = "shadow",
            shadow.LexerCount,
            shadow.ParserCount,
            binderDiagnostics = Describe(shadow.Diagnostics),
            receiverKind = parameter.Kind.ToString(),
            memberKind = property.Kind.ToString()
        });

        CheckExcluded("extraction", "function excluded(input: any): any { return Number.isFinite; }");
        CheckExcluded("optional-receiver", "function excluded(input: any): any { return Number?.isFinite(input); }");
        CheckExcluded("optional-call", "function excluded(input: any): any { return Number.isFinite?.(input); }");

        const string mainSource = "function main(input: any): bool { return Number.isFinite(input); }";
        var main = Bind(mainSource);
        Write(new
        {
            probe = "main",
            main.LexerCount,
            main.ParserCount,
            binderDiagnostics = Describe(main.Diagnostics)
        });
        Assert.True(main.Diagnostics.Count == 0, "C186_NUMBER_ISFINITE_BINDING_DIAGNOSTICS");

        var mainCall = GetReturnedCall(main.Source, "main");
        var mainMember = Assert.IsType<BoundMemberAccessExpression>(mainCall.Callee);
        var mainReceiver = Assert.IsType<BoundVariableExpression>(mainMember.Object);
        var numberClass = Assert.IsType<ClassSymbol>(mainReceiver.Symbol);
        var method = Assert.IsType<MethodSymbol>(mainMember.Member);
        Assert.Equal("Number", numberClass.Name);
        Assert.Equal("Number", method.DeclaringClassName);
        Assert.Equal("isFinite", method.Name);
        Assert.Single(mainCall.Arguments);
        Assert.Same(TsType.Bool, mainCall.Type);

        var ir = new IRGenerator().Generate(main.Source);
        var function = Assert.Single(ir.Functions, candidate => candidate.Name == "main");
        var instructions = Assert.Single(function.Blocks).Instructions.ToArray();
        Assert.Equal(3, instructions.Length);
        var load = instructions[0];
        Assert.Equal(Opcode.LoadArg, load.Opcode);
        Assert.Equal(0, load.Operand0);
        var call = instructions[1];
        Assert.Equal(Opcode.Call, call.Opcode);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Number::isFinite", target);
        Assert.Equal(1, call.Operand1);
        Assert.Equal(Opcode.Return, instructions[2].Opcode);
        var code = BytecodeCompiler.Compile(ir);

        (string Id, TsValue Input, bool Expected)[] rows =
        [
            ("finite", TsValue.FromFloat64(12.5d), true),
            ("nan", TsValue.FromFloat64(double.NaN), false),
            ("infinity", TsValue.FromFloat64(double.PositiveInfinity), false),
            ("numeric-string", TsValue.FromString("12.5"), false)
        ];
        foreach (var row in rows)
        {
            var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
            TsValue? result = null;
            var exception = Record.Exception(() => { result = interpreter.Execute(code, "main", [row.Input]); });
            Write(new
            {
                probe = row.Id,
                target,
                argc = call.Operand1,
                slot = load.Operand0,
                inputWrapper = row.Input.GetType().Name,
                inputTag = row.Input.ValueType.ToString(),
                resultWrapper = result?.GetType().Name,
                resultTag = result?.ValueType.ToString(),
                actual = (result as TsBoolValue)?.Value,
                expected = row.Expected,
                exception = exception?.GetType().FullName
            });
            Assert.Null(exception);
            var boolean = Assert.IsType<TsBoolValue>(result);
            Assert.Equal(TsValueType.Bool, boolean.ValueType);
            Assert.Equal(row.Expected, boolean.Value);
        }
    }

    private void CheckExcluded(string probe, string source)
    {
        var excluded = Bind(source);
        Write(new
        {
            probe,
            excluded.LexerCount,
            excluded.ParserCount,
            binderDiagnostics = Describe(excluded.Diagnostics)
        });
        var diagnostic = Assert.Single(excluded.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(DiagnosticCode.None, diagnostic.Code);
        Assert.Equal("No member 'isFinite' on type '() => number'", diagnostic.Message);
        Assert.Equal(source.IndexOf("Number", StringComparison.Ordinal), diagnostic.Location.Offset);
    }

    private static (BoundSourceFile Source, int LexerCount, int ParserCount, IReadOnlyList<Diagnostic> Diagnostics)
        Bind(string source)
    {
        var tokens = new Lexer(source).Tokenize();
        var lexerCount = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerCount);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var parsed = parser.Parse();
        var parserCount = parser.Diagnostics.Count;
        Assert.Empty(parser.Diagnostics);
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parsed);
        return (bound, lexerCount, parserCount, binder.Diagnostics.All);
    }

    private static BoundCallExpression GetReturnedCall(BoundSourceFile source, string name)
    {
        var declaration = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(source.Members));
        Assert.Equal(name, declaration.Symbol.Name);
        var block = Assert.IsType<BoundBlockStatement>(declaration.Body);
        var statement = Assert.IsType<BoundReturnStatement>(Assert.Single(block.Statements));
        return Assert.IsType<BoundCallExpression>(statement.Value);
    }

    private static object Describe(IReadOnlyList<Diagnostic> diagnostics) =>
        diagnostics.Select(diagnostic => new
        {
            severity = diagnostic.Severity.ToString(),
            code = diagnostic.Code.ToString(),
            message = diagnostic.Message,
            offset = diagnostic.Location.Offset
        }).ToArray();

    private void Write(object observation) => output.WriteLine(Prefix + JsonSerializer.Serialize(observation));
}
