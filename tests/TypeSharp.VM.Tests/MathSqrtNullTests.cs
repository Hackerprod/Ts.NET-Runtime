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

public sealed class MathSqrtNullTests(ITestOutputHelper output)
{
    private const string Source = "function main(value: any): number { return Math.sqrt(value); }";
    private const string Target = "Math::sqrt";

    [Fact]
    public void Sqrt_ExplicitNullReturnsPositiveZero()
    {
        var tokens = new Lexer(Source, "c222-math-sqrt-null.ts").Tokenize();
        var lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.All);

        var boundFunction = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", boundFunction.Symbol.Name);
        Assert.Same(TsType.Number, boundFunction.Symbol.Type);
        var parameter = Assert.Single(boundFunction.Symbol.Parameters);
        Assert.Equal("value", parameter.Name);
        Assert.Same(TsType.Any, parameter.Type);
        var body = Assert.IsType<BoundBlockStatement>(boundFunction.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var boundCall = Assert.IsType<BoundCallExpression>(ret.Value);
        Assert.Same(TsType.Number, boundCall.Type);
        var member = Assert.IsType<BoundMemberAccessExpression>(boundCall.Callee);
        var receiver = Assert.IsType<BoundVariableExpression>(member.Object);
        var math = Assert.IsType<ClassSymbol>(receiver.Symbol);
        Assert.Equal("Math", math.Name);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal(math.Name, method.DeclaringClassName);
        Assert.Equal("sqrt", method.Name);
        Assert.Same(TsType.Number, math.ClassType.Methods[method.Name].ReturnType);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.Number, method.Type);
        var argument = Assert.IsType<BoundVariableExpression>(Assert.Single(boundCall.Arguments));
        Assert.Same(parameter, Assert.IsType<ParameterSymbol>(argument.Symbol));
        Assert.Same(TsType.Any, argument.Type);

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions, function => function.Name == "main");
        Assert.Equal("value", Assert.Single(irMain.Parameters).Name);
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
            retInstruction => Assert.Equal(Opcode.Return, retInstruction.Opcode));

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
        Write(new
        {
            row = "route",
            source = Source,
            lexerErrors,
            parserDiagnostics = parser.Diagnostics.Count,
            binderDiagnostics = binder.Diagnostics.All.Count,
            main = restoredMain.Name,
            parameterCount = restoredMain.ParameterCount,
            route = "LoadArg(0),Call(1),Return",
            target,
            argc
        });

        var undefined = Assert.IsType<TsVoid>(TsValue.Void);
        Assert.Equal(TsValueType.Void, undefined.ValueType);
        Assert.True(double.IsNaN(Run("explicit-undefined", undefined, "undefined", "NaN")));
        CheckNumber("positive-zero", 0d, 0L, Hex(0L));
        CheckNumber("negative-zero", BitConverter.Int64BitsToDouble(long.MinValue), long.MinValue, Hex(long.MinValue));
        CheckNumber("positive-square", 4d, 0x4010000000000000L, Hex(0x4000000000000000L));
        CheckNumber("negative-one", -1d, unchecked((long)0xBFF0000000000000UL), "NaN");
        CheckNumber("positive-infinity", double.PositiveInfinity, 0x7FF0000000000000L, "+Infinity");

        var boolean = Assert.IsType<TsBoolValue>(TsValue.FromBool(true));
        Assert.Equal(TsValueType.Bool, boolean.ValueType);
        Assert.True(boolean.Value);
        Assert.Equal(0x3FF0000000000000L, BitConverter.DoubleToInt64Bits(
            Run("true", boolean, boolean.Value ? "true" : "false", Hex(0x3FF0000000000000L))));

        var text = Assert.IsType<TsStringValue>(TsValue.FromString("9"));
        Assert.Equal(TsValueType.String, text.ValueType);
        Assert.Equal("9", text.Value);
        Assert.Equal(0x4008000000000000L, BitConverter.DoubleToInt64Bits(
            Run("decimal-string", text, text.Value, Hex(0x4008000000000000L))));

        var explicitNull = Assert.IsType<TsNull>(TsValue.Null);
        Assert.Equal(TsValueType.Null, explicitNull.ValueType);
        var actual = Run("explicit-null", explicitNull, "null", Hex(0L));
        var observed = Classify(actual);
        Assert.True(observed == "NaN" || observed == Hex(0L), $"Unexpected null result: {observed}");
        Assert.True(BitConverter.DoubleToInt64Bits(actual) == 0L,
            $"C222_MATH_SQRT_NULL_RESULT expected {Hex(0L)}, observed {observed}");

        void CheckNumber(string label, double value, long inputBits, string expected)
        {
            var input = Assert.IsType<TsFloat64Value>(TsValue.FromFloat64(value));
            Assert.Equal(TsValueType.Float64, input.ValueType);
            var actualInputBits = BitConverter.DoubleToInt64Bits(input.Value);
            Assert.Equal(inputBits, actualInputBits);
            Assert.Equal(expected, Classify(Run(label, input, Hex(actualInputBits), expected)));
        }

        double Run(string label, TsValue input, string inputValue, string expected)
        {
            var result = Assert.IsType<TsFloat64Value>(
                new TypeSharp.VM.Interpreter.Interpreter().Execute(restored, restoredMain.Name, [input]));
            Assert.Equal(TsValueType.Float64, result.ValueType);
            Write(new
            {
                row = label,
                target,
                argc,
                inputWrapper = input.GetType().Name,
                inputTag = input.ValueType.ToString(),
                inputValue,
                resultWrapper = result.GetType().Name,
                resultTag = result.ValueType.ToString(),
                returnedNormally = true,
                observed = Classify(result.Value),
                expected
            });
            return result.Value;
        }
    }

    private static (int TargetIndex, int Argc) DecodeDirectCall(byte[] bytes)
    {
        var sequence = new List<byte>();
        var calls = new List<(int TargetIndex, int Argc)>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var opcode = bytes[offset];
            Assert.True(OpcodeFormats.TryGet(opcode, out var format), $"Unknown opcode at {offset}");
            var width = 1 + format.OperandBytes;
            Assert.True(width <= bytes.Length - offset, $"Truncated opcode at {offset}");
            sequence.Add(opcode);
            if (opcode == Opcodes.LoadArg)
            {
                Assert.Equal(OperandKind.Int32, format.Operand);
                Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)));
            }
            if (opcode == Opcodes.Return)
                Assert.Equal(OperandKind.None, format.Operand);
            if (opcode == Opcodes.Call)
            {
                Assert.Equal(OperandKind.Int32x2, format.Operand);
                calls.Add((
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4))));
            }
            offset += width;
        }
        Assert.Equal(bytes.Length, offset);
        Assert.Equal(new[] { Opcodes.LoadArg, Opcodes.Call, Opcodes.Return }, sequence.ToArray());
        return Assert.Single(calls);
    }

    private static string Classify(double value) =>
        double.IsNaN(value) ? "NaN" :
        double.IsPositiveInfinity(value) ? "+Infinity" :
        Hex(BitConverter.DoubleToInt64Bits(value));

    private static string Hex(long bits) => "0x" + bits.ToString("X16", CultureInfo.InvariantCulture);

    private void Write(object observation) =>
        output.WriteLine("C222_MATH_SQRT_NULL " + JsonSerializer.Serialize(observation));
}
