using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringConcatSignedZeroTests(ITestOutputHelper output)
{
    private const string Target = "String::concat";
    private const string Source = """
        function main(x: any): string {
            const prefix: string = "prefix:";
            return prefix.concat(x, ":end");
        }
        """;

    [Fact]
    public void Concat_FormatsDirectSignedZeroOperands()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            (string Label, TsValue Input, Type Type, TsValueType Tag, string Payload, string Expected)[] rows =
            [
                ("f64-negative-zero", new TsFloat64Value(BitConverter.Int64BitsToDouble(long.MinValue)),
                    typeof(TsFloat64Value), TsValueType.Float64, "8000000000000000", "prefix:0:end"),
                ("f32-negative-zero", new TsFloat32Value(BitConverter.Int32BitsToSingle(int.MinValue)),
                    typeof(TsFloat32Value), TsValueType.Float32, "80000000", "prefix:0:end"),
                ("f64-positive-zero", new TsFloat64Value(BitConverter.Int64BitsToDouble(0)),
                    typeof(TsFloat64Value), TsValueType.Float64, "0000000000000000", "prefix:0:end"),
                ("f32-positive-zero", new TsFloat32Value(BitConverter.Int32BitsToSingle(0)),
                    typeof(TsFloat32Value), TsValueType.Float32, "00000000", "prefix:0:end"),
                ("f64-negative-finite", new TsFloat64Value(-3d),
                    typeof(TsFloat64Value), TsValueType.Float64, "c008000000000000", "prefix:-3:end"),
                ("f32-negative-finite", new TsFloat32Value(-3f),
                    typeof(TsFloat32Value), TsValueType.Float32, "c0400000", "prefix:-3:end"),
                ("text-negative-zero", new TsStringValue("-0"),
                    typeof(TsStringValue), TsValueType.String, "-0", "prefix:-0:end")
            ];

            var tokens = new Lexer(Source, "c206-string-concat-signed-zero.ts").Tokenize();
            Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Error);
            var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
            var syntax = parser.Parse();
            Assert.Empty(parser.Diagnostics);
            var binder = new Binder();
            var bound = binder.Bind(syntax);
            Assert.Empty(binder.Diagnostics.All);

            var ir = new IRGenerator().Generate(bound);
            var irMain = Assert.Single(ir.Functions, function => function.Name == "main");
            Assert.Equal("x", Assert.Single(irMain.Parameters).Name);
            var irCall = Assert.Single(irMain.Blocks.SelectMany(block => block.Instructions),
                instruction => instruction.Opcode == Opcode.Call);
            Assert.Equal(Target, Assert.IsType<string>(irCall.OperandObject));
            Assert.Equal(3, irCall.Operand1);

            var compiled = BytecodeCompiler.Compile(ir);
            using var stream = new MemoryStream();
            BytecodeSerializer.Serialize(stream, compiled);
            stream.Position = 0;
            var restored = BytecodeSerializer.Deserialize(stream);
            var compiledMain = Assert.Single(compiled.Functions, function => function.Name == irMain.Name);
            var restoredMain = Assert.Single(restored.Functions, function => function.Name == irMain.Name);
            Assert.Equal(1, compiledMain.ParameterCount);
            Assert.Equal(1, restoredMain.ParameterCount);
            Assert.Equal(compiledMain.Instructions, restoredMain.Instructions);
            Assert.Equal(compiledMain.StringConstants, restoredMain.StringConstants);

            var (targetIndex, argc) = DecodeDirectCall(restoredMain.Instructions);
            Assert.InRange(targetIndex, 0, restoredMain.StringConstants.Length - 1);
            var target = restoredMain.StringConstants[targetIndex];
            Assert.Equal(Target, target);
            Assert.Equal(3, argc);

            var observations = rows.Select(row =>
            {
                TsValue[] args = [row.Input];
                object Snapshot()
                {
                    var slot = Assert.Single(args);
                    Assert.Same(row.Input, slot);
                    Assert.Equal(row.Type, slot.GetType());
                    Assert.Equal(row.Tag, slot.ValueType);
                    string value = slot switch
                    {
                        TsFloat64Value f64 => BitConverter.DoubleToInt64Bits(f64.Value).ToString("x16", CultureInfo.InvariantCulture),
                        TsFloat32Value f32 => BitConverter.SingleToInt32Bits(f32.Value).ToString("x8", CultureInfo.InvariantCulture),
                        TsStringValue str => str.Value,
                        _ => throw new InvalidOperationException("Unexpected fixture input type")
                    };
                    Assert.Equal(row.Payload, value);
                    return new { type = slot.GetType().FullName, tag = slot.ValueType.ToString(), value, sameReference = true };
                }

                var before = Snapshot();
                var result = Assert.IsType<TsStringValue>(
                    new TypeSharp.VM.Interpreter.Interpreter().Execute(restored, restoredMain.Name, args));
                Assert.Equal(TsValueType.String, result.ValueType);
                var after = Snapshot();
                return new
                {
                    label = row.Label,
                    before,
                    after,
                    resultType = result.GetType().FullName,
                    resultTag = result.ValueType.ToString(),
                    resultValue = result.Value,
                    expected = row.Expected
                };
            }).ToArray();

            output.WriteLine("C206_STRING_CONCAT_SIGNED_ZERO " + JsonSerializer.Serialize(new
            {
                main = restoredMain.Name, target, argc, rows = observations
            }));

            var mismatches = observations
                .Where(row => !string.Equals(row.expected, row.resultValue, StringComparison.Ordinal))
                .Select(row => $"[{row.label}] expected {row.expected}, actual {row.resultValue}")
                .ToArray();
            Assert.True(mismatches.Length == 0,
                "C206_STRING_CONCAT_SIGNED_ZERO_MISMATCH " + string.Join("; ", mismatches));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
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
}
