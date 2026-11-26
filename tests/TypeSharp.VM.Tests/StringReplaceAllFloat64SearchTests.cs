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

public sealed class StringReplaceAllFloat64SearchTests(ITestOutputHelper output)
{
    private const string Prefix = "C242_STRING_REPLACEALL_FLOAT64_SEARCH";
    private const string Target = "String::replaceAll";
    private const string ExpectedHex = "12000000001201000000120200000073000000000300000075";
    private const string Source = """
        function main(text: string, find: any, template: string): string {
            return text.replaceAll(find, template);
        }
        """;

    [Fact]
    public void StringReplaceAll_AppliesReplacementTemplateForFloat64Search()
    {
        var tokens = new Lexer(Source, "c242-string-replaceall-float64-search.ts").Tokenize();
        int lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        int parserDiagnostics = parser.Diagnostics.Count;
        Assert.Equal(0, parserDiagnostics);
        var binder = new Binder();
        var bound = binder.Bind(syntax);
        int binderDiagnostics = binder.Diagnostics.All.Count();
        Assert.Equal(0, binderDiagnostics);

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
        BytecodeVerifier.Verify(compiled);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiled);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        Assert.Equal(stream.Length, stream.Position);
        BytecodeVerifier.Verify(restored);
        var compiledMain = Assert.Single(compiled.Functions, function => function.Name == irMain.Name);
        var restoredMain = Assert.Single(restored.Functions, function => function.Name == irMain.Name);
        Assert.Equal(3, compiledMain.ParameterCount);
        Assert.Equal(3, restoredMain.ParameterCount);
        Assert.Equal(Target, Assert.Single(compiledMain.StringConstants));
        Assert.Equal(Target, Assert.Single(restoredMain.StringConstants));

        DecodeRoute(compiledMain.Instructions);
        var (argumentSlots, targetIndex, argc) = DecodeRoute(restoredMain.Instructions);
        var target = restoredMain.StringConstants[targetIndex];
        string compiledHex = Convert.ToHexString(compiledMain.Instructions);
        string restoredHex = Convert.ToHexString(restoredMain.Instructions);
        Assert.Equal(ExpectedHex, compiledHex);
        Assert.Equal(ExpectedHex, restoredHex);

        output.WriteLine(Prefix + " " + JsonSerializer.Serialize(new
        {
            row = "route",
            lexerErrors,
            parserDiagnostics,
            binderDiagnostics,
            main = restoredMain.Name,
            parameterCount = restoredMain.ParameterCount,
            argumentSlots,
            target,
            argc,
            compiledHex,
            restoredHex
        }));

        string Observe(string row, string text, string templateText)
        {
            TsValue[] args = [TsValue.FromString(text), TsValue.FromFloat64(7d), TsValue.FromString(templateText)];
            var receiver = Assert.IsType<TsStringValue>(args[0]);
            var search = Assert.IsType<TsFloat64Value>(args[1]);
            var template = Assert.IsType<TsStringValue>(args[2]);
            Assert.Equal(TsValueType.String, receiver.ValueType);
            Assert.Equal(TsValueType.Float64, search.ValueType);
            Assert.Equal(TsValueType.String, template.ValueType);
            Assert.Equal(text, receiver.Value);
            Assert.Equal(7d, search.Value);
            Assert.Equal(templateText, template.Value);
            string searchBits = BitConverter.DoubleToInt64Bits(search.Value).ToString("X16", CultureInfo.InvariantCulture);
            Assert.Equal("401C000000000000", searchBits);

            var result = Assert.IsType<TsStringValue>(
                new TypeSharp.VM.Interpreter.Interpreter().Execute(restored, restoredMain.Name, args));
            Assert.Equal(TsValueType.String, result.ValueType);
            output.WriteLine(Prefix + " " + JsonSerializer.Serialize(new
            {
                row,
                receiverTag = receiver.ValueType.ToString(),
                receiver = receiver.Value,
                searchTag = search.ValueType.ToString(),
                searchBits,
                templateTag = template.ValueType.ToString(),
                template = template.Value,
                resultTag = result.ValueType.ToString(),
                observed = result.Value
            }));
            return result.Value;
        }

        string witness = Observe("explicit-match", "price=7/7", "<$&>");
        string literal = Observe("literal-control", "7/7", "ok");
        string noMatch = Observe("no-match-control", "8/8", "<$&>");
        Assert.Equal("ok/ok", literal);
        Assert.Equal("8/8", noMatch);
        const string expected = "price=<7>/<7>";
        Assert.True(string.Equals(expected, witness, StringComparison.Ordinal),
            $"{Prefix}_RESULT expected {expected}, observed {witness}");
    }

    private static void AssertLoadArg(Instruction instruction, int index)
    {
        Assert.Equal(Opcode.LoadArg, instruction.Opcode);
        Assert.Equal(index, instruction.Operand0);
    }

    private static (int[] ArgumentSlots, int TargetIndex, int Argc) DecodeRoute(byte[] bytes)
    {
        Assert.Equal(25, bytes.Length);
        var slots = new int[3];
        for (int i = 0; i < slots.Length; i++)
        {
            int offset = i * 5;
            Assert.Equal(Opcodes.LoadArg, bytes[offset]);
            slots[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4));
            Assert.Equal(i, slots[i]);
        }

        Assert.Equal(Opcodes.Call, bytes[15]);
        int targetIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16, 4));
        int argc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20, 4));
        Assert.Equal(0, targetIndex);
        Assert.Equal(3, argc);
        Assert.Equal(Opcodes.Return, bytes[24]);
        return (slots, targetIndex, argc);
    }
}
