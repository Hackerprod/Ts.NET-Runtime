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

public sealed class MathSignNullTests(ITestOutputHelper output)
{
    private const string Source = "function main(value: any): number { return Math.sign(value); }";
    private const string Target = "Math::sign";

    [Fact]
    public void Sign_ExplicitNullReturnsPositiveZero()
    {
        var tokens = new Lexer(Source, "c210-math-sign-null.ts").Tokenize();
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
        Assert.Equal("sign", method.Name);
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
        CheckNumber("positive-zero", 0d, 0L, 0L);
        CheckNumber("negative-zero", BitConverter.Int64BitsToDouble(long.MinValue), long.MinValue, long.MinValue);
        CheckNumber("positive-fraction", 2.5d, 0x4004000000000000L, 0x3FF0000000000000L);
        CheckNumber("negative-fraction", -2.5d, unchecked((long)0xC004000000000000UL), unchecked((long)0xBFF0000000000000UL));
        CheckBoolean("false", false, 0L);
        CheckBoolean("true", true, 0x3FF0000000000000L);

        var explicitNull = Assert.IsType<TsNull>(TsValue.Null);
        Assert.Equal(TsValueType.Null, explicitNull.ValueType);
        var actual = Run("explicit-null", explicitNull, "null", Hex(0L));
        Assert.True(BitConverter.DoubleToInt64Bits(actual) == 0L,
            $"C210_MATH_SIGN_NULL_RESULT expected {Hex(0L)}, observed {Classify(actual)}");

        void CheckNumber(string label, double value, long inputBits, long expectedBits)
        {
            var input = Assert.IsType<TsFloat64Value>(TsValue.FromFloat64(value));
            Assert.Equal(TsValueType.Float64, input.ValueType);
            var actualInputBits = BitConverter.DoubleToInt64Bits(input.Value);
            Assert.Equal(inputBits, actualInputBits);
            var result = Run(label, input, Hex(actualInputBits), Hex(expectedBits));
            Assert.Equal(expectedBits, BitConverter.DoubleToInt64Bits(result));
        }

        void CheckBoolean(string label, bool value, long expectedBits)
        {
            var input = Assert.IsType<TsBoolValue>(TsValue.FromBool(value));
            Assert.Equal(TsValueType.Bool, input.ValueType);
            Assert.Equal(value, input.Value);
            var result = Run(label, input, input.Value ? "true" : "false", Hex(expectedBits));
            Assert.Equal(expectedBits, BitConverter.DoubleToInt64Bits(result));
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
        for (int offset = 0; offset < bytes.Length;)
        {
            var opcode = bytes[offset];
            Assert.True(OpcodeFormats.TryGet(opcode, out var format), $"Unknown opcode at {offset}");
            var width = 1 + format.OperandBytes;
            Assert.True(width <= bytes.Length - offset, $"Truncated opcode at {offset}");
            sequence.Add(opcode);
            if (opcode == Opcodes.LoadArg)
                Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)));
            if (opcode == Opcodes.Call)
            {
                Assert.Equal(OperandKind.Int32x2, format.Operand);
                calls.Add((
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4))));
            }
            offset += width;
        }
        Assert.Equal(new[] { Opcodes.LoadArg, Opcodes.Call, Opcodes.Return }, sequence.ToArray());
        return Assert.Single(calls);
    }

    private static string Classify(double value) =>
        double.IsNaN(value) ? "NaN" : Hex(BitConverter.DoubleToInt64Bits(value));

    private static string Hex(long bits) => "0x" + bits.ToString("X16", CultureInfo.InvariantCulture);

    private void Write(object observation) =>
        output.WriteLine("C210_MATH_SIGN_NULL " + JsonSerializer.Serialize(observation));
}
