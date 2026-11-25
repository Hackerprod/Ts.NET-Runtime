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

public sealed class MathLogBlankStringTests(ITestOutputHelper output)
{
    private const string Target = "Math::log";
    private const string NaN = "NaN";
    private const string NegativeInfinity = "-Infinity";
    private const string PositiveZero = "+0";

    [Fact]
    public void Log_ConvertsExplicitEcmaBlankStringToZero()
    {
        const string source = "function main(input: string): number { return Math.log(input); }";
        var tokens = new Lexer(source, "c204-math-log-blank-string.ts").Tokenize();
        Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Error);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.All);
        var main = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", main.Symbol.Name);
        Assert.Same(TsType.Number, main.Symbol.Type);
        var parameter = Assert.Single(main.Symbol.Parameters);
        Assert.Equal("input", parameter.Name);
        Assert.Same(TsType.String, parameter.Type);
        var body = Assert.IsType<BoundBlockStatement>(main.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var boundCall = Assert.IsType<BoundCallExpression>(ret.Value);
        Assert.Same(TsType.Number, boundCall.Type);
        var argument = Assert.IsType<BoundVariableExpression>(Assert.Single(boundCall.Arguments));
        Assert.Same(parameter, argument.Symbol);
        Assert.Same(TsType.String, argument.Type);
        var member = Assert.IsType<BoundMemberAccessExpression>(boundCall.Callee);
        var receiver = Assert.IsType<ClassSymbol>(Assert.IsType<BoundVariableExpression>(member.Object).Symbol);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal("Math", receiver.Name);
        Assert.Equal(Target, $"{method.DeclaringClassName}::{method.RuntimeName ?? method.Name}");
        Assert.Same(TsType.Number, method.Type);

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions, function => function.Name == main.Symbol.Name);
        var irOps = irMain.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.Equal(new[] { Opcode.LoadArg, Opcode.Call, Opcode.Return }, irOps.Select(op => op.Opcode));
        Assert.Equal(0, irOps[0].Operand0);
        Assert.Equal(Target, Assert.IsType<string>(irOps[1].OperandObject));
        Assert.Equal(1, irOps[1].Operand1);

        var compiled = BytecodeCompiler.Compile(ir);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiled);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        var compiledMain = Assert.Single(compiled.Functions, function => function.Name == irMain.Name);
        var restoredMain = Assert.Single(restored.Functions, function => function.Name == irMain.Name);
        Assert.Equal(1, compiledMain.ParameterCount);
        Assert.Equal(1, restoredMain.ParameterCount);
        Assert.Equal(-1, restoredMain.RestParameterIndex);
        Assert.True(compiledMain.Instructions.SequenceEqual(restoredMain.Instructions));
        Assert.Equal(compiled.Functions.Length, restored.Functions.Length);
        for (int index = 0; index < compiled.Functions.Length; index++)
        {
            Assert.Equal(compiled.Functions[index].Name, restored.Functions[index].Name);
            Assert.True(compiled.Functions[index].StringConstants
                .SequenceEqual(restored.Functions[index].StringConstants, StringComparer.Ordinal));
        }
        var (callOffset, targetIndex, argc) = DecodeDirectCall(restoredMain.Instructions);
        Assert.InRange(targetIndex, 0, restoredMain.StringConstants.Length - 1);
        var actualTarget = restoredMain.StringConstants[targetIndex];
        Assert.Equal(Target, actualTarget);
        Assert.Equal(1, argc);

        (string Label, string Input, string Expected)[] rows =
        [
            ("empty", "", NegativeInfinity),
            ("ascii-blank", " \t\v\f\r\n", NegativeInfinity),
            ("mixed-ecma-blank", "\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u202F\u205F\u3000\uFEFF\u2028\u2029", NegativeInfinity),
            ("one", "1", PositiveZero),
            ("negative-zero", "-0", NegativeInfinity),
            ("not-a-number", "not a number", NaN),
            ("nel", "\u0085", NaN),
            ("mixed-not-blank", "\uFEFF\u180E\u200B\u00A0", NaN)
        ];

        var observations = rows.Select(row =>
        {
            var input = new TsStringValue(row.Input);
            var actual = new TypeSharp.VM.Interpreter.Interpreter().Execute(restored, restoredMain.Name, [input]);
            var number = actual as TsFloat64Value;
            return new
            {
                label = row.Label,
                inputUtf16 = string.Concat(input.Value.Select(unit => "\\u" + ((int)unit).ToString("X4", CultureInfo.InvariantCulture))),
                inputType = input.GetType().FullName,
                resultType = actual?.GetType().FullName,
                resultTag = actual?.ValueType.ToString(),
                classification = number is null ? "not-float64" : Classify(number.Value),
                bits = number is null ? null : BitConverter.DoubleToInt64Bits(number.Value).ToString("X16", CultureInfo.InvariantCulture)
            };
        }).ToArray();

        output.WriteLine("C204_MATH_LOG_BLANK_STRING " + JsonSerializer.Serialize(new
        {
            main = restoredMain.Name,
            target = actualTarget,
            argc,
            callOffset,
            targetIndex,
            rows = observations
        }));

        var mismatches = observations.Where((actual, index) =>
            actual.resultType != typeof(TsFloat64Value).FullName
            || actual.resultTag != nameof(TsValueType.Float64)
            || actual.classification != rows[index].Expected)
            .Select(actual => $"{actual.label}={actual.resultType}/{actual.resultTag}/{actual.classification}")
            .ToArray();
        Assert.True(mismatches.Length == 0, "C204_MATH_LOG_BLANK_STRING_MISMATCH " + string.Join(", ", mismatches));
    }

    private static (int Offset, int TargetIndex, int Argc) DecodeDirectCall(byte[] bytes)
    {
        var calls = new List<(int Offset, int TargetIndex, int Argc)>();
        var opcodes = new List<byte>();
        for (int offset = 0; offset < bytes.Length;)
        {
            byte opcode = bytes[offset];
            Assert.True(OpcodeFormats.TryGet(opcode, out var format), $"Unknown opcode at {offset}");
            int width = 1 + format.OperandBytes;
            Assert.True(width <= bytes.Length - offset, $"Truncated opcode at {offset}");
            opcodes.Add(opcode);
            if (opcode == Opcodes.LoadArg)
            {
                Assert.Equal(OperandKind.Int32, format.Operand);
                Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)));
            }
            if (opcode == Opcodes.Call)
            {
                Assert.Equal(OperandKind.Int32x2, format.Operand);
                calls.Add((offset,
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4))));
            }
            offset += width;
        }
        Assert.Equal(new[] { Opcodes.LoadArg, Opcodes.Call, Opcodes.Return }, opcodes);
        return Assert.Single(calls);
    }

    private static string Classify(double value) =>
        double.IsNaN(value) ? NaN :
        double.IsNegativeInfinity(value) ? NegativeInfinity :
        BitConverter.DoubleToInt64Bits(value) == 0 ? PositiveZero :
        "other";
}
