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

public sealed class MathRoundNullTests(ITestOutputHelper output)
{
    private const string Source = "function main(value: any): number { return Math.round(value); }";
    private const string File = "c248-math-round-null.ts";
    private const string Target = "Math::round";
    private const string PositiveZero = "0000000000000000";
    private const string NaNBits = "FFF8000000000000";

    [Fact]
    public void Round_ExplicitNullReturnsPositiveZero()
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
        Assert.Equal("round", method.Name);
        Assert.Equal(math.Name, method.DeclaringClassName);
        // Builtin Math members bind through the ClassSymbol namespace path even
        // though their declarations do not set the ordinary method IsStatic flag.
        Assert.False(method.IsStatic);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.Number, method.Type);
        var declaredMethod = math.ClassType.Methods[method.Name];
        Assert.Equal(declaredMethod.IsStatic, method.IsStatic);
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
        Assert.Same(TsType.Number, irMain.ReturnType);
        var irParameter = Assert.Single(irMain.Parameters);
        Assert.Equal(parameter.Name, irParameter.Name);
        Assert.Same(TsType.Any, irParameter.Type);
        Assert.False(irParameter.IsRest);
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

        var rows = new (string Row, TsValue Argument, TsValueType ExpectedType, string? ExpectedInput,
            string ExpectedGreenBits, string ExpectedGreenObserved)[]
        {
            ("explicit-null", TsValue.Null, TsValueType.Null, null, PositiveZero, PositiveZero),
            ("explicit-undefined", TsValue.Void, TsValueType.Void, "undefined", NaNBits, "NaN"),
            ("numeric-nan", Float(0xFFF8000000000000UL), TsValueType.Float64, NaNBits, NaNBits, "NaN"),
            ("positive-zero", Float(0x0000000000000000UL), TsValueType.Float64,
                PositiveZero, PositiveZero, PositiveZero),
            ("negative-zero", Float(0x8000000000000000UL), TsValueType.Float64,
                "8000000000000000", "8000000000000000", "8000000000000000"),
            ("negative-half-tie", Float(0xBFE0000000000000UL), TsValueType.Float64,
                "BFE0000000000000", "8000000000000000", "8000000000000000"),
            ("negative-one-half-tie", Float(0xBFF8000000000000UL), TsValueType.Float64,
                "BFF8000000000000", "BFF0000000000000", "BFF0000000000000"),
            ("positive-one-half-tie", Float(0x3FF8000000000000UL), TsValueType.Float64,
                "3FF8000000000000", "4000000000000000", "4000000000000000"),
            ("positive-infinity", Float(0x7FF0000000000000UL), TsValueType.Float64,
                "7FF0000000000000", "7FF0000000000000", "7FF0000000000000")
        };

        var observations = new (TsValueType InputType, object? Input, string Observed, string ObservedBits,
            ulong RawBits)[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var argumentValue = rows[i].Argument;
            object? input;
            switch (argumentValue.ValueType)
            {
                case TsValueType.Null:
                    var explicitNull = Assert.IsType<TsNull>(argumentValue);
                    Assert.Same(TsValue.Null, explicitNull);
                    input = explicitNull.RawValue;
                    Assert.Null(input);
                    break;
                case TsValueType.Void:
                    var explicitUndefined = Assert.IsType<TsVoid>(argumentValue);
                    Assert.Same(TsValue.Void, explicitUndefined);
                    Assert.Null(explicitUndefined.RawValue);
                    input = "undefined";
                    break;
                default:
                    input = Hex(Bits(FloatPayload(argumentValue).Payload));
                    break;
            }

            var returned = new TypeSharp.VM.Interpreter.Interpreter()
                .Execute(restored, restoredMain.Name, [argumentValue]);
            var (outputType, payload) = FloatPayload(returned);
            ulong rawBits = Bits(payload);
            string observedBits = Hex(rawBits);
            string observed = double.IsNaN(payload) ? "NaN" : observedBits;
            observations[i] = (argumentValue.ValueType, input, observed, observedBits, rawBits);
            Write(new
            {
                row = rows[i].Row,
                inputType = argumentValue.ValueType.ToString(),
                input,
                outputType = outputType.ToString(),
                observed,
                observedBits
            });
        }

        const ulong ExponentMask = 0x7FF0000000000000UL;
        const ulong SignificandMask = 0x000FFFFFFFFFFFFFUL;
        bool unchangedControls = true;
        bool validProjections = true;
        for (int i = 0; i < rows.Length; i++)
        {
            var actual = observations[i];
            var expected = rows[i];
            bool bitsAreNaN = (actual.RawBits & ExponentMask) == ExponentMask
                && (actual.RawBits & SignificandMask) != 0UL;
            validProjections &= actual.InputType == expected.ExpectedType
                && Equals(actual.Input, expected.ExpectedInput)
                && actual.Observed == (bitsAreNaN ? "NaN" : actual.ObservedBits);
            if (i != 0)
                unchangedControls &= actual.Observed == expected.ExpectedGreenObserved
                    && actual.ObservedBits == expected.ExpectedGreenBits;
        }

        var nullResult = observations[0];
        bool stock = nullResult.Observed == "NaN" && nullResult.ObservedBits == NaNBits
            && nullResult.RawBits == observations[1].RawBits;
        bool green = nullResult.Observed == rows[0].ExpectedGreenObserved
            && nullResult.ObservedBits == rows[0].ExpectedGreenBits;
        Assert.True(validProjections && unchangedControls && (stock || green),
            "C248_MATH_ROUND_NULL_UNEXPECTED_PROFILE");
        Assert.True(green, "C248_MATH_ROUND_NULL_RESULT expected explicit null to produce Float64 bits 0000000000000000");
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

    private static TsValue Float(ulong bits) =>
        TsValue.FromFloat64(BitConverter.Int64BitsToDouble(unchecked((long)bits)));

    private static (TsValueType Type, double Payload) FloatPayload(TsValue? value)
    {
        var number = Assert.IsType<TsFloat64Value>(value);
        Assert.Equal(TsValueType.Float64, number.ValueType);
        double payload = Assert.IsType<double>(number.RawValue);
        Assert.Equal(Bits(number.Value), Bits(payload));
        return (number.ValueType, payload);
    }

    private static ulong Bits(double value) => unchecked((ulong)BitConverter.DoubleToInt64Bits(value));

    private static string Hex(ulong bits) => bits.ToString("X16", CultureInfo.InvariantCulture);

    private void Write(object record) => output.WriteLine("C248_MATH_ROUND_NULL " + JsonSerializer.Serialize(record));
}
