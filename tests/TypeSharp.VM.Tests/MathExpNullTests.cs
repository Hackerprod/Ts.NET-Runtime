using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class MathExpNullTests(ITestOutputHelper output)
{
    private const string Source = "function main(value: any): number { return Math.exp(value); }";
    private const string File = "c245-math-exp-null.ts";
    private const string Target = "Math::exp";
    private const string PositiveZero = "0000000000000000";
    private const string One = "3FF0000000000000";

    [Fact]
    public void Exp_ExplicitNullReturnsOne()
    {
        var tokens = new Lexer(Source, File).Tokenize();
        int lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(syntax);
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
        Assert.Equal("exp", method.Name);
        Assert.Equal(math.Name, method.DeclaringClassName);
        // Builtin Math members bind through the ClassSymbol namespace path even
        // though their declarations do not set the ordinary method IsStatic flag.
        Assert.False(method.IsStatic);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.Number, method.Type);
        var declaredMethod = math.ClassType.Methods[method.Name];
        Assert.Equal(declaredMethod.IsStatic, method.IsStatic);
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

        var rows = new (string Row, TsValue Argument, string? ExpectedInput)[]
        {
            ("explicit-null", explicitNull, null),
            ("undefined", undefined, "undefined"),
            ("numeric-nan", TsValue.FromFloat64(BitConverter.Int64BitsToDouble(0x7FF8000000000000L)),
                "7FF8000000000000"),
            ("positive-zero", TsValue.FromFloat64(0d), PositiveZero),
            ("negative-zero", TsValue.FromFloat64(BitConverter.Int64BitsToDouble(long.MinValue)),
                "8000000000000000"),
            ("positive-finite", TsValue.FromFloat64(1d), One),
            ("negative-infinity", TsValue.FromFloat64(double.NegativeInfinity), "FFF0000000000000")
        };

        var observations = new (object? Input, string Observed, long Bits)[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var (row, argumentValue, _) = rows[i];
            object? input;
            if (ReferenceEquals(explicitNull, argumentValue))
                input = explicitNull.RawValue;
            else if (ReferenceEquals(undefined, argumentValue))
                input = "undefined";
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
            string observed = double.IsNaN(result.Value) ? "NaN" : Hex(bits);
            observations[i] = (input, observed, bits);
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
            Assert.Equal<object?>(rows[i].ExpectedInput, observations[i].Input);

        for (int i = 0; i < rows.Length; i++)
        {
            const long ExponentMask = 0x7FF0000000000000L;
            const long SignificandMask = 0x000FFFFFFFFFFFFFL;
            long bits = observations[i].Bits;
            bool bitsAreNaN = (bits & ExponentMask) == ExponentMask && (bits & SignificandMask) != 0L;
            Assert.Equal(observations[i].Observed == "NaN", bitsAreNaN);
        }

        Assert.Equal("NaN", observations[1].Observed);
        Assert.Equal("NaN", observations[2].Observed);
        Assert.Equal(One, observations[3].Observed);
        Assert.Equal(One, observations[4].Observed);
        double positiveFinite = BitConverter.Int64BitsToDouble(observations[5].Bits);
        Assert.True(double.IsFinite(positiveFinite) && positiveFinite > 0d &&
            Math.Abs(positiveFinite - 2.718281828459045d) <= 1e-15);
        Assert.Equal(PositiveZero, observations[6].Observed);

        bool fixedNull = observations[0].Observed == One;
        Assert.True(fixedNull || observations[0].Observed == "NaN",
            "C245_MATH_EXP_NULL_UNEXPECTED_PROFILE observed " + observations[0].Observed);
        Assert.True(fixedNull, "C245_MATH_EXP_NULL_RESULT expected binary64 3FF0000000000000 for explicit null");
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
        string hex = Convert.ToHexString(bytes);
        Assert.Equal("120000000073000000000100000075", hex);
        return new(hex, slot, target, argc);
    }

    private static string Hex(long bits) => bits.ToString("X16", CultureInfo.InvariantCulture);

    private void Write(object record) => output.WriteLine("C245_MATH_EXP_NULL " + JsonSerializer.Serialize(record));
}
