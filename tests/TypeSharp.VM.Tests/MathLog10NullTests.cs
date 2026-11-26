using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.Syntax.SyntaxTree;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class MathLog10NullTests(ITestOutputHelper output)
{
    private const string Source = "function main(value: any): number { return Math.log10(value); }";
    private const string File = "c250-math-log10-null.ts";
    private const string Target = "Math::log10";
    private const string PositiveZero = "0000000000000000";
    private const string NegativeInfinity = "FFF0000000000000";
    private const string NaN = "NaN";

    [Fact]
    public void Log10_ExplicitNullReturnsNegativeInfinity()
    {
        var tokens = new Lexer(Source, File).Tokenize();
        int lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse(File);
        Assert.Equal(File, syntax.FileName);
        Assert.Empty(parser.Diagnostics);

        var syntaxFunction = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(syntax.Members));
        Assert.Equal("main", syntaxFunction.Name);
        Assert.False(syntaxFunction.IsAsync);
        Assert.False(syntaxFunction.IsGenerator);
        Assert.False(syntaxFunction.IsOverloadSignature);
        Assert.Empty(syntaxFunction.GenericParameters);
        Assert.Empty(syntaxFunction.Modifiers);
        Assert.Equal("value", Assert.Single(syntaxFunction.Parameters).Name);
        var syntaxBody = Assert.IsType<BlockStatementSyntax>(syntaxFunction.Body);
        var syntaxReturn = Assert.IsType<ReturnStatementSyntax>(Assert.Single(syntaxBody.Statements));
        var syntaxCall = Assert.IsType<CallExpressionSyntax>(syntaxReturn.Value);
        Assert.False(syntaxCall.IsNullConditional);
        Assert.Empty(syntaxCall.TypeArguments);
        var syntaxAccess = Assert.IsType<MemberAccessExpressionSyntax>(syntaxCall.Callee);
        Assert.False(syntaxAccess.IsNullConditional);
        Assert.Equal("Math", Assert.IsType<IdentifierExpressionSyntax>(syntaxAccess.Object).Name);
        Assert.Equal("log10", syntaxAccess.MemberName);
        Assert.Equal("value", Assert.IsType<IdentifierExpressionSyntax>(Assert.Single(syntaxCall.Arguments)).Name);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Equal(File, bound.FileName);
        Assert.Empty(binder.Diagnostics.All);

        var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", function.Symbol.Name);
        Assert.Same(TsType.Number, function.Symbol.Type);
        var parameter = Assert.Single(function.Symbol.Parameters);
        Assert.Equal("value", parameter.Name);
        Assert.Same(TsType.Any, parameter.Type);
        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        Assert.Same(TsType.Number, call.Type);
        Assert.False(call.IsNullConditional);
        var access = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        Assert.False(access.IsNullConditional);
        var signature = Assert.IsType<TsFunctionType>(access.Type);
        Assert.Same(TsType.Number, signature.ReturnType);
        Assert.True(signature.HasDynamicSignature);
        Assert.Empty(signature.Parameters);
        var receiver = Assert.IsType<BoundVariableExpression>(access.Object);
        var math = Assert.IsType<ClassSymbol>(receiver.Symbol);
        Assert.Equal("Math", math.Name);
        var method = Assert.IsType<MethodSymbol>(access.Member);
        Assert.Equal("log10", method.Name);
        Assert.Equal(math.Name, method.DeclaringClassName);
        // Builtin Math members bind through the ClassSymbol namespace path even
        // though their declarations do not set the ordinary method IsStatic flag.
        Assert.False(method.IsStatic);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.Number, method.Type);
        var declaredMethod = math.ClassType.Methods[method.Name];
        Assert.Equal(method.Name, declaredMethod.Name);
        Assert.False(declaredMethod.IsStatic);
        Assert.Empty(declaredMethod.Parameters);
        Assert.Same(TsType.Number, declaredMethod.ReturnType);
        string boundTarget = $"{method.DeclaringClassName}::{method.RuntimeName ?? method.Name}";
        Assert.Equal(Target, boundTarget);
        var argument = Assert.IsType<BoundVariableExpression>(Assert.Single(call.Arguments));
        var argumentSymbol = Assert.IsType<ParameterSymbol>(argument.Symbol);
        Assert.Same(parameter, argumentSymbol);
        Assert.Same(TsType.Any, argument.Type);

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions);
        Assert.Equal(function.Symbol.Name, irMain.Name);
        Assert.Equal(parameter.Name, Assert.Single(irMain.Parameters).Name);
        var instructions = Assert.Single(irMain.Blocks).Instructions.ToArray();
        Assert.Equal(new[] { Opcode.LoadArg, Opcode.Call, Opcode.Return },
            instructions.Select(instruction => instruction.Opcode).ToArray());
        Assert.Equal(0, instructions[0].Operand0);
        string irTarget = Assert.IsType<string>(instructions[1].OperandObject);
        Assert.Equal(Target, irTarget);
        Assert.Equal(1, instructions[1].Operand1);

        var compiled = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(compiled);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiled);
        Assert.True(stream.Length > 0);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        Assert.Equal(stream.Length, stream.Position);
        BytecodeVerifier.Verify(restored);
        var compiledMain = Assert.Single(compiled.Functions);
        var restoredMain = Assert.Single(restored.Functions);
        Assert.Equal(irMain.Name, compiledMain.Name);
        Assert.Equal(irMain.Name, restoredMain.Name);
        Assert.Equal(1, compiledMain.ParameterCount);
        Assert.Equal(1, restoredMain.ParameterCount);
        var compiledRoute = Decode(compiledMain.Instructions, compiledMain.StringConstants);
        var restoredRoute = Decode(restoredMain.Instructions, restoredMain.StringConstants);
        Assert.Equal(compiledMain.Instructions, restoredMain.Instructions);
        Assert.Equal(compiledMain.StringConstants, restoredMain.StringConstants);
        Assert.Equal(compiledRoute, restoredRoute);

        Write(new
        {
            row = "route",
            lexerErrors,
            parserDiagnostics = parser.Diagnostics.Count,
            binderDiagnostics = binder.Diagnostics.All.Count(),
            parsedFile = syntax.FileName,
            boundFile = bound.FileName,
            main = restoredMain.Name,
            parameterCount = restoredMain.ParameterCount,
            argumentSlot = restoredRoute.argumentSlot,
            target = restoredRoute.target,
            argc = restoredRoute.argc,
            compiledHex = compiledRoute.hex,
            restoredHex = restoredRoute.hex
        });

        var explicitNull = Assert.IsType<TsNull>(TsValue.Null);
        Assert.Same(TsValue.Null, explicitNull);
        Assert.Equal(TsValueType.Null, explicitNull.ValueType);
        Assert.Null(explicitNull.RawValue);
        var undefined = Assert.IsType<TsVoid>(TsValue.Void);
        Assert.Same(TsValue.Void, undefined);
        Assert.Equal(TsValueType.Void, undefined.ValueType);

        var rows = new (string Row, TsValue Argument, TsValueType ExpectedType, object? ExpectedInput,
            string? ExpectedControl)[]
        {
            ("explicit-null", explicitNull, TsValueType.Null, null, null),
            ("explicit-undefined", undefined, TsValueType.Void, "undefined", NaN),
            ("positive-zero", TsValue.FromFloat64(0d), TsValueType.Float64, PositiveZero, NegativeInfinity),
            ("negative-zero", TsValue.FromFloat64(BitConverter.Int64BitsToDouble(long.MinValue)),
                TsValueType.Float64, "8000000000000000", NegativeInfinity),
            ("negative-one", TsValue.FromFloat64(-1d), TsValueType.Float64, "BFF0000000000000", NaN),
            ("positive-one", TsValue.FromFloat64(1d), TsValueType.Float64, "3FF0000000000000", PositiveZero),
            ("int32-hundred", TsValue.FromInt32(100), TsValueType.Int32, 100, "4000000000000000"),
            ("positive-infinity", TsValue.FromFloat64(double.PositiveInfinity),
                TsValueType.Float64, "7FF0000000000000", "7FF0000000000000")
        };

        var observations = new (TsValueType InputType, object? Input, string Observed, long Bits)[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var (row, argumentValue, _, _, _) = rows[i];
            object? input;
            if (ReferenceEquals(explicitNull, argumentValue))
                input = explicitNull.RawValue;
            else if (ReferenceEquals(undefined, argumentValue))
                input = "undefined";
            else if (argumentValue is TsInt32Value)
            {
                var integer = Assert.IsType<TsInt32Value>(argumentValue);
                Assert.Equal(TsValueType.Int32, integer.ValueType);
                input = integer.Value;
            }
            else
            {
                var number = Assert.IsType<TsFloat64Value>(argumentValue);
                Assert.Equal(TsValueType.Float64, number.ValueType);
                input = Hex(BitConverter.DoubleToInt64Bits(number.Value));
            }

            var returned = new TypeSharp.VM.Interpreter.Interpreter()
                .Execute(restored, restoredMain.Name, [argumentValue]);
            var result = Assert.IsType<TsFloat64Value>(returned);
            Assert.Equal(TsValueType.Float64, result.ValueType);
            long bits = BitConverter.DoubleToInt64Bits(result.Value);
            string observed = double.IsNaN(result.Value) ? NaN : Hex(bits);
            observations[i] = (argumentValue.ValueType, input, observed, bits);
            Write(new
            {
                row,
                inputType = argumentValue.ValueType.ToString(),
                input,
                outputType = result.ValueType.ToString(),
                observed,
                observedBits = Hex(bits)
            });
        }

        for (int i = 0; i < rows.Length; i++)
        {
            Assert.Equal(rows[i].ExpectedType, observations[i].InputType);
            Assert.Equal<object?>(rows[i].ExpectedInput, observations[i].Input);
            const long ExponentMask = 0x7FF0000000000000L;
            const long SignificandMask = 0x000FFFFFFFFFFFFFL;
            long bits = observations[i].Bits;
            bool bitsAreNaN = (bits & ExponentMask) == ExponentMask && (bits & SignificandMask) != 0L;
            Assert.Equal(observations[i].Observed == NaN, bitsAreNaN);
        }

        for (int i = 1; i < rows.Length; i++)
            Assert.Equal(rows[i].ExpectedControl, observations[i].Observed);

        bool fixedNull = observations[0].Observed == NegativeInfinity;
        bool stockNull = observations[0].Observed == NaN && observations[0].Bits == observations[1].Bits;
        Assert.True(fixedNull || stockNull,
            "C250_MATH_LOG10_NULL_UNEXPECTED_PROFILE observed " + observations[0].Observed);
        Assert.True(fixedNull, "C250_MATH_LOG10_NULL_RESULT expected explicit null to produce Float64 bits FFF0000000000000");
    }

    private sealed record Route(string hex, int argumentSlot, string target, int argc);

    private static Route Decode(byte[] bytes, string[] strings)
    {
        Assert.Equal(15, bytes.Length);
        Assert.Equal((byte)0x12, bytes[0]);
        Assert.Equal((byte)0x73, bytes[5]);
        Assert.Equal((byte)0x75, bytes[14]);
        int slot = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1, 4));
        int targetIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(6, 4));
        int argc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(10, 4));
        Assert.Equal(0, slot);
        Assert.Equal(0, targetIndex);
        Assert.Equal(1, argc);
        Assert.Equal(Target, Assert.Single(strings));
        string target = strings[targetIndex];
        string hex = Convert.ToHexString(bytes).ToLowerInvariant();
        Assert.Equal("120000000073000000000100000075", hex);
        return new(hex, slot, target, argc);
    }

    private static string Hex(long bits) => bits.ToString("X16", CultureInfo.InvariantCulture);

    private void Write(object record) => output.WriteLine("C250_MATH_LOG10_NULL " + JsonSerializer.Serialize(record));
}
