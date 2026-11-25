using System.Buffers.Binary;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringReplaceAllInt32SearchTests(ITestOutputHelper output)
{
    private const string Target = "String::replaceAll";
    private const string Source = """
        function main(text: string, find: any, template: string): string {
            return text.replaceAll(find, template);
        }
        """;

    [Fact]
    public void StringReplaceAll_AppliesReplacementTemplateForInt32Search()
    {
        (string Label, string Text, string Template, string Expected)[] rows =
        [
            ("match-each", "7/7", "<$&>", "<7>/<7>"),
            ("literal-dollar-and-match", "7/7", "[$$:$&]", "[$:7]/[$:7]"),
            ("literal-control", "7/7", "ok", "ok/ok"),
            ("no-match-control", "8/8", "<$&>", "8/8")
        ];

        var tokens = new Lexer(Source, "c207-string-replaceall-int32-search.ts").Tokenize();
        Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Error);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions, function => function.Name == "main");
        Assert.Equal(new[] { "text", "find", "template" }, irMain.Parameters.Select(p => p.Name));
        Assert.Collection(Assert.Single(irMain.Blocks).Instructions,
            first => AssertLoadArg(first, 0),
            second => AssertLoadArg(second, 1),
            third => AssertLoadArg(third, 2),
            call =>
            {
                Assert.Equal(Opcode.Call, call.Opcode);
                Assert.Equal(Target, Assert.IsType<string>(call.OperandObject));
                Assert.Equal(3, call.Operand1);
            },
            ret => Assert.Equal(Opcode.Return, ret.Opcode));

        var compiled = BytecodeCompiler.Compile(ir);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiled);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        var compiledMain = Assert.Single(compiled.Functions, function => function.Name == irMain.Name);
        var restoredMain = Assert.Single(restored.Functions, function => function.Name == irMain.Name);
        Assert.Equal(3, compiledMain.ParameterCount);
        Assert.Equal(3, restoredMain.ParameterCount);
        Assert.Equal(compiledMain.Instructions, restoredMain.Instructions);
        Assert.Equal(compiledMain.StringConstants, restoredMain.StringConstants);

        var (targetIndex, argc) = DecodeDirectCall(restoredMain.Instructions);
        Assert.InRange(targetIndex, 0, restoredMain.StringConstants.Length - 1);
        var target = restoredMain.StringConstants[targetIndex];
        Assert.Equal(Target, target);
        Assert.Equal(3, argc);

        var observations = rows.Select(row =>
        {
            TsValue[] args = [TsValue.FromString(row.Text), TsValue.FromInt32(7), TsValue.FromString(row.Template)];
            var receiver = Assert.IsType<TsStringValue>(args[0]);
            var search = Assert.IsType<TsInt32Value>(args[1]);
            var replacement = Assert.IsType<TsStringValue>(args[2]);
            Assert.Equal(TsValueType.String, receiver.ValueType);
            Assert.Equal(TsValueType.Int32, search.ValueType);
            Assert.Equal(TsValueType.String, replacement.ValueType);
            Assert.Equal(row.Text, receiver.Value);
            Assert.Equal(7, search.Value);
            Assert.Equal(row.Template, replacement.Value);

            var result = Assert.IsType<TsStringValue>(
                new TypeSharp.VM.Interpreter.Interpreter().Execute(restored, restoredMain.Name, args));
            Assert.Equal(TsValueType.String, result.ValueType);
            return new
            {
                label = row.Label,
                receiver = new { type = receiver.GetType().FullName, tag = receiver.ValueType.ToString(), value = receiver.Value },
                search = new { type = search.GetType().FullName, tag = search.ValueType.ToString(), value = search.Value },
                replacement = new { type = replacement.GetType().FullName, tag = replacement.ValueType.ToString(), value = replacement.Value },
                exception = "none",
                resultType = result.GetType().FullName,
                resultTag = result.ValueType.ToString(),
                resultValue = result.Value,
                expected = row.Expected
            };
        }).ToArray();

        output.WriteLine("C207_STRING_REPLACEALL_INT32_SEARCH " + JsonSerializer.Serialize(new
        {
            source = Source, lexerErrors = 0, parserDiagnostics = 0, binderDiagnostics = 0,
            main = restoredMain.Name, target, argc, rows = observations
        }));

        var mismatches = observations
            .Where(row => !string.Equals(row.expected, row.resultValue, StringComparison.Ordinal))
            .Select(row => $"[{row.label}] expected {row.expected}, actual {row.resultValue}")
            .ToArray();
        Assert.True(mismatches.Length == 0,
            "C207_STRING_REPLACEALL_INT32_SEARCH_MISMATCH " + string.Join("; ", mismatches));
    }

    private static void AssertLoadArg(Instruction instruction, int index)
    {
        Assert.Equal(Opcode.LoadArg, instruction.Opcode);
        Assert.Equal(index, instruction.Operand0);
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
