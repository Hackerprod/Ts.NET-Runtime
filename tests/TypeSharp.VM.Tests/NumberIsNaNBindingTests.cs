using System.Buffers.Binary;
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

public sealed class NumberIsNaNBindingTests(ITestOutputHelper output)
{
    private const string Target = "Number::isNaN";

    [Fact]
    public void NumberIsNaN_BindsDefaultDirectCall()
    {
        const string shadowSource = "function shadow(Number: any, input: any): any { return Number.isNaN(input); }";
        var shadow = Bind(shadowSource);
        Assert.Empty(shadow.Diagnostics);
        var shadowDeclaration = GetDeclaration(shadow.Bound, "shadow");
        var shadowCall = GetReturnedCall(shadowDeclaration);
        var shadowMember = Assert.IsType<BoundMemberAccessExpression>(shadowCall.Callee);
        var shadowReceiver = Assert.IsType<BoundVariableExpression>(shadowMember.Object);
        var parameter = Assert.IsType<ParameterSymbol>(shadowReceiver.Symbol);
        var property = Assert.IsType<PropertySymbol>(shadowMember.Member);
        Write(new
        {
            probe = "shadow",
            source = shadowSource,
            shadow.LexerErrors,
            shadow.ParserDiagnostics,
            binderCount = shadow.Diagnostics.Count,
            binderDiagnostics = Describe(shadow.Diagnostics),
            receiverKind = parameter.Kind.ToString(),
            receiverName = parameter.Name,
            memberKind = property.Kind.ToString(),
            memberName = property.Name,
            callType = shadowCall.Type.Name
        });
        Assert.Equal("Number", parameter.Name);
        Assert.Same(shadowDeclaration.Symbol.Parameters[0], parameter);
        Assert.Equal("isNaN", property.Name);
        Assert.Same(TsType.Any, shadowCall.Type);

        CheckExcluded("extraction", "function excluded(input: any): any { return Number.isNaN; }");
        CheckExcluded("optional-receiver", "function excluded(input: any): any { return Number?.isNaN(input); }");
        CheckExcluded("optional-call", "function excluded(input: any): any { return Number.isNaN?.(input); }");

        const string mainSource = "function main(input: any): bool { return Number.isNaN(input); }";
        var main = Bind(mainSource);
        Write(new
        {
            probe = "main",
            source = mainSource,
            main.LexerErrors,
            main.ParserDiagnostics,
            binderCount = main.Diagnostics.Count,
            binderDiagnostics = Describe(main.Diagnostics)
        });
        Assert.True(main.Diagnostics.Count == 0, "C209_NUMBER_ISNAN_BINDING_DIAGNOSTICS");

        var mainDeclaration = GetDeclaration(main.Bound, "main");
        var mainCall = GetReturnedCall(mainDeclaration);
        var mainMember = Assert.IsType<BoundMemberAccessExpression>(mainCall.Callee);
        var mainReceiver = Assert.IsType<BoundVariableExpression>(mainMember.Object);
        var number = Assert.IsType<ClassSymbol>(mainReceiver.Symbol);
        var method = Assert.IsType<MethodSymbol>(mainMember.Member);
        Assert.Equal("Number", number.Name);
        Assert.Equal(number.Name, method.DeclaringClassName);
        Assert.Equal("isNaN", method.Name);
        Assert.Same(TsType.Bool, number.ClassType.Methods[method.Name].ReturnType);
        Assert.Same(TsType.Bool, method.Type);
        Assert.Same(TsType.Bool, mainCall.Type);
        var input = Assert.IsType<BoundVariableExpression>(Assert.Single(mainCall.Arguments));
        Assert.Same(mainDeclaration.Symbol.Parameters[0], input.Symbol);

        var ir = new IRGenerator().Generate(main.Bound);
        var irMain = Assert.Single(ir.Functions, function => function.Name == "main");
        Assert.Collection(Assert.Single(irMain.Blocks).Instructions,
            load =>
            {
                Assert.Equal(Opcode.LoadArg, load.Opcode);
                Assert.Equal(0, load.Operand0);
            },
            call =>
            {
                Assert.Equal(Opcode.Call, call.Opcode);
                Assert.Equal(Target, Assert.IsType<string>(call.OperandObject));
                Assert.Equal(1, call.Operand1);
            },
            ret => Assert.Equal(Opcode.Return, ret.Opcode));

        var compiled = BytecodeCompiler.Compile(ir);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiled);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        var compiledMain = Assert.Single(compiled.Functions, function => function.Name == irMain.Name);
        var restoredMain = Assert.Single(restored.Functions, function => function.Name == irMain.Name);
        Assert.Equal(1, compiledMain.ParameterCount);
        Assert.Equal(compiledMain.ParameterCount, restoredMain.ParameterCount);
        Assert.Equal(compiledMain.Instructions, restoredMain.Instructions);
        Assert.Equal(compiledMain.StringConstants, restoredMain.StringConstants);

        var (targetIndex, argc) = DecodeDirectCall(restoredMain.Instructions);
        Assert.InRange(targetIndex, 0, restoredMain.StringConstants.Length - 1);
        var target = restoredMain.StringConstants[targetIndex];
        Assert.Equal(Target, target);
        Assert.Equal(1, argc);

        (string Label, TsValue Input, bool Expected)[] rows =
        [
            ("float64-nan", TsValue.FromFloat64(double.NaN), true),
            ("float32-nan", TsValue.FromFloat32(float.NaN), true),
            ("float64-finite", TsValue.FromFloat64(12.5d), false),
            ("float64-positive-infinity", TsValue.FromFloat64(double.PositiveInfinity), false),
            ("string-nan", TsValue.FromString("NaN"), false),
            ("void", TsValue.Void, false)
        ];
        foreach (var row in rows)
        {
            var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(restored, restoredMain.Name, [row.Input]);
            Write(new
            {
                probe = row.Label,
                target,
                argc,
                inputWrapper = row.Input.GetType().Name,
                inputTag = row.Input.ValueType.ToString(),
                resultWrapper = result?.GetType().Name,
                resultTag = result?.ValueType.ToString(),
                actual = (result as TsBoolValue)?.Value,
                expected = row.Expected
            });
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
            source,
            excluded.LexerErrors,
            excluded.ParserDiagnostics,
            binderCount = excluded.Diagnostics.Count,
            binderDiagnostics = Describe(excluded.Diagnostics)
        });
        var diagnostic = Assert.Single(excluded.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(DiagnosticCode.None, diagnostic.Code);
        Assert.Equal("No member 'isNaN' on type '() => number'", diagnostic.Message);
        Assert.Equal(source.IndexOf("Number", StringComparison.Ordinal), diagnostic.Location.Offset);
    }

    private static (BoundSourceFile Bound, int LexerErrors, int ParserDiagnostics, IReadOnlyList<Diagnostic> Diagnostics)
        Bind(string source)
    {
        var tokens = new Lexer(source).Tokenize();
        var lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var parsed = parser.Parse();
        var parserDiagnostics = parser.Diagnostics.Count;
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(parsed);
        return (bound, lexerErrors, parserDiagnostics, binder.Diagnostics.All);
    }

    private static BoundFunctionDeclaration GetDeclaration(BoundSourceFile source, string name)
    {
        var declaration = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(source.Members));
        Assert.Equal(name, declaration.Symbol.Name);
        return declaration;
    }

    private static BoundCallExpression GetReturnedCall(BoundFunctionDeclaration declaration)
    {
        var block = Assert.IsType<BoundBlockStatement>(declaration.Body);
        var statement = Assert.IsType<BoundReturnStatement>(Assert.Single(block.Statements));
        return Assert.IsType<BoundCallExpression>(statement.Value);
    }

    private static (int TargetIndex, int Argc) DecodeDirectCall(byte[] bytes)
    {
        var calls = new List<(int TargetIndex, int Argc)>();
        for (int offset = 0; offset < bytes.Length;)
        {
            byte opcode = bytes[offset];
            Assert.True(OpcodeFormats.TryGet(opcode, out var format), $"Unknown opcode at {offset}");
            int width = 1 + format.OperandBytes;
            Assert.True(width <= bytes.Length - offset, $"Truncated opcode at {offset}");
            if (opcode == Opcodes.Call)
            {
                Assert.Equal(OperandKind.Int32x2, format.Operand);
                calls.Add((
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4))));
            }
            offset += width;
        }
        return Assert.Single(calls);
    }

    private static object Describe(IReadOnlyList<Diagnostic> diagnostics) =>
        diagnostics.Select(diagnostic => new
        {
            severity = diagnostic.Severity.ToString(),
            code = diagnostic.Code.ToString(),
            message = diagnostic.Message,
            offset = diagnostic.Location.Offset
        }).ToArray();

    private void Write(object observation) =>
        output.WriteLine("C209_NUMBER_ISNAN_BINDING " + JsonSerializer.Serialize(observation));
}
