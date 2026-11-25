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

public sealed class MathCbrtNullTests(ITestOutputHelper output)
{
    private const string Source = "function main(value: any): number { return Math.cbrt(value); }";
    private const string File = "c235-math-cbrt-null.ts";
    private const string Target = "Math::cbrt";
    private const string PositiveZero = "0000000000000000";

    [Fact]
    public void Cbrt_ExplicitNullReturnsPositiveZero()
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
        Assert.Equal("cbrt", method.Name);
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
            source = Source,
            file = File,
            lexerErrors,
            parserDiagnostics = parser.Diagnostics.Count,
            binderDiagnostics = binder.Diagnostics.All.Count(),
            main = function.Symbol.Name,
            mainType = function.Symbol.Type.ToString(),
            parameter = parameter.Name,
            parameterType = parameter.Type.ToString(),
            argument = argumentSymbol.Name,
            argumentType = argument.Type.ToString(),
            sameArgumentSymbol = ReferenceEquals(parameter, argumentSymbol),
            receiverWrapper = math.GetType().Name,
            receiver = math.Name,
            methodWrapper = method.GetType().Name,
            method = method.Name,
            declaringClass = method.DeclaringClassName,
            methodStatic = method.IsStatic,
            methodDynamic = method.HasDynamicSignature,
            methodType = method.Type.ToString(),
            signatureType = signature.ReturnType.ToString(),
            callType = call.Type.ToString(),
            boundTarget,
            irSequence = instructions.Select(instruction => instruction.Opcode.ToString()).ToArray(),
            irArgumentSlot = instructions[0].Operand0,
            irTarget,
            irArgumentCount = instructions[1].Operand1,
            compiledMain = compiledMain.Name,
            compiledParameterCount = compiledMain.ParameterCount,
            compiledStringConstants = compiledMain.StringConstants,
            compiledRoute,
            restoredMain = restoredMain.Name,
            restoredParameterCount = restoredMain.ParameterCount,
            restoredStringConstants = restoredMain.StringConstants,
            restoredRoute
        });

        var undefined = Assert.IsType<TsVoid>(TsValue.Void);
        Assert.Equal(TsValueType.Void, undefined.ValueType);
        Run("undefined", undefined, "undefined", "NaN", value =>
        {
            Assert.True(double.IsNaN(value));
            return "NaN";
        });
        var positiveZero = Number(0d, 0L);
        Run("positive-zero", positiveZero, Hex(Bits(positiveZero.Value)), PositiveZero, value => Exact(value, 0L));
        var negativeZero = Number(BitConverter.Int64BitsToDouble(long.MinValue), long.MinValue);
        Run("negative-zero", negativeZero, Hex(Bits(negativeZero.Value)), "8000000000000000",
            value => Exact(value, long.MinValue));
        var positiveCube = Number(8d, 0x4020000000000000L);
        Run("positive-cube", positiveCube, Hex(Bits(positiveCube.Value)), "2.0000000000", value => Finite(value, 2d));
        var negativeCube = Number(-8d, unchecked((long)0xC020000000000000UL));
        Run("negative-cube", negativeCube, Hex(Bits(negativeCube.Value)), "-2.0000000000", value => Finite(value, -2d));
        var decimalText = Assert.IsType<TsStringValue>(TsValue.FromString("8"));
        Assert.Equal(TsValueType.String, decimalText.ValueType);
        Assert.Equal("8", decimalText.Value);
        Run("decimal", decimalText, decimalText.Value, "2.0000000000", value => Finite(value, 2d));
        var boolean = Assert.IsType<TsBoolValue>(TsValue.FromBool(true));
        Assert.Equal(TsValueType.Bool, boolean.ValueType);
        Assert.True(boolean.Value);
        Run("true", boolean, boolean.Value, "1.0000000000", value => Finite(value, 1d));
        var explicitNull = Assert.IsType<TsNull>(TsValue.Null);
        Assert.Same(TsValue.Null, explicitNull);
        Assert.Equal(TsValueType.Null, explicitNull.ValueType);
        Assert.Null(explicitNull.RawValue);
        var (actual, observed) = Run("explicit-null", explicitNull, explicitNull.RawValue, PositiveZero, value =>
        {
            if (double.IsNaN(value))
                return "NaN";
            return Exact(value, 0L);
        });
        Assert.True(!double.IsNaN(actual) && Bits(actual) == 0L,
            "C235_MATH_CBRT_NULL_RESULT expected 0000000000000000, observed " + observed);

        (double Value, string Observed) Run(string row, TsValue input, object? inputValue, string expected,
            Func<double, string> project)
        {
            TsValue? returned = null;
            var escaped = Record.Exception(() =>
            {
                returned = new TypeSharp.VM.Interpreter.Interpreter().Execute(restored, restoredMain.Name, [input]);
            });
            Assert.Null(escaped);
            var result = Assert.IsType<TsFloat64Value>(returned);
            Assert.Equal(TsValueType.Float64, result.ValueType);
            string observedValue = project(result.Value);
            Write(new
            {
                row,
                target = restoredRoute.target,
                argc = restoredRoute.argc,
                inputWrapper = input.GetType().Name,
                inputTag = input.ValueType.ToString(),
                inputValue,
                returnedNormally = escaped is null,
                resultWrapper = result.GetType().Name,
                resultTag = result.ValueType.ToString(),
                observed = observedValue,
                expected
            });
            return (result.Value, observedValue);
        }
    }

    private sealed record Route(string hex, int length, byte loadOpcode, byte callOpcode, byte returnOpcode,
        int argumentSlot, int targetIndex, string target, int argc);

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
        return new(hex, bytes.Length, bytes[0], bytes[5], bytes[14], slot, targetIndex, target, argc);
    }

    private static TsFloat64Value Number(double value, long expectedBits)
    {
        var input = Assert.IsType<TsFloat64Value>(TsValue.FromFloat64(value));
        Assert.Equal(TsValueType.Float64, input.ValueType);
        Assert.Equal(expectedBits, Bits(input.Value));
        return input;
    }

    private static string Exact(double value, long expectedBits)
    {
        long bits = Bits(value);
        Assert.Equal(expectedBits, bits);
        return Hex(bits);
    }

    private static string Finite(double value, double expected)
    {
        Assert.True(double.IsFinite(value));
        Assert.InRange(Math.Abs(value - expected), 0d, 1e-12);
        return value.ToString("F10", CultureInfo.InvariantCulture);
    }

    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);

    private static string Hex(long bits) => bits.ToString("X16", CultureInfo.InvariantCulture);

    private void Write(object record) => output.WriteLine("C235_MATH_CBRT_NULL " + JsonSerializer.Serialize(record));
}
